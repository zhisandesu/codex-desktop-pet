using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

/// <summary>
/// Small, dependency-free client for the newline-delimited JSON protocol exposed by
/// <c>codex app-server --stdio</c>.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private sealed class PendingRpcRequest
    {
        public TaskCompletionSource<JsonNode?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingApproval(string Method, JsonObject Params, JsonNode RequestId);

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, PendingRpcRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, PendingApproval> _pendingApprovals = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _stderrGate = new();
    private readonly Queue<string> _stderrTail = new();

    private readonly bool _marshalEventsToCurrentContext;
    private SynchronizationContext? _eventContext;
    private Process? _process;
    private StreamWriter? _writer;
    private CancellationTokenSource? _connectionCts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private bool _disposed;
    private CodexConnectionState _connectionState = CodexConnectionState.Disconnected;
    private string? _currentThreadId;
    private string? _currentTurnId;
    private string? _lastError;

    public CodexAppServerClient(bool marshalEventsToCurrentContext = true)
    {
        _marshalEventsToCurrentContext = marshalEventsToCurrentContext;
        _eventContext = marshalEventsToCurrentContext ? SynchronizationContext.Current : null;
    }

    public event EventHandler<CodexConnectionState>? ConnectionStateChanged;

    public event EventHandler<CodexNotification>? NotificationReceived;

    public event EventHandler<string>? AgentMessageDelta;

    public event EventHandler<CodexAgentMessageCompleted>? AgentMessageCompleted;

    public event EventHandler<CodexTurnCompleted>? TurnCompleted;

    public event EventHandler<CodexApprovalRequest>? ApprovalRequested;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public CodexConnectionState ConnectionState
    {
        get
        {
            lock (_stateGate)
            {
                return _connectionState;
            }
        }
    }

    public bool IsConnected => ConnectionState == CodexConnectionState.Connected;

    public string? CurrentThreadId
    {
        get
        {
            lock (_stateGate)
            {
                return _currentThreadId;
            }
        }
    }

    public string? CurrentTurnId
    {
        get
        {
            lock (_stateGate)
            {
                return _currentTurnId;
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

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CaptureEventContext();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsProcessConnected())
            {
                return;
            }

            await StopProcessAsync().ConfigureAwait(false);
            await StartAndInitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CaptureEventContext();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
            await StartAndInitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var threads = new List<CodexThreadSummary>();
        string? cursor = null;

        // A bounded pagination loop keeps the pet responsive even for very large histories.
        for (var page = 0; page < 5; page++)
        {
            var parameters = new JsonObject
            {
                ["cursor"] = cursor,
                ["limit"] = 100,
                ["sortKey"] = "updated_at",
                ["sortDirection"] = "desc",
                ["sourceKinds"] = new JsonArray("cli", "vscode", "appServer")
            };

            var result = await SendRequestAsync("thread/list", parameters, cancellationToken)
                .ConfigureAwait(false);
            var resultObject = AsObject(result);
            var data = FindArray(resultObject, "data", "threads", "items");

            if (data is not null)
            {
                foreach (var node in data)
                {
                    if (node is JsonObject thread && TryParseThread(thread, out var summary))
                    {
                        threads.Add(summary);
                    }
                }
            }

            cursor = ReadString(resultObject, "nextCursor", "next_cursor", "cursor");
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        return threads
            .GroupBy(thread => thread.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToArray();
    }

    public async Task<CodexAccountInfo> ReadAccountAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var result = await SendRequestAsync(
                "account/read",
                new JsonObject
                {
                    ["refreshToken"] = false
                },
                cancellationToken)
            .ConfigureAwait(false);
        var account = AsObject(AsObject(result)["account"]);
        return new CodexAccountInfo(
            ReadString(account, "type"),
            ReadString(account, "planType", "plan_type"));
    }

    public async Task<bool> IsModelAvailableAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await SendRequestAsync(
                    "model/list",
                    new JsonObject
                    {
                        ["cursor"] = cursor,
                        ["includeHidden"] = true,
                        ["limit"] = 100
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var response = AsObject(result);
            var data = FindArray(response, "data", "models", "items");
            if (data is not null)
            {
                foreach (var node in data)
                {
                    if (node is not JsonObject item)
                    {
                        continue;
                    }

                    var advertised = ReadString(item, "model", "id");
                    if (string.Equals(advertised, model, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            cursor = ReadString(response, "nextCursor", "next_cursor", "cursor");
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        return false;
    }

    public Task<string> StartThreadAsync(string cwd, CancellationToken cancellationToken = default) =>
        StartThreadAsync(cwd, options: null, cancellationToken);

    public async Task<string> StartThreadAsync(
        string cwd,
        CodexThreadStartOptions? options,
        CancellationToken cancellationToken = default)
    {
        var started = await StartThreadDetailedAsync(cwd, options, cancellationToken)
            .ConfigureAwait(false);
        return started.ThreadId;
    }

    public async Task<CodexThreadStartResult> StartThreadDetailedAsync(
        string cwd,
        CodexThreadStartOptions? options,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        var fullPath = Path.GetFullPath(cwd);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{fullPath}");
        }

        var parameters = new JsonObject
        {
            ["cwd"] = fullPath
        };
        ApplyThreadStartOptions(parameters, options);

        var result = await SendRequestAsync(
                "thread/start",
                parameters,
                cancellationToken)
            .ConfigureAwait(false);

        var resultObject = AsObject(result);
        var thread = AsObject(resultObject["thread"]);
        var threadId = ReadString(thread, "id")
                       ?? ReadString(resultObject, "threadId", "thread_id", "id")
                       ?? throw new CodexProtocolException("thread/start 响应中没有线程 ID。");
        var model = ReadString(resultObject, "model") ?? ReadString(thread, "model");
        var modelProvider = ReadString(resultObject, "modelProvider", "model_provider")
                            ?? ReadString(thread, "modelProvider", "model_provider");
        var ephemeral = ReadBoolean(thread, "ephemeral");

        SetCurrentIds(threadId, null);
        return new CodexThreadStartResult(threadId, model, modelProvider, ephemeral);
    }

    public async Task SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await SendRequestAsync(
                "thread/name/set",
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["name"] = name
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CodexThreadMetadata> ReadThreadMetadataAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var result = await SendRequestAsync(
                "thread/read",
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["includeTurns"] = false
                },
                cancellationToken)
            .ConfigureAwait(false);
        var resultObject = AsObject(result);
        var thread = resultObject["thread"] as JsonObject ?? resultObject;
        var id = ReadString(thread, "id", "threadId", "thread_id")
                 ?? throw new CodexProtocolException("thread/read 响应中没有线程 ID。");
        return new CodexThreadMetadata(
            id,
            ReadPath(thread["cwd"]),
            ReadString(thread, "threadSource", "thread_source"),
            ReadBoolean(thread, "ephemeral"),
            ReadString(thread, "modelProvider", "model_provider"),
            ReadString(thread, "name", "title"));
    }

    public async Task<string> ResumeThreadAsync(string id, CancellationToken cancellationToken = default)
        => await ResumeThreadAsync(id, cwd: null, options: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<string> ResumeThreadAsync(
        string id,
        string? cwd,
        CodexThreadStartOptions? options,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var parameters = new JsonObject
        {
            ["threadId"] = id,
            ["excludeTurns"] = true
        };
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            parameters["cwd"] = Path.GetFullPath(cwd);
        }

        ApplyThreadResumeOptions(parameters, options);
        var result = await SendRequestAsync(
                "thread/resume",
                parameters,
                cancellationToken)
            .ConfigureAwait(false);

        var threadId = ReadNestedString(result, "thread", "id")
                       ?? ReadString(AsObject(result), "threadId", "thread_id", "id")
                       ?? id;

        SetCurrentIds(threadId, null);
        return threadId;
    }

    public async Task ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        await SendRequestAsync(
                "thread/archive",
                new JsonObject { ["threadId"] = threadId },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> StartTurnAsync(
        string threadId,
        string prompt,
        CancellationToken cancellationToken = default) =>
        StartTurnAsync(threadId, prompt, options: null, cancellationToken);

    public async Task<string> StartTurnAsync(
        string threadId,
        string prompt,
        CodexTurnStartOptions? options,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var parameters = new JsonObject
        {
            ["threadId"] = threadId,
            ["input"] = CreateTextInput(prompt)
        };
        ApplyTurnStartOptions(parameters, options);

        var result = await SendRequestAsync(
                "turn/start",
                parameters,
                cancellationToken)
            .ConfigureAwait(false);

        var turnId = ReadNestedString(result, "turn", "id")
                     ?? ReadString(AsObject(result), "turnId", "turn_id", "id")
                     ?? throw new CodexProtocolException("turn/start 响应中没有回合 ID。");

        SetCurrentIds(threadId, turnId);
        return turnId;
    }

    public Task<string> SteerTurnAsync(
        string threadId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var turnId = CurrentTurnId;
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("当前没有可以追加要求的运行中任务。");
        }

        return SteerTurnAsync(threadId, turnId, prompt, cancellationToken);
    }

    public async Task<string> SteerTurnAsync(
        string threadId,
        string expectedTurnId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var result = await SendRequestAsync(
                "turn/steer",
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["expectedTurnId"] = expectedTurnId,
                    ["input"] = CreateTextInput(prompt)
                },
                cancellationToken)
            .ConfigureAwait(false);

        var turnId = ReadString(AsObject(result), "turnId", "turn_id", "id") ?? expectedTurnId;
        SetCurrentIds(threadId, turnId);
        return turnId;
    }

    public Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        var threadId = CurrentThreadId;
        var turnId = CurrentTurnId;
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("当前没有可以中断的运行中任务。");
        }

        return InterruptTurnAsync(threadId, turnId, cancellationToken);
    }

    public async Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        await SendRequestAsync(
                "turn/interrupt",
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task RespondToApprovalAsync(
        CodexApprovalRequest request,
        string decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RespondToApprovalAsync(request.RequestId, decision, cancellationToken);
    }

    public async Task RespondToApprovalAsync(
        JsonNode requestId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision);

        var key = RequestKey(requestId);
        if (!_pendingApprovals.TryRemove(key, out var approval))
        {
            throw new InvalidOperationException("这项确认已经处理或已失效。");
        }

        var response = BuildApprovalResponse(approval, decision);
        var message = new JsonObject
        {
            ["id"] = requestId.DeepClone(),
            ["result"] = response
        };

        try
        {
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingApprovals.TryAdd(key, approval);
            throw;
        }
    }

    private async Task StartAndInitializeAsync(CancellationToken cancellationToken)
    {
        SetConnectionState(CodexConnectionState.Connecting, clearError: true);

        try
        {
            var executable = FindCodexExecutable();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8WithoutBom,
                StandardOutputEncoding = Utf8WithoutBom,
                StandardErrorEncoding = Utf8WithoutBom,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");

            // ProcessStartInfo inherits the environment. Assigning CODEX_HOME explicitly makes
            // that contract clear and also preserves a non-default desktop-app data directory.
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(codexHome))
            {
                startInfo.Environment["CODEX_HOME"] = codexHome;
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 Codex App Server。");
            }

            var connectionCts = new CancellationTokenSource();
            _process = process;
            _connectionCts = connectionCts;
            _writer = process.StandardInput;
            _writer.AutoFlush = true;
            _writer.NewLine = "\n";

            _stdoutTask = ReadStdoutAsync(process, connectionCts.Token);
            _stderrTask = ReadStderrAsync(process, connectionCts.Token);

            var assemblyVersion = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString()
                                  ?? "1.0.0";
            var initializeParams = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "xiaobian_pet",
                    ["title"] = "柯朵桌宠",
                    ["version"] = assemblyVersion
                },
                ["capabilities"] = new JsonObject
                {
                    ["experimentalApi"] = true,
                    ["requestAttestation"] = false
                }
            };

            await SendRequestAsync("initialize", initializeParams, cancellationToken)
                .ConfigureAwait(false);
            await SendMessageAsync(
                    new JsonObject
                    {
                        ["method"] = "initialized",
                        ["params"] = new JsonObject()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            SetConnectionState(CodexConnectionState.Connected, clearError: true);
        }
        catch (Exception ex)
        {
            SetConnectionState(CodexConnectionState.Error, ex.Message);
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JsonNode?> SendRequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var key = id.ToString(CultureInfo.InvariantCulture);
        var pending = new PendingRpcRequest();

        if (!_pendingRequests.TryAdd(key, pending))
        {
            throw new InvalidOperationException("无法登记 Codex RPC 请求。");
        }

        try
        {
            await SendMessageAsync(
                    new JsonObject
                    {
                        ["method"] = method,
                        ["id"] = id,
                        ["params"] = parameters
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (RequestTimeout != Timeout.InfiniteTimeSpan)
            {
                timeoutCts.CancelAfter(RequestTimeout);
            }

            try
            {
                return await pending.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Codex 请求 {method} 等待响应超时。");
            }
        }
        finally
        {
            _pendingRequests.TryRemove(key, out _);
        }
    }

    private async Task SendMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var writer = _writer;
        var process = _process;
        if (writer is null || process is null || SafeHasExited(process))
        {
            throw new InvalidOperationException("Codex App Server 尚未连接。");
        }

        var line = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            SetLastError(ex.Message);
            throw new IOException("向 Codex App Server 发送消息失败。", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(Process owner, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await owner.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(line) is JsonObject message)
                    {
                        await HandleIncomingMessageAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (JsonException ex)
                {
                    // A malformed stdout line should not tear down an otherwise healthy session.
                    SetLastError($"Codex 返回了无法解析的消息：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(owner, _process) && !_disposed)
            {
                var detail = GetStderrSummary();
                var message = string.IsNullOrWhiteSpace(detail)
                    ? "Codex App Server 已意外退出。"
                    : $"Codex App Server 已退出：{detail}";
                FailAllPending(new IOException(message));
                SetConnectionState(CodexConnectionState.Error, message);
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

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lock (_stderrGate)
                {
                    _stderrTail.Enqueue(line.Trim());
                    while (_stderrTail.Count > 30)
                    {
                        _stderrTail.Dequeue();
                    }
                }

                Debug.WriteLine($"[codex app-server] {line}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[codex app-server stderr] {ex}");
        }
    }

    private async Task HandleIncomingMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"];
        var method = ReadString(message, "method");

        if (id is not null && !string.IsNullOrWhiteSpace(method))
        {
            await HandleServerRequestAsync(id, method, AsObject(message["params"]), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (id is not null)
        {
            HandleResponse(id, message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(method))
        {
            HandleNotification(method, AsObject(message["params"]));
        }
    }

    private void HandleResponse(JsonNode id, JsonObject message)
    {
        if (!_pendingRequests.TryRemove(RequestKey(id), out var pending))
        {
            return;
        }

        if (message["error"] is JsonObject error)
        {
            var code = ReadLong(error, "code");
            var errorMessage = ReadString(error, "message", "detail") ?? "Codex 请求失败。";
            SetLastError(errorMessage);
            pending.Completion.TrySetException(new CodexRpcException(code, errorMessage, error["data"]?.DeepClone()));
            return;
        }

        pending.Completion.TrySetResult(message["result"]?.DeepClone());
    }

    private async Task HandleServerRequestAsync(
        JsonNode id,
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (IsApprovalMethod(method))
        {
            var idClone = id.DeepClone();
            var paramsClone = (JsonObject)parameters.DeepClone();
            var approval = new PendingApproval(method, paramsClone, idClone);
            _pendingApprovals[RequestKey(id)] = approval;
            RaiseApprovalRequested(approval);
            return;
        }

        // Do not leave an unsupported reverse request hanging indefinitely.
        await SendMessageAsync(
                new JsonObject
                {
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = $"Client method not supported: {method}"
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void HandleNotification(string method, JsonObject parameters)
    {
        var parametersForEvent = (JsonObject)parameters.DeepClone();
        RaiseEvent(NotificationReceived, new CodexNotification(method, parametersForEvent));

        switch (method)
        {
            case "thread/started":
            {
                var threadId = ReadNestedString(parameters, "thread", "id")
                               ?? ReadString(parameters, "threadId", "thread_id");
                if (!string.IsNullOrWhiteSpace(threadId) && string.IsNullOrWhiteSpace(CurrentThreadId))
                {
                    SetCurrentIds(threadId, CurrentTurnId);
                }

                break;
            }
            case "turn/started":
            {
                var threadId = ReadString(parameters, "threadId", "thread_id") ?? CurrentThreadId;
                var turnId = ReadNestedString(parameters, "turn", "id")
                             ?? ReadString(parameters, "turnId", "turn_id");
                var currentThreadId = CurrentThreadId;
                if (string.IsNullOrWhiteSpace(currentThreadId) ||
                    string.Equals(currentThreadId, threadId, StringComparison.Ordinal))
                {
                    SetCurrentIds(threadId, turnId);
                }
                break;
            }
            case "item/agentMessage/delta":
            {
                var delta = ReadString(parameters, "delta", "text");
                if (!string.IsNullOrEmpty(delta))
                {
                    RaiseEvent(AgentMessageDelta, delta);
                }

                break;
            }
            case "item/completed":
            {
                var item = AsObject(parameters["item"]);
                if (string.Equals(ReadString(item, "type"), "agentMessage", StringComparison.Ordinal))
                {
                    var text = ReadString(item, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var threadId = ReadString(parameters, "threadId", "thread_id")
                                       ?? CurrentThreadId
                                       ?? string.Empty;
                        var turnId = ReadString(parameters, "turnId", "turn_id")
                                     ?? CurrentTurnId
                                     ?? string.Empty;
                        var itemId = ReadString(item, "id", "itemId", "item_id") ?? string.Empty;
                        var phase = ReadString(item, "phase");
                        RaiseEvent(
                            AgentMessageCompleted,
                            new CodexAgentMessageCompleted(threadId, turnId, itemId, text, phase));
                    }
                }

                break;
            }
            case "turn/completed":
            {
                var threadId = ReadString(parameters, "threadId", "thread_id") ?? CurrentThreadId ?? string.Empty;
                var turn = AsObject(parameters["turn"]);
                var turnId = ReadString(turn, "id", "turnId", "turn_id")
                             ?? ReadString(parameters, "turnId", "turn_id")
                             ?? CurrentTurnId
                             ?? string.Empty;
                var status = ReadString(turn, "status")
                             ?? ReadString(parameters, "status")
                             ?? "completed";
                var errorMessage = ReadNestedString(turn, "error", "message")
                                   ?? ReadNestedString(parameters, "error", "message")
                                   ?? ReadString(parameters, "errorMessage");

                lock (_stateGate)
                {
                    var sameThread = string.IsNullOrWhiteSpace(threadId) ||
                                     string.Equals(_currentThreadId, threadId, StringComparison.Ordinal);
                    var sameTurn = string.IsNullOrWhiteSpace(turnId) ||
                                   string.Equals(_currentTurnId, turnId, StringComparison.Ordinal);
                    if (sameThread && sameTurn)
                    {
                        _currentTurnId = null;
                    }
                }

                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    SetLastError(errorMessage);
                }

                RaiseEvent(TurnCompleted, new CodexTurnCompleted(threadId, turnId, status, errorMessage));
                break;
            }
            case "error":
            {
                var errorMessage = ReadNestedString(parameters, "error", "message")
                                   ?? ReadString(parameters, "message", "detail");
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    SetLastError(errorMessage);
                }

                break;
            }
            case "serverRequest/resolved":
            {
                var requestId = parameters["requestId"];
                if (requestId is not null)
                {
                    _pendingApprovals.TryRemove(RequestKey(requestId), out _);
                }

                break;
            }
        }
    }

    private void RaiseApprovalRequested(PendingApproval approval)
    {
        var parameters = approval.Params;
        var threadId = ReadString(parameters, "threadId", "thread_id", "conversationId")
                       ?? CurrentThreadId
                       ?? string.Empty;
        var turnId = ReadString(parameters, "turnId", "turn_id") ?? CurrentTurnId ?? string.Empty;
        var reason = ReadString(parameters, "reason", "message");
        var cwd = ReadString(parameters, "cwd");
        var command = ReadString(parameters, "command");
        var title = approval.Method switch
        {
            "item/fileChange/requestApproval" or "applyPatchApproval" => "确认文件修改",
            "item/permissions/requestApproval" => "授予额外权限",
            _ when parameters["networkApprovalContext"] is not null => "确认网络访问",
            _ => "确认执行命令"
        };

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(command))
        {
            details.Add(command);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            details.Add(reason);
        }

        if (!string.IsNullOrWhiteSpace(cwd))
        {
            details.Add($"目录：{cwd}");
        }

        if (parameters["permissions"] is JsonObject permissions)
        {
            details.Add($"权限：{permissions.ToJsonString()}");
        }

        var availableDecisions = ReadAvailableDecisions(approval.Method, parameters);
        var request = new CodexApprovalRequest(
            approval.RequestId.DeepClone(),
            approval.Method,
            threadId,
            turnId,
            title,
            details.Count == 0 ? "Codex 请求执行一项需要确认的操作。" : string.Join(Environment.NewLine, details),
            availableDecisions);

        RaiseEvent(ApprovalRequested, request);
    }

    private static JsonObject BuildApprovalResponse(PendingApproval approval, string decision)
    {
        var normalized = NormalizeDecision(decision);

        if (approval.Method == "item/permissions/requestApproval")
        {
            var accepted = normalized is "accept" or "acceptForSession";
            var requestedPermissions = approval.Params["permissions"] as JsonObject;
            var grantedPermissions = accepted && requestedPermissions is not null
                ? (JsonObject)requestedPermissions.DeepClone()
                : new JsonObject();

            return new JsonObject
            {
                ["permissions"] = grantedPermissions,
                ["scope"] = normalized == "acceptForSession" ? "session" : "turn"
            };
        }

        if (approval.Method is "execCommandApproval" or "applyPatchApproval")
        {
            var legacyDecision = normalized switch
            {
                "accept" => "approved",
                "acceptForSession" => "approved_for_session",
                "cancel" => "abort",
                _ => "abort"
            };
            return new JsonObject { ["decision"] = legacyDecision };
        }

        return new JsonObject { ["decision"] = normalized };
    }

    private static IReadOnlyList<string> ReadAvailableDecisions(string method, JsonObject parameters)
    {
        if (parameters["availableDecisions"] is JsonArray array)
        {
            var values = new List<string>();
            foreach (var node in array)
            {
                if (node is JsonObject decisionObject)
                {
                    var property = decisionObject.FirstOrDefault().Key;
                    if (!string.IsNullOrWhiteSpace(property))
                    {
                        values.Add(property);
                    }
                }
                else
                {
                    var value = NodeAsString(node);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value);
                    }
                }
            }

            if (values.Count > 0)
            {
                return values;
            }
        }

        return method == "item/permissions/requestApproval"
            ? new[] { "accept", "acceptForSession", "decline" }
            : new[] { "accept", "acceptForSession", "decline", "cancel" };
    }

    private static string NormalizeDecision(string decision)
    {
        return decision.Trim().ToLowerInvariant() switch
        {
            "accept" or "approve" or "approved" or "allow" or "yes" => "accept",
            "acceptforsession" or "accept_for_session" or "approved_for_session" or "session" =>
                "acceptForSession",
            "cancel" or "abort" => "cancel",
            "decline" or "deny" or "denied" or "reject" or "no" => "decline",
            _ => decision.Trim()
        };
    }

    private static bool IsApprovalMethod(string method)
    {
        return method is
            "item/commandExecution/requestApproval" or
            "item/fileChange/requestApproval" or
            "item/permissions/requestApproval" or
            "execCommandApproval" or
            "applyPatchApproval";
    }

    private static JsonArray CreateTextInput(string prompt)
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = prompt
            }
        };
    }

    private static void ApplyThreadStartOptions(
        JsonObject parameters,
        CodexThreadStartOptions? options)
    {
        if (options is null)
        {
            return;
        }

        AddOptionalString(parameters, "model", options.Model);
        AddOptionalString(parameters, "modelProvider", options.ModelProvider);
        AddOptionalString(parameters, "baseInstructions", options.BaseInstructions);
        AddOptionalString(parameters, "developerInstructions", options.DeveloperInstructions);
        AddOptionalString(parameters, "approvalPolicy", options.ApprovalPolicy);
        AddOptionalString(parameters, "sandbox", options.Sandbox);
        AddOptionalString(parameters, "threadSource", options.ThreadSource);
        if (options.Ephemeral is { } ephemeral)
        {
            parameters["ephemeral"] = ephemeral;
        }

        if (options.AllowProviderModelFallback is { } allowFallback)
        {
            parameters["allowProviderModelFallback"] = allowFallback;
        }

        if (options.DisableDynamicTools)
        {
            parameters["dynamicTools"] = new JsonArray();
        }

        if (options.DisableEnvironments)
        {
            parameters["environments"] = new JsonArray();
        }
    }

    private static void ApplyThreadResumeOptions(
        JsonObject parameters,
        CodexThreadStartOptions? options)
    {
        if (options is null)
        {
            return;
        }

        AddOptionalString(parameters, "model", options.Model);
        AddOptionalString(parameters, "modelProvider", options.ModelProvider);
        AddOptionalString(parameters, "baseInstructions", options.BaseInstructions);
        AddOptionalString(parameters, "developerInstructions", options.DeveloperInstructions);
        AddOptionalString(parameters, "approvalPolicy", options.ApprovalPolicy);
        AddOptionalString(parameters, "sandbox", options.Sandbox);
    }

    private static void ApplyTurnStartOptions(
        JsonObject parameters,
        CodexTurnStartOptions? options)
    {
        if (options is null)
        {
            return;
        }

        AddOptionalString(parameters, "model", options.Model);
        AddOptionalString(parameters, "effort", options.Effort);
        AddOptionalString(parameters, "turnTrigger", options.TurnTrigger);
        if (options.OutputSchema is not null)
        {
            parameters["outputSchema"] = options.OutputSchema.DeepClone();
        }
    }

    private static void AddOptionalString(
        JsonObject parameters,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters[name] = value.Trim();
        }
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        var writer = _writer;
        var connectionCts = _connectionCts;
        var stdoutTask = _stdoutTask;
        var stderrTask = _stderrTask;

        _process = null;
        _writer = null;
        _connectionCts = null;
        _stdoutTask = null;
        _stderrTask = null;

        connectionCts?.Cancel();
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
                if (!SafeHasExited(process))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(ShutdownWait).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        var readTasks = new[] { stdoutTask, stderrTask }.Where(task => task is not null).Cast<Task>().ToArray();
        if (readTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(readTasks).WaitAsync(ShutdownWait).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        writer?.Dispose();
        process?.Dispose();
        connectionCts?.Dispose();

        FailAllPending(new IOException("Codex App Server 连接已关闭。"));
        _pendingApprovals.Clear();
        SetCurrentIds(null, null);
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pair in _pendingRequests.ToArray())
        {
            if (_pendingRequests.TryRemove(pair.Key, out var request))
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    private bool IsProcessConnected()
    {
        var process = _process;
        return ConnectionState == CodexConnectionState.Connected &&
               process is not null &&
               !SafeHasExited(process);
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!IsProcessConnected())
        {
            throw new InvalidOperationException("尚未连接到 Codex App Server。");
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static string FindCodexExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var candidates = new List<string>();
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            candidates.Add(Path.Combine(directory, "codex.exe"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps", "codex.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "codex.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
            }
        }

        var desktopBinRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            var installedExecutable = Directory.Exists(desktopBinRoot)
                ? Directory.EnumerateFiles(desktopBinRoot, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (!string.IsNullOrWhiteSpace(installedExecutable))
            {
                return Path.GetFullPath(installedExecutable);
            }
        }
        catch
        {
        }

        throw new FileNotFoundException(
            "没有找到 codex.exe。请先安装或启动一次 Codex 桌面版，或将 CODEX_EXECUTABLE 指向 codex.exe。");
    }

    private static bool TryParseThread(JsonObject thread, out CodexThreadSummary summary)
    {
        var id = ReadString(thread, "id", "threadId", "thread_id");
        if (string.IsNullOrWhiteSpace(id))
        {
            summary = null!;
            return false;
        }

        var name = ReadString(thread, "name", "title") ?? string.Empty;
        var preview = ReadString(thread, "preview", "firstUserMessage", "summary") ?? string.Empty;
        var cwd = ReadPath(thread["cwd"]);
        var statusNode = thread["status"];
        var status = statusNode is JsonObject statusObject
            ? ReadString(statusObject, "type", "status") ?? statusObject.ToJsonString()
            : NodeAsString(statusNode) ?? "unknown";
        var updatedAt = ReadLong(thread, "updatedAt", "updated_at", "recencyAt", "createdAt") ?? 0L;

        summary = new CodexThreadSummary(
            id,
            name,
            preview,
            cwd,
            status,
            updatedAt,
            ReadString(thread, "threadSource", "thread_source"),
            ReadBoolean(thread, "ephemeral"),
            ReadString(thread, "modelProvider", "model_provider"));
        return true;
    }

    private static string ReadPath(JsonNode? node)
    {
        if (node is JsonObject pathObject)
        {
            return ReadString(pathObject, "path", "value", "cwd") ?? pathObject.ToJsonString();
        }

        return NodeAsString(node) ?? string.Empty;
    }

    private static JsonObject AsObject(JsonNode? node)
    {
        return node as JsonObject ?? new JsonObject();
    }

    private static JsonArray? FindArray(JsonObject source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (source[name] is JsonArray array)
            {
                return array;
            }
        }

        return null;
    }

    private static string? ReadNestedString(JsonNode? source, string objectProperty, params string[] properties)
    {
        return source is JsonObject sourceObject && sourceObject[objectProperty] is JsonObject nested
            ? ReadString(nested, properties)
            : null;
    }

    private static string? ReadString(JsonObject source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var value = NodeAsString(source[name]);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
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

        if (value.TryGetValue<long>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(out var floatingPoint))
        {
            return floatingPoint.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean ? "true" : "false";
        }

        return value.ToJsonString().Trim('"');
    }

    private static long? ReadLong(JsonObject source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (source[name] is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue<long>(out var integer))
            {
                return integer;
            }

            if (value.TryGetValue<double>(out var floatingPoint))
            {
                return checked((long)floatingPoint);
            }

            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
            {
                return integer;
            }
        }

        return null;
    }

    private static bool? ReadBoolean(JsonObject source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (source[name] is JsonValue value && value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
        }

        return null;
    }

    private static string RequestKey(JsonNode requestId)
    {
        return requestId.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private void SetCurrentIds(string? threadId, string? turnId)
    {
        lock (_stateGate)
        {
            _currentThreadId = threadId;
            _currentTurnId = turnId;
        }
    }

    private void SetLastError(string? error)
    {
        lock (_stateGate)
        {
            _lastError = error;
        }
    }

    private void SetConnectionState(
        CodexConnectionState state,
        string? error = null,
        bool clearError = false)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (clearError)
            {
                _lastError = null;
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                _lastError = error;
            }

            if (_connectionState != state)
            {
                _connectionState = state;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseEvent(ConnectionStateChanged, state);
        }
    }

    private string GetStderrSummary()
    {
        lock (_stderrGate)
        {
            return string.Join(" | ", _stderrTail.TakeLast(4));
        }
    }

    private void CaptureEventContext()
    {
        if (_marshalEventsToCurrentContext)
        {
            _eventContext ??= SynchronizationContext.Current;
        }
    }

    private void RaiseEvent<T>(EventHandler<T>? eventHandler, T value)
    {
        if (eventHandler is null)
        {
            return;
        }

        void InvokeHandlers()
        {
            foreach (EventHandler<T> handler in eventHandler.GetInvocationList())
            {
                try
                {
                    handler(this, value);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Codex event handler failed: {ex}");
                }
            }
        }

        var context = _eventContext;
        if (context is not null && context != SynchronizationContext.Current)
        {
            context.Post(_ => InvokeHandlers(), null);
        }
        else
        {
            InvokeHandlers();
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
            SetConnectionState(CodexConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _writeGate.Dispose();
        _lifecycleGate.Dispose();
    }
}

public sealed class CodexRpcException : Exception
{
    public CodexRpcException(long? code, string message, JsonNode? data = null)
        : base(code is null ? message : $"Codex RPC {code}: {message}")
    {
        Code = code;
        DataNode = data;
    }

    public long? Code { get; }

    public JsonNode? DataNode { get; }
}

public sealed class CodexProtocolException : Exception
{
    public CodexProtocolException(string message)
        : base(message)
    {
    }
}
