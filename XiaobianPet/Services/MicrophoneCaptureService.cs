using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.IO;
using NAudio.Wave;

namespace XiaobianPet.Services;

internal sealed record MicrophoneCaptureResult(
    string AudioPath,
    TimeSpan Duration);

internal sealed class NoSpeechDetectedException : Exception
{
    public NoSpeechDetectedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Captures one spoken turn from the default Windows microphone. A small local
/// energy gate supplies the conversational end point; Whisper performs the
/// second, model-based VAD pass during transcription.
/// </summary>
internal sealed class MicrophoneCaptureService : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private const int SilenceAfterSpeechMilliseconds = 950;
    private const int NoSpeechTimeoutMilliseconds = 10000;
    private const int MaximumCaptureMilliseconds = 30000;
    private const int MinimumVoicedMilliseconds = 160;
    private static readonly TimeSpan StaleRecordingAge = TimeSpan.FromHours(1);

    private readonly object _gate = new();

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<MicrophoneCaptureResult>? _completion;
    private CancellationTokenRegistration _cancellationRegistration;
    private Stopwatch? _stopwatch;
    private string? _audioPath;
    private bool _stopRequested;
    private bool _cancelRequested;
    private bool _speechStarted;
    private int _consecutiveVoicedMilliseconds;
    private int _totalVoicedMilliseconds;
    private long _lastVoiceAtMilliseconds;
    private double _noiseFloor = 0.0035;
    private bool _disposed;

    private sealed record DetachedCapture(
        WaveInEvent? WaveIn,
        WaveFileWriter? Writer,
        TaskCompletionSource<MicrophoneCaptureResult>? Completion,
        CancellationTokenRegistration CancellationRegistration,
        string? AudioPath);

