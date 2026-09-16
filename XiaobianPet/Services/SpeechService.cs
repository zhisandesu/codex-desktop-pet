using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed class SpeechService : IDisposable
{
    public const string DefaultVoice = TtsVoiceCatalog.DefaultId;

    private const int SpeakAsyncFlag = 1;
    private const int PurgeBeforeSpeakFlag = 2;
    private const int CancellationPollMilliseconds = 100;
    private const int MaxPendingSpeechRequests = 1;

    private static readonly CancellationToken SupersededCancellationToken = new(canceled: true);

    private readonly BlockingCollection<SpeechRequest> _requests = new(
        new ConcurrentQueue<SpeechRequest>(),
        MaxPendingSpeechRequests);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly ManualResetEventSlim _startupCompleted = new();
    private readonly object _speechControlGate = new();
    private readonly ConcurrentQueue<CancellationTokenSource> _retiredStopCancellations = new();
    private CancellationTokenSource _stopCancellation = new();
    private Thread? _worker;
    private VoiceConfiguration _voiceConfiguration = CreateDefaultVoiceConfiguration();
    private string? _lastCloudLogId;
    private volatile bool _isAvailable;
    private int _lastBackend;
    private int _isDisposed;

    public SpeechService()
    {
        if (!OperatingSystem.IsWindows())
        {
            _startupCompleted.Set();
            return;
        }

        try
        {
            var worker = new Thread(RunSpeechWorker)
            {
                IsBackground = true,
                Name = "XiaobianPet.Speech"
            };
            worker.SetApartmentState(ApartmentState.STA);

            // Publish the worker before it starts. Together with accepting requests while
            // startup is in progress, this removes the old constructor/startup race.
            _worker = worker;
            worker.Start();
        }
        catch
        {
            _worker = null;
            _isAvailable = false;
            _startupCompleted.Set();
        }
    }

    public bool IsAvailable =>
        Volatile.Read(ref _isDisposed) == 0
        && (!_startupCompleted.IsSet || _isAvailable);

    public string ConfiguredVoice => Volatile.Read(ref _voiceConfiguration).Speaker;

    internal string LastBackend => Volatile.Read(ref _lastBackend) switch
    {
        1 => "ArkAgentPlan",
        2 => "WindowsSapi",
        _ => "None"
    };

    internal string? LastCloudLogId => Volatile.Read(ref _lastCloudLogId);

    public void ConfigureVoice(
        string speaker,
        string? instruction = null,
        int speechRate = 0,
        int pitch = 0)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(speaker);
        ValidateTuning(speechRate, pitch);

        var configuration = new VoiceConfiguration(
            speaker.Trim(),
            string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim(),
            speechRate,
            pitch);
        Volatile.Write(ref _voiceConfiguration, configuration);
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        => SpeakAsync(text, performanceInstruction: null, cancellationToken);

    public Task SpeakAsync(
        string text,
        string? performanceInstruction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var voice = Volatile.Read(ref _voiceConfiguration);
        return EnqueueSpeechRequest(
            text.Trim(),
            WithPerformanceInstruction(voice, performanceInstruction),
            preparedAudio: null,
            preparedLogId: null,
            cancellationToken);
    }

    internal async Task<PreparedSpeech?> PrepareAsync(
        string text,
        string? performanceInstruction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var apiKey = WindowsCredentialStore.GetArkAgentPlanApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var voice = WithPerformanceInstruction(
                Volatile.Read(ref _voiceConfiguration),
                performanceInstruction);
            using var arkClient = new ArkAgentPlanTtsClient();
            var audio = await arkClient.SynthesizeAsync(
                    apiKey,
                    text.Trim(),
                    voice.Speaker,
                    voice.Instruction,
                    voice.SpeechRate,
                    voice.Pitch,
                    cancellationToken)
                .ConfigureAwait(false);
            return new PreparedSpeech(text.Trim(), voice, audio, arkClient.LastLogId);
        }
        finally
        {
            apiKey = null;
        }
    }

    internal Task SpeakPreparedAsync(
        PreparedSpeech preparedSpeech,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedSpeech);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return EnqueueSpeechRequest(
            preparedSpeech.Text,
            preparedSpeech.Voice,
            preparedSpeech.Audio,
            preparedSpeech.LogId,
            cancellationToken);
    }

    public void Stop()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        lock (_speechControlGate)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            var cancellation = _stopCancellation;
            var cancellationToken = cancellation.Token;
            _stopCancellation = new CancellationTokenSource();

            cancellation.Cancel();
            _retiredStopCancellations.Enqueue(cancellation);
            CancelPendingRequests(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _isAvailable = false;
        lock (_speechControlGate)
        {
            var stopCancellation = _stopCancellation;
            stopCancellation.Cancel();
            _retiredStopCancellations.Enqueue(stopCancellation);
            _disposeCancellation.Cancel();
            _requests.CompleteAdding();
        }

        var calledFromWorker = _worker == Thread.CurrentThread;
        var workerStopped = _worker is null
                            || (!calledFromWorker && _worker.Join(TimeSpan.FromSeconds(5)));

        CancelPendingRequests(_disposeCancellation.Token);

        // If a native or network call ignores cancellation, leave these small shared
        // objects alive so the background worker cannot race disposed primitives.
        if (workerStopped && !calledFromWorker)
        {
            while (_retiredStopCancellations.TryDequeue(out var cancellation))
            {
                cancellation.Dispose();
            }

            _requests.Dispose();
            _startupCompleted.Dispose();
            _disposeCancellation.Dispose();
        }
    }

    private void RunSpeechWorker()
    {
        object? voiceObject = null;

        try
        {
            voiceObject = TryCreateSapiVoice();
            using var arkClient = new ArkAgentPlanTtsClient();
            var mp3Player = new Mp3Player();

            _isAvailable = true;
            _startupCompleted.Set();

            foreach (var request in _requests.GetConsumingEnumerable())
            {
                ProcessRequest(voiceObject, arkClient, mp3Player, request);

                if (_disposeCancellation.IsCancellationRequested)
                {
                    break;
                }
            }

            if (_disposeCancellation.IsCancellationRequested && voiceObject is not null)
            {
                TryPurge(voiceObject);
            }
        }
        catch (Exception exception)
        {
            _isAvailable = false;
            Debug.WriteLine($"Speech worker stopped: {exception.GetType().Name}");
        }
        finally
        {
            _isAvailable = false;
            _startupCompleted.Set();
            CancelPendingRequests(
                _disposeCancellation.IsCancellationRequested
                    ? _disposeCancellation.Token
                    : SupersededCancellationToken);

            if (voiceObject is not null && Marshal.IsComObject(voiceObject))
            {
                Marshal.FinalReleaseComObject(voiceObject);
            }
        }
    }

    private void ProcessRequest(
        object? voice,
        ArkAgentPlanTtsClient arkClient,
        Mp3Player mp3Player,
        SpeechRequest request)
    {
        try
        {
            if (request.Completion.Task.IsCompleted || request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                return;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken,
                request.ServiceCancellationToken,
                _disposeCancellation.Token);
            var cancellationToken = linkedCancellation.Token;
            Exception? cloudFailure = null;
            Volatile.Write(ref _lastBackend, 0);
            Volatile.Write(ref _lastCloudLogId, null);

            if (request.PreparedAudio is { Length: > 0 } preparedAudio)
            {
                try
                {
                    Volatile.Write(ref _lastCloudLogId, request.PreparedLogId);
                    mp3Player.Play(preparedAudio, cancellationToken);
                    Volatile.Write(ref _lastBackend, 1);
                    request.Completion.TrySetResult();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CompleteCancellation(request);
                    return;
                }
                catch (Exception exception)
                {
                    cloudFailure = exception;
                    TraceCloudFailure(exception);
                }
            }
            else
            {
                var apiKey = WindowsCredentialStore.GetArkAgentPlanApiKey();
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    try
                    {
                        var audio = arkClient.SynthesizeAsync(
                                apiKey,
                                request.Text,
                                request.Voice.Speaker,
                                request.Voice.Instruction,
                                request.Voice.SpeechRate,
                                request.Voice.Pitch,
                                cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                        Volatile.Write(ref _lastCloudLogId, arkClient.LastLogId);
                        mp3Player.Play(audio, cancellationToken);
                        Volatile.Write(ref _lastBackend, 1);
                        request.Completion.TrySetResult();
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        CompleteCancellation(request);
                        return;
                    }
                    catch (Exception exception)
                    {
                        Volatile.Write(
                            ref _lastCloudLogId,
                            (exception as ArkAgentPlanTtsException)?.LogId ?? arkClient.LastLogId);
                        cloudFailure = exception;
                        TraceCloudFailure(exception);
                    }
                    finally
                    {
                        apiKey = null;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (voice is not null)
            {
                try
                {
                    SpeakWithSapi(voice, request, cancellationToken);
                    Volatile.Write(ref _lastBackend, 2);
                    request.Completion.TrySetResult();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CompleteCancellation(request);
                    return;
                }
                catch (Exception sapiFailure)
                {
                    if (cloudFailure is not null)
                    {
                        request.Completion.TrySetException(new AggregateException(
                            "方舟 TTS 和 Windows SAPI 回退均失败。",
                            cloudFailure,
                            sapiFailure));
                    }
                    else
                    {
                        request.Completion.TrySetException(sapiFailure);
                    }

                    return;
                }
            }

            if (cloudFailure is not null)
            {
                request.Completion.TrySetException(new InvalidOperationException(
                    "方舟 TTS 失败，Windows SAPI 回退不可用。",
                    cloudFailure));
                return;
            }

            // This preserves the previous no-op behavior on systems without any
            // configured speech backend.
            request.Completion.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            CompleteCancellation(request);
        }
        catch (Exception exception)
        {
            request.Completion.TrySetException(exception);
        }
        finally
        {
            request.CancellationRegistration.Dispose();
        }
    }

    private static object? TryCreateSapiVoice()
    {
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            return voiceType is null ? null : Activator.CreateInstance(voiceType);
        }
        catch
        {
            return null;
        }
    }

    private void SpeakWithSapi(
        object voiceObject,
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        dynamic voice = voiceObject;
        voice.Speak(request.Text, SpeakAsyncFlag);

        while (!Convert.ToBoolean(voice.WaitUntilDone(CancellationPollMilliseconds)))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            TryPurge(voiceObject);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void TryPurge(object voiceObject)
    {
        try
        {
            dynamic voice = voiceObject;
            voice.Speak(string.Empty, SpeakAsyncFlag | PurgeBeforeSpeakFlag);
        }
        catch
        {
        }
    }

    private void CompleteCancellation(SpeechRequest request)
    {
        var cancellationToken = request.CancellationToken.IsCancellationRequested
            ? request.CancellationToken
            : request.ServiceCancellationToken.IsCancellationRequested
                ? request.ServiceCancellationToken
                : _disposeCancellation.Token;
        request.Completion.TrySetCanceled(cancellationToken);
    }

    private static void TraceCloudFailure(Exception exception)
    {
        var logId = (exception as ArkAgentPlanTtsException)?.LogId;
        var logIdSuffix = string.IsNullOrWhiteSpace(logId) ? string.Empty : $", log id {logId}";
        Debug.WriteLine(
            $"Ark Agent Plan TTS failed ({exception.GetType().Name}{logIdSuffix}); using Windows SAPI.");
    }

    private static void ValidateTuning(int speechRate, int pitch)
    {
        if (speechRate is < -50 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speechRate),
                speechRate,
                "语速必须在 -50 到 100 之间。");
        }

        if (pitch is < -12 or > 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pitch),
                pitch,
                "音高必须在 -12 到 12 之间。");
        }
    }

    private static VoiceConfiguration CreateDefaultVoiceConfiguration()
    {
        var profile = TtsVoiceCatalog.GetOrDefault(DefaultVoice);
        return new VoiceConfiguration(
            profile.Id,
            profile.Instruction,
            profile.SpeechRate,
            profile.Pitch);
    }

    private static VoiceConfiguration WithPerformanceInstruction(
        VoiceConfiguration voice,
        string? performanceInstruction)
    {
        if (string.IsNullOrWhiteSpace(performanceInstruction))
        {
            return voice;
        }

        var instruction = string.IsNullOrWhiteSpace(voice.Instruction)
            ? performanceInstruction.Trim()
            : $"{voice.Instruction} 本句表演：{performanceInstruction.Trim()}";
        return voice with { Instruction = instruction };
    }

    private void EnqueueLatestUnsafe(SpeechRequest request)
    {
        while (!_requests.TryAdd(request))
        {
            if (_requests.IsAddingCompleted)
            {
                throw new InvalidOperationException("语音队列已关闭。");
            }

            if (_requests.TryTake(out var supersededRequest))
            {
                CompletePendingRequest(supersededRequest, SupersededCancellationToken);
            }
        }
    }

    private Task EnqueueSpeechRequest(
        string text,
        VoiceConfiguration voice,
        byte[]? preparedAudio,
        string? preparedLogId,
        CancellationToken cancellationToken)
    {
        // Before startup finishes, requests must be queued instead of being silently
        // dropped because _isAvailable has not been initialized yet.
        if (_startupCompleted.IsSet && !_isAvailable)
        {
            return Task.CompletedTask;
        }

        SpeechRequest request;
        lock (_speechControlGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            request = new SpeechRequest(
                text,
                voice,
                cancellationToken,
                _stopCancellation.Token,
                preparedAudio,
                preparedLogId);
            request.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var pendingRequest = (SpeechRequest)state!;
                    pendingRequest.Completion.TrySetCanceled(pendingRequest.CancellationToken);
                },
                request);

            try
            {
                EnqueueLatestUnsafe(request);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
            {
                CompletePendingRequest(
                    request,
                    _disposeCancellation.IsCancellationRequested
                        ? _disposeCancellation.Token
                        : SupersededCancellationToken);
            }
        }

        return request.Completion.Task;
    }

    private static void CompletePendingRequest(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        request.CancellationRegistration.Dispose();
        request.Completion.TrySetCanceled(cancellationToken);
    }

    private void CancelPendingRequests(CancellationToken cancellationToken)
    {
        while (_requests.TryTake(out var request))
        {
            CompletePendingRequest(request, cancellationToken);
        }
    }

    private sealed class SpeechRequest(
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken,
        CancellationToken serviceCancellationToken,
        byte[]? preparedAudio,
        string? preparedLogId)
    {
        public string Text { get; } = text;

        public VoiceConfiguration Voice { get; } = voice;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public CancellationToken ServiceCancellationToken { get; } = serviceCancellationToken;

        public byte[]? PreparedAudio { get; } = preparedAudio;

        public string? PreparedLogId { get; } = preparedLogId;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    internal sealed class PreparedSpeech
    {
        private readonly byte[] _audio;

        internal PreparedSpeech(
            string text,
            VoiceConfiguration voice,
            byte[] audio,
            string? logId)
        {
            Text = text;
            Voice = voice;
            _audio = audio;
            LogId = logId;
        }

        internal string Text { get; }

        internal VoiceConfiguration Voice { get; }

        internal byte[] Audio => _audio;

        internal string? LogId { get; }
    }

    internal sealed record VoiceConfiguration(
        string Speaker,
        string? Instruction,
        int SpeechRate,
        int Pitch);
}
