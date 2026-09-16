using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XiaobianPet.Services;

/// <summary>
/// Hosts a warm, local faster-whisper process. The worker never receives an API
/// key and is forced to use a model directory already present on this machine.
/// </summary>
internal sealed class LocalWhisperTranscriptionService : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly string[] SafeInheritedEnvironmentVariables =
    [
        "SYSTEMROOT",
        "WINDIR",
        "PATH",
        "PATHEXT",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "LOCALAPPDATA",
        "APPDATA",
        "PROGRAMDATA",
        "PROGRAMFILES",
        "PROGRAMFILES(X86)",
        "COMMONPROGRAMFILES",
        "CUDA_PATH",
        "CUDA_VISIBLE_DEVICES",
        "XIAOBIAN_CUDA_DLL_DIR"
    ];

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _stderrGate = new();
    private readonly Queue<string> _stderrTail = new();

    private Process? _process;
    private StreamWriter? _writer;
    private CancellationTokenSource? _processCts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private TaskCompletionSource<bool>? _ready;
    private bool _disposed;

    public string BackendDescription { get; private set; } = "Whisper large-v3-turbo · 本机";

    internal int? WorkerProcessId
    {
        get
        {
            var process = _process;
            try
            {
                return process is not null && !process.HasExited ? process.Id : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<string> TranscribeAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);

        var fullPath = Path.GetFullPath(audioPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到刚才的录音。", fullPath);
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("无法登记本地语音识别请求。");
            }

            var retireWorker = false;
            try
            {
                var request = new JsonObject
                {
                    ["id"] = requestId,
                    ["audioPath"] = fullPath
                };
                await SendLineAsync(request.ToJsonString(), cancellationToken).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TranscriptionTimeout);
                string text;
                try
                {
                    text = await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    retireWorker = true;
                    throw;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    retireWorker = true;
                    throw new TimeoutException("本地语音识别等待超时，请稍后再试。");
                }

                text = NormalizeTranscript(text);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new NoSpeechDetectedException(
                        "Whisper 没有听清这句话，再靠近麦克风一点试试吧。");
                }

                return text;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                retireWorker = true;
                throw;
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
                if (retireWorker)
                {
                    await RetireWorkerAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsProcessRunning())
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsProcessRunning())
            {
                return;
            }

            await StopProcessAsync().ConfigureAwait(false);

            var workerPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "whisper_worker.py");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException("柯朵的本地语音识别脚本不见了。", workerPath);
            }

            var modelPath = ResolveModelPath();
            var requiredModelFiles = new[] { "model.bin", "config.json", "tokenizer.json" };
            if (requiredModelFiles.Any(file => !File.Exists(Path.Combine(modelPath, file))))
            {
                throw new InvalidOperationException(
                    "本地 Whisper large-v3-turbo 模型尚未安装。请运行“配置柯朵本地语音识别.cmd”。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = FindPythonExecutable(),
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
            startInfo.ArgumentList.Add(workerPath);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelPath);

            var safeEnvironment = SafeInheritedEnvironmentVariables
                .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .ToArray();
            startInfo.Environment.Clear();
            foreach (var (name, value) in safeEnvironment)
            {
                startInfo.Environment[name] = value!;
            }

            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["HF_HUB_OFFLINE"] = "1";
            startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
            startInfo.Environment["TOKENIZERS_PARALLELISM"] = "false";

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动本地 Whisper 识别进程。");
            }

            var processCts = new CancellationTokenSource();
            _process = process;
            _processCts = processCts;
            _writer = process.StandardInput;
            _writer.AutoFlush = true;
            _writer.NewLine = "\n";
            _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stdoutTask = ReadStdoutAsync(process, processCts.Token);
            _stderrTask = ReadStderrAsync(process, processCts.Token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(StartupTimeout);
            try
            {
                await _ready.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Whisper 模型加载超时。首次启动或显存紧张时可稍后再试。");
            }
            catch
            {
                throw new InvalidOperationException(
                    BuildStartupFailureMessage("Whisper 模型没有成功启动。"));
            }
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                JsonObject message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject
                              ?? throw new JsonException("Worker message is not an object.");
                }
                catch (JsonException)
                {
                    AddStderr($"Whisper worker returned invalid JSON: {line}");
                    continue;
                }

                var type = message["type"]?.GetValue<string>();
                switch (type)
                {
                    case "ready":
                    case "backend":
                    {
                        if (type == "ready" &&
                            (message["protocolVersion"]?.GetValue<int>() != 1 ||
                             message["sensitiveEnvironmentSanitized"]?.GetValue<bool>() != true))
                        {
                            terminalError = new InvalidOperationException(
                                "Whisper worker 安全握手失败，已拒绝继续识别。");
                            _ready?.TrySetException(terminalError);
                            break;
                        }

                        var device = message["device"]?.GetValue<string>() ?? "local";
                        var computeType = message["computeType"]?.GetValue<string>() ?? "auto";
                        BackendDescription = device.Equals("cuda", StringComparison.OrdinalIgnoreCase)
                            ? $"Whisper large-v3-turbo · GPU {computeType}"
                            : $"Whisper large-v3-turbo · CPU {computeType}";
                        if (type == "ready")
                        {
                            _ready?.TrySetResult(true);
                        }
                        break;
                    }
                    case "result":
                    {
                        var id = message["id"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(id) && _pending.TryGetValue(id, out var completion))
                        {
                            completion.TrySetResult(message["text"]?.GetValue<string>() ?? string.Empty);
                        }

                        break;
                    }
                    case "error":
                    {
                        var id = message["id"]?.GetValue<string>();
                        var error = message["error"]?.GetValue<string>() ?? "本地语音识别失败。";
                        if (!string.IsNullOrWhiteSpace(id) && _pending.TryGetValue(id, out var completion))
                        {
                            completion.TrySetException(new InvalidOperationException(error));
                        }

                        break;
                    }
                    case "fatal":
                    {
                        terminalError = new InvalidOperationException(
                            message["error"]?.GetValue<string>() ?? "Whisper worker stopped during startup.");
                        _ready?.TrySetException(terminalError);
                        break;
                    }
                }

                if (terminalError is not null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            terminalError ??= new IOException("本地 Whisper 进程意外退出。");
            _ready?.TrySetException(terminalError);
            FailPending(terminalError);
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                AddStderr(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AddStderr(exception.Message);
        }
    }

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        var process = _process;
        var writer = _writer;
        if (process is null || writer is null || process.HasExited)
        {
            throw new IOException("本地 Whisper 进程没有运行。");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private bool IsProcessRunning()
    {
        var process = _process;
        try
        {
            return process is not null && !process.HasExited && _ready?.Task.IsCompletedSuccessfully == true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveModelPath()
    {
        var configured = Environment.GetEnvironmentVariable("XIAOBIAN_WHISPER_MODEL");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XiaobianPet",
                "Models",
                "faster-whisper-large-v3-turbo")
            : Path.GetFullPath(configured.Trim());
    }

    private static string FindPythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("XIAOBIAN_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var programs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Python");
        if (Directory.Exists(programs))
        {
            var candidate = Directory.EnumerateDirectories(programs, "Python*", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Version = ParsePythonDirectoryVersion(path) })
                .OrderByDescending(candidate => candidate.Version.Major)
                .ThenByDescending(candidate => candidate.Version.Minor)
                .Select(candidate => candidate.Path)
                .Select(path => Path.Combine(path, "python.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "没有找到本机 Python。请运行“配置柯朵本地语音识别.cmd”。");
    }

    private static (int Major, int Minor) ParsePythonDirectoryVersion(string path)
    {
        var name = Path.GetFileName(path);
        var digits = name.StartsWith("Python", StringComparison.OrdinalIgnoreCase)
            ? new string(name["Python".Length..].TakeWhile(char.IsDigit).ToArray())
            : string.Empty;
        if (digits.Length < 2 || !int.TryParse(digits[..1], out var major) ||
            !int.TryParse(digits[1..], out var minor))
        {
            return (0, 0);
        }

        return (major, minor);
    }

    private void AddStderr(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_stderrGate)
        {
            _stderrTail.Enqueue(line.Trim());
            while (_stderrTail.Count > 12)
            {
                _stderrTail.Dequeue();
            }
        }
    }

    private string BuildStartupFailureMessage(string prefix)
    {
        lock (_stderrGate)
        {
            return _stderrTail.Count == 0
                ? prefix
                : $"{prefix} {string.Join(" | ", _stderrTail.TakeLast(4))}";
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        var writer = _writer;
        var processCts = _processCts;
        var stdoutTask = _stdoutTask;
        var stderrTask = _stderrTask;

        _process = null;
        _writer = null;
        _processCts = null;
        _stdoutTask = null;
        _stderrTask = null;
        _ready = null;

        processCts?.Cancel();
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
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
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
                await Task.WhenAll(readTasks).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        writer?.Dispose();
        process?.Dispose();
        processCts?.Dispose();
        FailPending(new IOException("本地 Whisper 进程已关闭。"));
    }

    private async Task RetireWorkerAsync()
    {
        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private static string NormalizeTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            ' ',
            text.Replace('\r', ' ').Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopProcessAsync().ConfigureAwait(false);
            }
            finally
            {
                _startGate.Release();
            }
        }
        finally
        {
            _requestGate.Release();
            _startGate.Dispose();
            _writeGate.Dispose();
            _requestGate.Dispose();
        }
    }
}