    public MicrophoneCaptureService()
    {
        CleanupStaleRecordings();
    }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _waveIn is not null && !_stopRequested;
            }
        }
    }

    public async Task<MicrophoneCaptureResult> CaptureUtteranceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<MicrophoneCaptureResult> completionTask;
        DetachedCapture? failedCapture = null;
        Exception? startException = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_waveIn is not null)
            {
                throw new InvalidOperationException("麦克风已经在监听。请先结束当前语音回合。");
            }

            var temporaryDirectory = GetTemporaryDirectory();
            Directory.CreateDirectory(temporaryDirectory);

            _audioPath = Path.Combine(temporaryDirectory, $"voice-{Guid.NewGuid():N}.wav");
            _completion = new TaskCompletionSource<MicrophoneCaptureResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completionTask = _completion.Task;
            _stopRequested = false;
            _cancelRequested = false;
            _speechStarted = false;
            _consecutiveVoicedMilliseconds = 0;
            _totalVoicedMilliseconds = 0;
            _lastVoiceAtMilliseconds = 0;
            _noiseFloor = 0.0035;
            _stopwatch = Stopwatch.StartNew();

            var waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                BufferMilliseconds = 40,
                NumberOfBuffers = 3,
                WaveFormat = new WaveFormat(SampleRate, 16, 1)
            };
            var writer = new WaveFileWriter(_audioPath, waveIn.WaveFormat);

            waveIn.DataAvailable += WaveIn_DataAvailable;
            waveIn.RecordingStopped += WaveIn_RecordingStopped;
            _waveIn = waveIn;
            _writer = writer;

            try
            {
                waveIn.StartRecording();
                _cancellationRegistration = cancellationToken.Register(
                    static state => ((MicrophoneCaptureService)state!).RequestStop(cancel: true),
                    this);
            }
            catch (Exception exception)
            {
                startException = exception;
                failedCapture = DetachCaptureUnsafe();
            }
        }

        if (failedCapture is not null)
        {
            DisposeDetachedCapture(failedCapture, deleteAudio: true);
            ExceptionDispatchInfo.Capture(startException!).Throw();
        }

        return await completionTask.ConfigureAwait(false);
    }

    public void Cancel() => RequestStop(cancel: true);

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var shouldStop = false;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _waveIn) ||
                _writer is null || _stopwatch is null || _stopRequested)
            {
                return;
            }

            _writer.Write(e.Buffer, 0, e.BytesRecorded);

            var rms = CalculatePcm16Rms(e.Buffer.AsSpan(0, e.BytesRecorded));
            var frameMilliseconds = Math.Max(
                1,
                e.BytesRecorded * 1000 / Math.Max(1, SampleRate * sizeof(short)));
            var elapsedMilliseconds = _stopwatch.ElapsedMilliseconds;

            if (!_speechStarted && rms < 0.025)
            {
                _noiseFloor = (_noiseFloor * 0.97) + (rms * 0.03);
            }

            var voiceThreshold = _speechStarted
                ? Math.Clamp(_noiseFloor * 1.9, 0.0045, 0.025)
                : Math.Clamp(_noiseFloor * 2.7, 0.0065, 0.032);
            var voiced = rms >= voiceThreshold;
            if (voiced)
            {
                _consecutiveVoicedMilliseconds += frameMilliseconds;
                _totalVoicedMilliseconds += frameMilliseconds;
                _lastVoiceAtMilliseconds = elapsedMilliseconds;
                if (_consecutiveVoicedMilliseconds >= 100)
                {
                    _speechStarted = true;
                }
            }
            else
            {
                _consecutiveVoicedMilliseconds = 0;
            }

            shouldStop =
                _speechStarted &&
                elapsedMilliseconds - _lastVoiceAtMilliseconds >= SilenceAfterSpeechMilliseconds ||
                !_speechStarted && elapsedMilliseconds >= NoSpeechTimeoutMilliseconds ||
                elapsedMilliseconds >= MaximumCaptureMilliseconds;
        }

        if (shouldStop)
        {
            RequestStop(cancel: false);
        }
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        TaskCompletionSource<MicrophoneCaptureResult>? completion;
        string? audioPath;
        TimeSpan duration;
        bool canceled;
        bool hasSpeech;
        DetachedCapture? detached;

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _waveIn))
            {
                return;
            }

            completion = _completion;
            audioPath = _audioPath;
            duration = _stopwatch?.Elapsed ?? TimeSpan.Zero;
            canceled = _cancelRequested;
            hasSpeech = _speechStarted && _totalVoicedMilliseconds >= MinimumVoicedMilliseconds;
            detached = DetachCaptureUnsafe();
        }

        DisposeDetachedCapture(
            detached,
            deleteAudio: canceled || e.Exception is not null || !hasSpeech);

        if (completion is null)
        {
            return;
        }

        if (canceled)
        {
            completion.TrySetCanceled();
        }
        else if (e.Exception is not null)
        {
            completion.TrySetException(new InvalidOperationException(
                "麦克风录音中断了。请检查 Windows 麦克风权限和默认输入设备。",
                e.Exception));
        }
        else if (!hasSpeech || string.IsNullOrWhiteSpace(audioPath))
        {
            completion.TrySetException(new NoSpeechDetectedException(
                "刚才没有听清主人说话，再靠近麦克风一点试试吧。"));
        }
        else
        {
            completion.TrySetResult(new MicrophoneCaptureResult(audioPath, duration));
        }
    }

    private void RequestStop(bool cancel)
    {
        WaveInEvent? waveIn;
        lock (_gate)
        {
            if (_waveIn is null)
            {
                return;
            }

            _cancelRequested |= cancel;
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;
            waveIn = _waveIn;
        }

        _ = Task.Run(() =>
        {
            try
            {
                waveIn.StopRecording();
            }
            catch (Exception exception)
            {
                DetachedCapture? detached = null;
                lock (_gate)
                {
                    if (ReferenceEquals(waveIn, _waveIn))
                    {
                        detached = DetachCaptureUnsafe();
                    }
                }

                if (detached is not null)
                {
                    DisposeDetachedCapture(detached, deleteAudio: true);
                    detached.Completion?.TrySetException(exception);
                }
            }
        });
    }

    private DetachedCapture DetachCaptureUnsafe()
    {
        var detached = new DetachedCapture(
            _waveIn,
            _writer,
            _completion,
            _cancellationRegistration,
            _audioPath);

        _waveIn = null;
        _writer = null;
        _completion = null;
        _stopwatch = null;
        _audioPath = null;
        _cancellationRegistration = default;
        return detached;
    }

    private void DisposeDetachedCapture(DetachedCapture detached, bool deleteAudio)
    {
        detached.CancellationRegistration.Dispose();

        if (detached.WaveIn is not null)
        {
            detached.WaveIn.DataAvailable -= WaveIn_DataAvailable;
            detached.WaveIn.RecordingStopped -= WaveIn_RecordingStopped;
            detached.WaveIn.Dispose();
        }

        detached.Writer?.Dispose();
        if (deleteAudio && !string.IsNullOrWhiteSpace(detached.AudioPath))
        {
            TryDelete(detached.AudioPath);
        }
    }

    private static string GetTemporaryDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiaobianPet",
        "VoiceTemp");

    private static void CleanupStaleRecordings()
    {
        var temporaryDirectory = GetTemporaryDirectory();
        if (!Directory.Exists(temporaryDirectory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleRecordingAge;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         temporaryDirectory,
                         "voice-*.wav",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        TryDelete(path);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static double CalculatePcm16Rms(ReadOnlySpan<byte> bytes)
    {
        var sampleCount = bytes.Length / sizeof(short);
        if (sampleCount == 0)
        {
            return 0;
        }

        double sum = 0;
        for (var index = 0; index + 1 < bytes.Length; index += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index, sizeof(short))) /
                         32768.0;
            sum += sample * sample;
        }

        return Math.Sqrt(sum / sampleCount);
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<bool> TryDeleteWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (TryDelete(path))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)))
                .ConfigureAwait(false);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Task<MicrophoneCaptureResult>? completionTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            completionTask = _completion?.Task;
        }

        Cancel();
        if (completionTask is not null)
        {
            try
            {
                await completionTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        DetachedCapture? detached = null;
        lock (_gate)
        {
            if (_waveIn is not null || _writer is not null || _completion is not null)
            {
                detached = DetachCaptureUnsafe();
            }
        }

        if (detached is not null)
        {
            DisposeDetachedCapture(detached, deleteAudio: true);
            detached.Completion?.TrySetCanceled();
        }
    }
}
