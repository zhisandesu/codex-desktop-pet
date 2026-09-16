using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

/// <summary>
/// Read/write bridge to the app-tool MCP server owned by the running Codex desktop app.
/// Tool text is always handled as untrusted data and is never executed by this class.
/// </summary>
public sealed class CodexDesktopBridge : IAsyncDisposable
{
    private sealed record DesktopEndpoint(string PipePath, string PluginDirectory, string? NodePath);

    private sealed class PendingRequest
    {
        public TaskCompletionSource<JsonObject> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] RequiredTools =
    [
        "list_threads",
        "read_thread",
        "send_message_to_thread",
        "navigate_to_codex_page"
    ];

    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _stderrGate = new();
    private readonly Queue<string> _stderrTail = new();

    private Process? _process;
    private StreamWriter? _writer;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private string? _anchorTaskId;
    private string? _lastError;
    private bool _connected;
    private bool _disposed;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
            {
                return _connected && _process is { HasExited: false };
            }
        }
    }

    public string? AnchorTaskId
    {
        get
        {
            lock (_stateGate)
            {
                return _anchorTaskId;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastError;
            }
        }
    }

    public async Task ConnectAsync(
        IEnumerable<string> taskIdCandidates,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(taskIdCandidates);

        var anchors = taskIdCandidates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var environmentAnchor = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        if (!string.IsNullOrWhiteSpace(environmentAnchor) && !anchors.Contains(environmentAnchor, StringComparer.Ordinal))
        {
            anchors.Add(environmentAnchor);
        }

        if (anchors.Count == 0)
        {
            throw new ArgumentException("至少需要一个真实的 Codex 任务 ID 作为桌面桥接锚点。", nameof(taskIdCandidates));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
            SetState(connected: false, anchorTaskId: null, error: null);

            var endpoints = DiscoverEndpoints();
            if (endpoints.Count == 0)
            {
                throw new CodexDesktopBridgeException(
                    "没有发现正在运行的官方 Codex App Tools 管道。请先启动 Codex 桌面应用。");
            }

            var failures = new List<string>();
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await StartAndInitializeAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    var tools = await ListToolNamesAsync(cancellationToken).ConfigureAwait(false);
                    var missing = RequiredTools.Where(tool => !tools.Contains(tool, StringComparer.Ordinal)).ToArray();
                    if (missing.Length > 0)
                    {
                        throw new CodexDesktopBridgeException($"官方 Codex 缺少所需工具：{string.Join("、", missing)}");
                    }

                    foreach (var anchor in anchors)
                    {
                        try
                        {
                            var probe = await CallToolWithAnchorAsync(
                                    "list_threads",
                                    new JsonObject { ["limit"] = 1 },
                                    anchor,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (probe.IsError || probe.JsonData is not JsonObject probeData ||
                                probeData["threads"] is not JsonArray)
                            {
                                failures.Add($"锚点 {anchor} 未返回有效任务列表");
                                continue;
                            }

                            SetState(connected: true, anchorTaskId: anchor, error: null);
                            return;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failures.Add($"锚点 {anchor}：{ex.Message}");
                        }
                    }

                    throw new CodexDesktopBridgeException("当前官方 Codex 不接受提供的任务锚点。");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{endpoint.PipePath}：{ex.Message}");
                    await StopProcessAsync().ConfigureAwait(false);
                }
            }

            var detail = failures.Count == 0
                ? "未知连接错误"
                : string.Join(Environment.NewLine, failures.Take(8));
            throw new CodexDesktopBridgeException($"无法连接官方 Codex App Tools。{Environment.NewLine}{detail}");
        }
        catch (Exception ex)
        {
            SetState(connected: false, anchorTaskId: null, error: ex.Message);
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexDesktopTask>> ListTasksAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "任务数量必须大于零。");
        }

        // The desktop tool currently caps non-pinned results at 50 and exposes no cursor.
        var result = await CallToolAsync(
                "list_threads",
                new JsonObject { ["limit"] = Math.Min(limit, 50) },
                cancellationToken)
            .ConfigureAwait(false);
        var data = RequireToolData(result, "list_threads");
        if (data["threads"] is not JsonArray && data["pinnedThreads"] is not JsonArray)
        {
            throw new CodexDesktopBridgeException("官方 Codex 返回的任务列表缺少 threads/pinnedThreads 字段。");
        }

        var parsed = new List<CodexDesktopTask>();
        foreach (var array in new[] { data["pinnedThreads"] as JsonArray, data["threads"] as JsonArray })
        {
            if (array is null)
            {
                continue;
            }

            foreach (var node in array)
            {
                if (node is JsonObject task && TryParseTask(task, out var parsedTask))
                {
                    parsed.Add(parsedTask);
                }
            }
        }

        return parsed
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(task => task.UpdatedAt)
            .Take(limit)
            .ToArray();
    }

    public async Task<CodexDesktopTaskDetail> ReadTaskAsync(
        string threadId,
        string? hostId = null,
        int turnLimit = 3,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (turnLimit is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(turnLimit), "turnLimit 必须在 1 到 10 之间。");
        }

        var arguments = new JsonObject
        {
            ["threadId"] = threadId,
            ["turnLimit"] = turnLimit,
            ["includeOutputs"] = true,
            ["maxOutputCharsPerItem"] = 1200
        };
        if (!string.IsNullOrWhiteSpace(hostId))
        {
            arguments["hostId"] = hostId;
        }

        var result = await CallToolAsync("read_thread", arguments, cancellationToken).ConfigureAwait(false);
        var data = RequireToolData(result, "read_thread");
        if (data["thread"] is not JsonObject thread)
        {
            throw new CodexDesktopBridgeException("官方 Codex 返回的任务详情缺少 thread 字段。");
        }

        var statusNode = thread["status"];
        var status = ReadStatus(statusNode);
        var activeFlags = ReadActiveFlags(statusNode);
        var turns = ParseTurns(data["turns"] as JsonArray);
        var currentTurn = turns.FirstOrDefault(turn =>
                              turn.Status.Equals("inProgress", StringComparison.OrdinalIgnoreCase))
                          ?? turns.FirstOrDefault();
        var currentOperation = currentTurn?.Operation ?? OperationFromFlags(activeFlags, status, currentTurn?.Status);
        var title = FirstNonEmpty(ReadString(thread, "title"), "未命名任务")!;

        return new CodexDesktopTaskDetail(
            ReadString(thread, "id") ?? threadId,
            title,
            ReadString(thread, "summary") ?? ReadString(thread, "preview"),
            status,
            ReadString(thread, "hostId") ?? hostId,
            ReadString(thread, "cwd"),
            ReadLong(thread, "updatedAt") ?? 0,
            ReadString(thread, "projectId"),
            activeFlags,
            currentTurn?.Status,
            currentOperation,
            currentTurn?.CurrentActivityKey,
            currentTurn?.Id,
            turns);
    }

    public Task<CodexDesktopToolResult> SendMessageAsync(
        string threadId,
        string? hostId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var arguments = new JsonObject
        {
            ["threadId"] = threadId,
            ["prompt"] = prompt
        };
        if (!string.IsNullOrWhiteSpace(hostId))
        {
            arguments["hostId"] = hostId;
        }

        return CallToolAsync("send_message_to_thread", arguments, cancellationToken);
    }

    public Task<CodexDesktopToolResult> NavigateToTaskAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return CallToolAsync(
            "navigate_to_codex_page",
            new JsonObject { ["threadId"] = threadId },
            cancellationToken);
    }

    public Task<CodexDesktopToolResult> CallToolAsync(
        string toolName,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var anchor = AnchorTaskId
                     ?? throw new CodexDesktopBridgeException("官方 Codex 桥接缺少有效任务锚点。");
        return CallToolWithAnchorAsync(toolName, arguments ?? new JsonObject(), anchor, cancellationToken);
    }

    private async Task StartAndInitializeAsync(DesktopEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(endpoint.PluginDirectory))
        {
            throw new DirectoryNotFoundException($"Codex App Tools 目录不存在：{endpoint.PluginDirectory}");
        }

        var launcher = Path.Combine(endpoint.PluginDirectory, "scripts", "launch_codex_app_tools_mcp.cmd");
        var server = Path.Combine(endpoint.PluginDirectory, "server.mjs");
        if (!File.Exists(launcher) || !File.Exists(server))
        {
            throw new FileNotFoundException("Codex App Tools 缺少启动脚本或 server.mjs。", endpoint.PluginDirectory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = endpoint.PluginDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom
        };
        foreach (var argument in new[]
                 {
                     "/d", "/s", "/c", "call",
                     "./scripts/launch_codex_app_tools_mcp.cmd", "./server.mjs"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CODEX_APP_TOOLS_PIPE_PATH"] = endpoint.PipePath;
        if (!string.IsNullOrWhiteSpace(endpoint.NodePath))
        {
            startInfo.Environment["CODEX_MCP_NODE_PATH"] = endpoint.NodePath;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new CodexDesktopBridgeException("Codex App Tools MCP 代理未能启动。");
        }

        _process = process;
        _writer = process.StandardInput;
        _connectionCancellation = new CancellationTokenSource();
        _stdoutTask = ReadStdoutAsync(process, _connectionCancellation.Token);
        _stderrTask = ReadStderrAsync(process, _connectionCancellation.Token);

        var initialize = await SendRequestAsync(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "xiaobian-pet",
                        ["title"] = "柯朵桌宠",
                        ["version"] = "0.2.0"
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        var protocolVersion = ReadString(initialize, "protocolVersion");
        if (!string.Equals(protocolVersion, "2025-06-18", StringComparison.Ordinal))
        {
            throw new CodexDesktopBridgeException($"Codex App Tools 协议版本不兼容：{protocolVersion ?? "未知"}");
        }

        await SendNotificationAsync("notifications/initialized", new JsonObject(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HashSet<string>> ListToolNamesAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync("tools/list", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (response["tools"] is JsonArray tools)
        {
            foreach (var node in tools.OfType<JsonObject>())
            {
                var name = ReadString(node, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private async Task<CodexDesktopToolResult> CallToolWithAnchorAsync(
        string toolName,
        JsonObject arguments,
        string anchorTaskId,
        CancellationToken cancellationToken)
    {
        var callToken = Guid.NewGuid().ToString("N");
        var response = await SendRequestAsync(
                "tools/call",
                new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments.DeepClone(),
                    ["_meta"] = new JsonObject
                    {
                        ["openai/threadId"] = anchorTaskId,
                        ["openai/turnId"] = $"xiaobian-pet-{callToken}",
                        ["openai/toolCallId"] = $"xiaobian-pet-call-{callToken}"
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        var textBlocks = new List<string>();
        if (response["content"] is JsonArray content)
        {
            foreach (var block in content.OfType<JsonObject>())
            {
                if (string.Equals(ReadString(block, "type"), "text", StringComparison.Ordinal) &&
                    ReadString(block, "text") is { } text)
                {
                    textBlocks.Add(text);
                }
            }
        }

        JsonNode? jsonData = response["structuredContent"]?.DeepClone();
        if (jsonData is null)
        {
            foreach (var text in textBlocks)
            {
                try
                {
                    jsonData = JsonNode.Parse(text);
                    if (jsonData is not null)
                    {
                        break;
                    }
                }
                catch
                {
                    // Tool text is untrusted data; non-JSON text remains in TextBlocks only.
                }
            }
        }

        return new CodexDesktopToolResult(
            ReadBoolean(response, "isError") ?? false,
            textBlocks,
            jsonData,
            (JsonObject)response.DeepClone());
    }

    private async Task<JsonObject> SendRequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        EnsureProcessRunning();
        var id = Interlocked.Increment(ref _nextRequestId);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new PendingRequest();
        if (!_pending.TryAdd(key, pending))
        {
            throw new CodexDesktopBridgeException("无法登记 MCP 请求。");
        }

        try
        {
            await WriteMessageAsync(
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["method"] = method,
                        ["params"] = parameters
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (RequestTimeout != Timeout.InfiniteTimeSpan)
            {
                timeout.CancelAfter(RequestTimeout);
            }

            try
            {
                return await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Codex App Tools 请求超时：{method}");
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters
            },
            cancellationToken);

    private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new CodexDesktopBridgeException("Codex App Tools 输入流不可用。");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetDisconnected($"写入 Codex App Tools 失败：{ex.Message}");
            throw new CodexDesktopBridgeException("写入 Codex App Tools 失败。", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(Process owner, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await owner.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (Exception ex)
                {
                    throw new CodexDesktopBridgeException("Codex App Tools 返回了无效 JSON。", ex);
                }

                if (message is null || message["id"] is null)
                {
                    continue;
                }

                var key = NodeAsString(message["id"]);
                if (string.IsNullOrWhiteSpace(key) || !_pending.TryRemove(key, out var pending))
                {
                    continue;
                }

                if (message["error"] is JsonObject error)
                {
                    var code = ReadLong(error, "code");
                    var errorMessage = ReadString(error, "message") ?? "未知 MCP 错误";
                    pending.Completion.TrySetException(
                        new CodexDesktopBridgeException($"Codex App Tools 错误 {code?.ToString() ?? ""}：{errorMessage}"));
                    continue;
                }

                if (message["result"] is JsonObject result)
                {
                    pending.Completion.TrySetResult((JsonObject)result.DeepClone());
                }
                else
                {
                    pending.Completion.TrySetException(
                        new CodexDesktopBridgeException("Codex App Tools 响应缺少 result。"));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                var message = failure?.Message ?? BuildProcessExitMessage(owner);
                SetDisconnected(message);
                FailPending(new CodexDesktopBridgeException(message, failure ?? new EndOfStreamException(message)));
            }
        }
    }

    private async Task ReadStderrAsync(Process owner, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await owner.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lock (_stderrGate)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 20)
                    {
                        _stderrTail.Dequeue();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // stderr is diagnostic only; stdout owns the connection lifecycle.
        }
    }

    private string BuildProcessExitMessage(Process process)
    {
        string stderr;
        lock (_stderrGate)
        {
            stderr = string.Join(Environment.NewLine, _stderrTail);
        }

        var exit = process.HasExited ? $"退出码 {process.ExitCode}" : "输出流已关闭";
        return string.IsNullOrWhiteSpace(stderr)
            ? $"Codex App Tools 已断开（{exit}）。"
            : $"Codex App Tools 已断开（{exit}）：{stderr}";
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        var writer = _writer;
        var cancellation = _connectionCancellation;
        var stdout = _stdoutTask;
        var stderr = _stderrTask;

        _process = null;
        _writer = null;
        _connectionCancellation = null;
        _stdoutTask = null;
        _stderrTask = null;
        cancellation?.Cancel();

        try
        {
            writer?.Close();
        }
        catch
        {
        }

        if (process is not null)
        {
            try
            {
                using var wait = new CancellationTokenSource(ShutdownTimeout);
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            }
        }

        if (stdout is not null || stderr is not null)
        {
            try
            {
                await Task.WhenAll(new[] { stdout, stderr }.Where(task => task is not null).Cast<Task>())
                    .WaitAsync(ShutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }

        writer?.Dispose();
        cancellation?.Dispose();
        process?.Dispose();
        FailPending(new CodexDesktopBridgeException("Codex App Tools 连接已关闭。"));
        lock (_stateGate)
        {
            _connected = false;
            _anchorTaskId = null;
        }
    }

    private static IReadOnlyList<DesktopEndpoint> DiscoverEndpoints()
    {
        var commandLines = ReadOfficialCodexCommandLines();
        var discovered = new List<DesktopEndpoint>();
        foreach (var commandLine in commandLines.Where(line => line.Contains("codex-app-tools", StringComparison.OrdinalIgnoreCase)))
        {
            var pipe = ExtractEscapedAssignment(commandLine, "CODEX_APP_TOOLS_PIPE_PATH");
            var node = ExtractEscapedAssignment(commandLine, "CODEX_MCP_NODE_PATH");
            var cwd = ExtractEscapedAssignment(commandLine, "cwd");
            if (!string.IsNullOrWhiteSpace(pipe) && !string.IsNullOrWhiteSpace(cwd))
            {
                discovered.Add(new DesktopEndpoint(pipe, cwd, node));
            }
        }

        var environmentPipe = Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPipe))
        {
            var environmentNode = Environment.GetEnvironmentVariable("CODEX_MCP_NODE_PATH");
            var matching = discovered.FirstOrDefault(endpoint =>
                string.Equals(endpoint.PipePath, environmentPipe, StringComparison.OrdinalIgnoreCase));
            var pluginDirectory = matching?.PluginDirectory ?? FindPluginDirectoryFromEnvironment() ?? FindInstalledPluginDirectory();
            if (!string.IsNullOrWhiteSpace(pluginDirectory))
            {
                discovered.Insert(
                    0,
                    new DesktopEndpoint(
                        environmentPipe,
                        pluginDirectory,
                        environmentNode ?? matching?.NodePath));
            }
        }

        var fallbackPlugin = discovered.Select(endpoint => endpoint.PluginDirectory).FirstOrDefault(Directory.Exists)
                             ?? FindPluginDirectoryFromEnvironment()
                             ?? FindInstalledPluginDirectory();
        var fallbackNode = Environment.GetEnvironmentVariable("CODEX_MCP_NODE_PATH")
                           ?? discovered.Select(endpoint => endpoint.NodePath).FirstOrDefault(File.Exists)
                           ?? FindInstalledNodeRuntime();
        if (!string.IsNullOrWhiteSpace(fallbackPlugin))
        {
            foreach (var pipe in EnumerateCodexPipeCandidates())
            {
                discovered.Add(new DesktopEndpoint(pipe, fallbackPlugin, fallbackNode));
            }
        }

        return discovered
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.PipePath) &&
                               CodexDesktopInstallation.IsPluginDirectory(endpoint.PluginDirectory))
            .DistinctBy(endpoint => $"{endpoint.PipePath}|{endpoint.PluginDirectory}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadOfficialCodexCommandLines()
    {
        var lines = new List<string>();
        object? locatorObject = null;
        object? servicesObject = null;
        object? resultsObject = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
            {
                return lines;
            }

            locatorObject = Activator.CreateInstance(locatorType);
            if (locatorObject is null)
            {
                return lines;
            }

            dynamic locator = locatorObject;
            servicesObject = locator.ConnectServer(".", "root\\cimv2");
            dynamic services = servicesObject;
            resultsObject = services.ExecQuery(
                "SELECT CommandLine FROM Win32_Process WHERE Name='codex.exe'",
                "WQL",
                0);

            dynamic results = resultsObject;
            var count = (int)results.Count;
            for (var index = 0; index < count; index++)
            {
                object? itemObject = null;
                object? propertiesObject = null;
                object? propertyObject = null;
                try
                {
                    itemObject = results.ItemIndex(index);
                    dynamic item = itemObject;
                    propertiesObject = item.Properties_;
                    dynamic properties = propertiesObject;
                    propertyObject = properties.Item("CommandLine");
                    dynamic property = propertyObject;
                    var commandLine = property.Value as string;
                    if (!string.IsNullOrWhiteSpace(commandLine))
                    {
                        lines.Add(commandLine);
                    }
                }
                finally
                {
                    ReleaseComObject(propertyObject);
                    ReleaseComObject(propertiesObject);
                    ReleaseComObject(itemObject);
                }
            }
        }
        catch
        {
            // Discovery failure is surfaced later as "no endpoint"; no process is modified here.
        }
        finally
        {
            ReleaseComObject(resultsObject);
            ReleaseComObject(servicesObject);
            ReleaseComObject(locatorObject);
        }

        return lines;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
            }
        }
    }

    private static string? ExtractEscapedAssignment(string commandLine, string key)
    {
        foreach (var format in new[]
                 {
                     (Marker: $"{key}\\\"=\\\"", End: "\\\"", Escaped: true),
                     (Marker: $"{key}=\"", End: "\"", Escaped: false)
                 })
        {
            var start = commandLine.IndexOf(format.Marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }

            start += format.Marker.Length;
            var end = commandLine.IndexOf(format.End, start, StringComparison.Ordinal);
            if (end <= start)
            {
                continue;
            }

            var value = commandLine[start..end];
            if (format.Escaped)
            {
                try
                {
                    return JsonSerializer.Deserialize<string>($"\"{value}\"");
                }
                catch
                {
                    // Keep a conservative path-only fallback for older command-line encoders.
                    value = value.Replace("\\\"", "\"", StringComparison.Ordinal)
                        .Replace("\\\\", "\\", StringComparison.Ordinal);
                }
            }

            return value;
        }

        return null;
    }

    private static string? FindPluginDirectoryFromEnvironment()
    {
        var resources = Environment.GetEnvironmentVariable("CODEX_ELECTRON_RESOURCES_PATH");
        if (string.IsNullOrWhiteSpace(resources))
        {
            return null;
        }

        var candidate = Path.Combine(resources, "plugins", "openai-bundled", "plugins", "codex-app-tools");
        return CodexDesktopInstallation.IsPluginDirectory(candidate) ? candidate : null;
    }

    private static string? FindInstalledPluginDirectory()
    {
        // Current desktop builds install versioned plugins under CODEX_HOME,
        // rather than embedding them in a WindowsApps resource directory.
        foreach (var codexDirectory in new[]
                 {
                     Environment.GetEnvironmentVariable("CODEX_HOME"),
                     Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cached = CodexDesktopInstallation.FindCachedPluginDirectory(codexDirectory);
            if (cached is not null) return cached;
        }

        try
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");
            return Directory.EnumerateDirectories(windowsApps, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly)
                .Select(path => Path.Combine(
                    path,
                    "app",
                    "resources",
                    "plugins",
                    "openai-bundled",
                    "plugins",
                    "codex-app-tools"))
                .Where(CodexDesktopInstallation.IsPluginDirectory)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindInstalledNodeRuntime()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI",
                "Codex",
                "runtimes",
                "cua_node");
            return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, "bin", "node.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> EnumerateCodexPipeCandidates()
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Where(CodexDesktopInstallation.IsAppToolsPipeCandidate)
                // Prefer the current app pipe namespace to legacy per-session
                // browser pipes when no executor environment is available.
                .OrderBy(path => path.StartsWith(@"\\.\pipe\codex-browser-use\",
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool TryParseTask(JsonObject source, out CodexDesktopTask task)
    {
        var id = ReadString(source, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            task = null!;
            return false;
        }

        var statusNode = source["status"];
        task = new CodexDesktopTask(
            id,
            FirstNonEmpty(ReadString(source, "title"), "未命名任务")!,
            ReadString(source, "summary"),
            ReadStatus(statusNode),
            ReadString(source, "hostId"),
            ReadString(source, "cwd"),
            ReadLong(source, "updatedAt") ?? 0,
            ReadString(source, "projectId"),
            ReadActiveFlags(statusNode),
            null,
            null,
            null);
        return true;
    }

    private static IReadOnlyList<CodexDesktopTurnSummary> ParseTurns(JsonArray? source)
    {
        if (source is null)
        {
            return [];
        }

        var turns = new List<CodexDesktopTurnSummary>();
        foreach (var turn in source.OfType<JsonObject>())
        {
            var id = ReadString(turn, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var activity = ExtractCurrentActivity(turn["items"] as JsonArray, id);
            var context = PetTaskContextSignals.Read(turn["items"] as JsonArray);
            turns.Add(new CodexDesktopTurnSummary(
                id,
                ReadString(turn, "status") ?? "unknown",
                activity.Operation,
                activity.ActivityKey,
                ReadLong(turn, "startedAt"),
                ReadLong(turn, "completedAt"),
                ReadLong(turn, "durationMs"),
                turn["error"] is JsonObject error ? ReadString(error, "message") : null)
            {
                ResumeRequested = context.ResumeRequested,
                HasCodeEdit = context.HasCodeEdit
            });
        }

        return turns;
    }

    private static (string? Operation, string? ActivityKey) ExtractCurrentActivity(
        JsonArray? items,
        string turnId)
    {
        if (items is null)
        {
            return (null, null);
        }

        string? currentActivityKey = null;
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index] is not JsonObject item)
            {
                continue;
            }

            var type = ReadString(item, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            // The key deliberately excludes message/output text. In particular, streamed
            // agentMessage text may change on every poll while the activity itself has not.
            currentActivityKey ??= BuildActivityKey(item, type, turnId, index);
            var operation = type switch
            {
                "agentMessage" => ReadString(item, "text"),
                "commandExecution" => Prefix("执行命令", ReadString(item, "command")),
                "fileChange" => "正在处理文件改动",
                "mcpToolCall" => Prefix(
                    "调用工具",
                    FirstNonEmpty(ReadString(item, "tool"), ReadString(item, "server"))),
                "dynamicToolCall" => Prefix("调用工具", ReadString(item, "tool")),
                "collabToolCall" or "collabAgentToolCall" => Prefix("协作", ReadString(item, "tool")),
                "webSearch" => Prefix(
                    "搜索",
                    FirstNonEmpty(ReadString(item, "query"), ReadNestedString(item, "action", "query"))),
                "imageView" => Prefix("查看图片", ReadString(item, "path")),
                "plan" => Prefix("计划", ReadString(item, "text")),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(operation))
            {
                return (Shorten(operation, 180), currentActivityKey);
            }
        }

        return (null, currentActivityKey);
    }

    private static string BuildActivityKey(
        JsonObject item,
        string type,
        string turnId,
        int itemIndex)
    {
        var itemId = ReadString(item, "id");
        var status = ReadItemStatus(item);

        // Prefer the protocol's native identity tuple. Older/partial payloads can omit
        // item.id, so turn + array position provides a stable fallback that still changes
        // when a genuinely new item appears.
        var stableId = !string.IsNullOrWhiteSpace(itemId)
            ? itemId
            : FirstNonEmpty(
                ReadString(item, "callId", "toolCallId"),
                $"{turnId}:item-{itemIndex}")!;
        return $"{stableId}:{type}:{status}";
    }

    private static string ReadItemStatus(JsonObject item)
    {
        var status = item["status"] switch
        {
            JsonObject statusObject => ReadString(statusObject, "type", "status", "state"),
            { } statusNode => NodeAsString(statusNode),
            _ => null
        };

        return FirstNonEmpty(status, ReadString(item, "state"), "unknown")!;
    }

    private static string OperationFromFlags(
        IReadOnlyList<string> flags,
        string taskStatus,
        string? turnStatus)
    {
        if (flags.Any(flag => flag.Equals("waitingOnApproval", StringComparison.OrdinalIgnoreCase)))
        {
            return "等待你的确认";
        }

        if (flags.Any(flag => flag.Equals("waitingOnUserInput", StringComparison.OrdinalIgnoreCase)))
        {
            return "等待你的输入";
        }

        if (string.Equals(turnStatus, "inProgress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(taskStatus, "active", StringComparison.OrdinalIgnoreCase))
        {
            return "任务正在运行";
        }

        return taskStatus.Equals("idle", StringComparison.OrdinalIgnoreCase) ? "任务空闲" : "任务未载入";
    }

    private static JsonObject RequireToolData(CodexDesktopToolResult result, string toolName)
    {
        if (result.IsError)
        {
            throw new CodexDesktopBridgeException($"官方 Codex 工具 {toolName} 返回失败。{FirstNonEmpty(result.TextBlocks.FirstOrDefault(), string.Empty)}");
        }

        return result.JsonData as JsonObject
               ?? throw new CodexDesktopBridgeException($"官方 Codex 工具 {toolName} 未返回有效 JSON 数据。");
    }

    private static string ReadStatus(JsonNode? node)
    {
        if (node is JsonObject statusObject)
        {
            return ReadString(statusObject, "type", "status") ?? "unknown";
        }

        return NodeAsString(node) ?? "unknown";
    }

    private static IReadOnlyList<string> ReadActiveFlags(JsonNode? node)
    {
        if (node is not JsonObject status || status["activeFlags"] is not JsonArray flags)
        {
            return [];
        }

        return flags.Select(NodeAsString)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? Prefix(string prefix, string? value) =>
        string.IsNullOrWhiteSpace(value) ? prefix : $"{prefix}：{value}";

    private static string Shorten(string value, int maxLength)
    {
        var collapsed = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxLength ? collapsed : $"{collapsed[..Math.Max(1, maxLength - 1)]}…";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? ReadNestedString(JsonObject source, string property, params string[] nested) =>
        source[property] is JsonObject child ? ReadString(child, nested) : null;

    private static string? ReadString(JsonObject source, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (NodeAsString(source[property]) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static long? ReadLong(JsonObject source, string property)
    {
        if (source[property] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return long.TryParse(NodeAsString(value), out number) ? number : null;
    }

    private static bool? ReadBoolean(JsonObject source, string property)
    {
        if (source[property] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        return bool.TryParse(NodeAsString(value), out result) ? result : null;
    }

    private static string? NodeAsString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<long>(out var integer))
        {
            return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return value.ToJsonString().Trim('"');
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!IsConnected)
        {
            throw new CodexDesktopBridgeException(LastError ?? "尚未连接官方 Codex 桌面应用。");
        }
    }

    private void EnsureProcessRunning()
    {
        ThrowIfDisposed();
        if (_process is not { HasExited: false } || _writer is null)
        {
            throw new CodexDesktopBridgeException(LastError ?? "Codex App Tools MCP 代理未运行。");
        }
    }

    private void SetState(bool connected, string? anchorTaskId, string? error)
    {
        lock (_stateGate)
        {
            _connected = connected;
            _anchorTaskId = anchorTaskId;
            _lastError = error;
        }
    }

    private void SetDisconnected(string error)
    {
        lock (_stateGate)
        {
            _connected = false;
            _lastError = error;
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var entry in _pending.ToArray())
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _writeGate.Dispose();
            _lifecycleGate.Dispose();
        }
    }
}
