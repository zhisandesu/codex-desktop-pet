using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using XiaobianPet.Models;
using XiaobianPet.Services;

namespace XiaobianPet;

public partial class MainWindow : Window
{
    private sealed record AnimationBoundaryWaiter(
        long Generation,
        PetMood Mood,
        PetVisualOwner Owner,
        bool StopAtBoundary,
        TaskCompletionSource<bool> Completion)
    {
        public int RemainingBoundaryCount { get; set; } = 1;
    }

    private sealed record PendingVisualTransition(
        long RequestId,
        PetMood Mood,
        PetVisualOwner Owner,
        CancellationToken CancellationToken,
        Func<bool> Apply,
        TaskCompletionSource<bool> Completion);

    private sealed record SeatedRoutinePlan(TimeSpan VisitDuration);

    private sealed record ActiveFollowRoute(
        PetFollowTargetKind TargetKind,
        DesktopPointerMonitorSnapshot Snapshot);

    private const double BaseRestingWindowWidth = 242;
    private const double BaseRestingWindowHeight = 276;
    // The transparent native host only changes when the user changes pet size.
    // Between setting changes it reserves enough room for the largest animated
    // presentation, so animation playback never clips at the native boundary.
    // RestingWindow* describe the visible resting viewport, not this host.
    private double HostWindowWidth =>
        RestingWindowWidth * PetAnimations.MaximumPresentationScale;
    private double HostWindowHeight =>
        RestingWindowHeight * PetAnimations.MaximumPresentationScale;
    private const double MinimumVerticalActionTravel = 72;

    private double RestingWindowWidth =>
        PetAnimations.CalculateContentScaledWindowDimension(
            BaseRestingWindowWidth,
            _activePetSizeScale);

    private double RestingWindowHeight =>
        PetAnimations.CalculateContentScaledWindowDimension(
            BaseRestingWindowHeight,
            _activePetSizeScale);

    // Keep a short arbitration window so a quick double-click can still wave,
    // while a normal click starts its animation without the system's full
    // (often 500 ms) double-click delay feeling like a missed input.
    private static readonly TimeSpan SingleClickConfirmationDelay =
        TimeSpan.FromMilliseconds(220);
    private static bool ForceRightWalkPreview =>
        string.Equals(
            Environment.GetEnvironmentVariable("XIAOBIAN_WALK_PREVIEW_RIGHT_ONLY"),
            "1",
            StringComparison.Ordinal);
    private static bool ForceFollowPreview =>
        string.Equals(
            Environment.GetEnvironmentVariable("XIAOBIAN_FOLLOW_PREVIEW"),
            "1",
            StringComparison.Ordinal);

    private const long InteractionVoiceCooldownMilliseconds = 8000;
    private const long GrabVoiceCooldownMilliseconds = 2500;
    private const long ImportantVoiceProtectionMilliseconds = 8000;
    private const string PetInteractionHint =
        "不按鼠标在头部左右轻抚可摸头；单击触发互动动画；长按再拖动：按头、翅膀、身体分别抓取；双击打招呼；聊天请点悬浮球";

    private double ViewportWidth =>
        double.IsFinite(PetViewport.Width) && PetViewport.Width > 0
            ? PetViewport.Width
            : PetViewport.ActualWidth;

    private double ViewportHeight =>
        double.IsFinite(PetViewport.Height) && PetViewport.Height > 0
            ? PetViewport.Height
            : PetViewport.ActualHeight;

    private double ViewportInsetX => (HostWindowWidth - ViewportWidth) / 2;
    private double ViewportInsetY => (HostWindowHeight - ViewportHeight) / 2;
    private double RestingViewportInsetX => (HostWindowWidth - RestingWindowWidth) / 2;
    private double RestingViewportInsetY => (HostWindowHeight - RestingWindowHeight) / 2;
    private double RestingViewportLeft => Left + RestingViewportInsetX;
    private double RestingViewportTop => Top + RestingViewportInsetY;
    private double CurrentViewportLeft => Left + ViewportInsetX;
    private double CurrentViewportTop => Top + ViewportInsetY;

    internal Rect GetPetViewportScreenBounds() => new(
        CurrentViewportLeft,
        CurrentViewportTop,
        ViewportWidth,
        ViewportHeight);

    private void SetPetViewportSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            width < RestingWindowWidth || height < RestingWindowHeight ||
            width > HostWindowWidth + 0.01 || height > HostWindowHeight + 0.01)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        PetViewport.Width = width;
        PetViewport.Height = height;
        RepositionAttachedWindows();
    }

    private void SetHostPositionForRestingViewport(double left, double top)
    {
        Left = left - RestingViewportInsetX;
        Top = top - RestingViewportInsetY;
    }

    private void ApplyPetSize(double requestedScale, bool preserveCenter)
    {
        var scale = double.IsFinite(requestedScale)
            ? Math.Clamp(
                requestedScale,
                PetSettings.MinimumPetSizeScale,
                PetSettings.MaximumPetSizeScale)
            : PetSettings.DefaultPetSizeScale;
        var oldRestingWidth = RestingWindowWidth;
        var oldRestingHeight = RestingWindowHeight;
        var centerX = RestingViewportLeft + (oldRestingWidth / 2);
        var centerY = RestingViewportTop + (oldRestingHeight / 2);
        var viewportContentScaleX = (ViewportWidth - PetAnimations.PetImageTotalMargin) /
            Math.Max(1, oldRestingWidth - PetAnimations.PetImageTotalMargin);
        var viewportContentScaleY = (ViewportHeight - PetAnimations.PetImageTotalMargin) /
            Math.Max(1, oldRestingHeight - PetAnimations.PetImageTotalMargin);

        _activePetSizeScale = scale;
        // A size-setting change is the sole allowed native-host resize. Animation
        // transitions only call SetPetViewportSize.
        Width = HostWindowWidth;
        Height = HostWindowHeight;
        SetPetViewportSize(
            PetAnimations.CalculateContentScaledWindowDimension(
                RestingWindowWidth,
                viewportContentScaleX),
            PetAnimations.CalculateContentScaledWindowDimension(
                RestingWindowHeight,
                viewportContentScaleY));

        if (preserveCenter)
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            SetHostPositionForRestingViewport(
                Clamp(
                    centerX - (RestingWindowWidth / 2),
                    virtualLeft,
                    Math.Max(virtualLeft, virtualRight - RestingWindowWidth)),
                Clamp(
                    centerY - (RestingWindowHeight / 2),
                    virtualTop,
                    Math.Max(virtualTop, virtualBottom - RestingWindowHeight)));
        }

        RepositionAttachedWindows();
    }

    private void RepositionAttachedWindows()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.FollowPet();
        }

        _chatOrb?.PositionNearPet();
        _progressBubble?.PositionNear(this);
    }

    private readonly DispatcherTimer _animationTimer = new();
    private readonly DispatcherTimer _movementTimer = new();
    private readonly DispatcherTimer _dragHoldTimer = new();
    private readonly PetPointerGestureRecognizer _pointerGesture = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly PetAnimationPreparationLifetime _animationPreparationLifetime = new();
    private readonly SpeechService _speech = new();
    private readonly MicrophoneCaptureService _microphone = new();
    private readonly LocalWhisperTranscriptionService _transcription = new();
    private readonly ArkAgentPlanAsrClient _agentPlanAsr = new();
    private readonly PetMemoryStore _memoryStore = new();
    private readonly CharacterProfileStore _profileStore = new();
    private readonly ConversationArchiveStore _conversationArchive = new();
    private readonly ConversationContextStore _conversationContext = new();
    private readonly CodexPetDialogueClient _dialogueClient = new();
    private readonly PetDialogueService _dialogue;
    private readonly CancellationTokenSource _dialogueLifetimeCts = new();
    private readonly CodexAppServerClient _codex = new();
    private readonly CodexProgressTracker _progress;
    private readonly CodexDesktopBridge _desktopBridge = new();
    private readonly CodexDesktopTaskMonitor _desktopMonitor;

    private SpriteAtlas? _spriteAtlas;
    private PreparedAnimationSequence? _currentSequence;
    private PetVisualOwner _visualOwner = PetVisualOwner.Persistent;
    private long _playbackGeneration;
    private AnimationBoundaryWaiter? _animationBoundaryWaiter;
    private PendingVisualTransition? _pendingVisualTransition;
    private long _visualTransitionRequestId;
    private PanelWindow? _panel;
    private ChatOrbWindow? _chatOrb;
    private ProgressBubbleWindow? _progressBubble;
    private SettingsWindow? _settingsWindow;
    private PetSettings _settings = new();
    private double _activePetSizeScale = PetSettings.DefaultPetSizeScale;
    private PetMood _persistentMood = PetMood.Idle;
    private PetMood _currentMood = PetMood.Idle;
    private int _frameIndex;
    private bool _isAutonomousMoving;
    private bool _autonomousWindowScaleApplied;
    private double _autonomousWindowCenterOffsetX;
    private double _autonomousWindowCenterOffsetY;
    private bool _seatedWindowScaleApplied;
    private bool _temporarySeatedWindowScaleApplied;
    private bool _clickFlickFallWindowScaleApplied;
    private bool _cuteAngryWindowScaleApplied;
    private bool _isTemporaryMoodActive;
    private bool _isSeatedRoutineActive;
    private bool _isSeatedPreviewActive;
    private volatile bool _seatedExitRequested;
    private int _walkDirection = 1;
    private int _pendingWalkDirection;
    private PetIdleBoundaryAction _pendingIdleBoundaryAction;
    private double _autonomousMovementSpeed;
    private long _nextAutonomousActionAtMilliseconds;
    private long _nextFollowActionAtMilliseconds =
        PetFollowMovementPolicy.UnscheduledDeadline;
    private long _nextIdleGestureAtMilliseconds;
    private long _nextSeatedRoutineAtMilliseconds =
        PetSeatedRoutinePolicy.UnscheduledDeadline;
    private long _autonomousActionEndsAtMilliseconds;
    private long _lastMovementTickMilliseconds;
    private bool _autonomousStopRequested;
    private int _autonomousCompletedCycleCount;
    private ActiveFollowRoute? _activeFollowRoute;
    private PetMood _dragMood = PetMood.Dragged;
    private PetMood? _lastIdleGestureMood;
    private PetGrabRegion _grabRegionAtPointerDown = PetGrabRegion.Body;
    private Point _dragStartScreen;
    private Point _dragAnchorInWindow;
    private double _pointerDownWindowLeft;
    private double _pointerDownWindowTop;
    private double _dragDpiScaleX = 1;
    private double _dragDpiScaleY = 1;
    private bool _isClosing;
    private bool _shutdownReady;
    private CodexDesktopMonitorSnapshot _desktopSnapshot = CodexDesktopMonitorSnapshot.Empty;
    private readonly Dictionary<string, string> _desktopTaskFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexDesktopTask> _desktopTasksById = new(StringComparer.Ordinal);
    private readonly object _dialogueRequestGate = new();
    private readonly object _voicePriorityGate = new();
    private readonly SemaphoreSlim _voiceConversationGate = new(1, 1);
    private bool _desktopSnapshotInitialized;
    private string? _localProgressFingerprint;
    private string? _activeBubbleSemanticKey;
    private string? _lastDisplayedBubbleSemanticKey;
    private CancellationTokenSource? _bubbleHideCts;
    private CancellationTokenSource? _temporaryMoodCts;
    private PetVisualOwner _temporaryMoodOwner = PetVisualOwner.Temporary;
    private long _temporaryMoodPlaybackGeneration;
    private CancellationTokenSource? _singleClickCts;
    private CancellationTokenSource? _clickFlickFallCts;
    private bool _animationHeldForSingleClickConfirmation;
    private CancellationTokenSource? _cuteAngryBoundaryRequestCts;
    private CancellationTokenSource? _cuteAngryCts;
    private CancellationTokenSource? _dragReleaseCts;
    private CancellationTokenSource? _grabReleaseLinePreparationCts;
    private Task<string?>? _preparedGrabReleaseLineTask;
    private PetDialogueIntent? _preparedGrabReleaseIntent;
    private CancellationTokenSource? _runFallVoicePreparationCts;
    private Task<SpeechService.PreparedSpeech?>? _preparedRunFallSpeechTask;
    private string? _preparedRunFallLine;
    private bool _runFallImpactVoiceDelivered;
    private CancellationTokenSource? _automaticDialogueCts;
    private CancellationTokenSource? _interactionDialogueCts;
    private CancellationTokenSource? _messageFeedbackCts;
    private CancellationTokenSource? _seatedRoutineCts;
    private TaskCompletionSource<bool>? _seatedAnimationCompletion;
    private TaskCompletionSource<bool>? _seatedRoutineCompletion;
    private TaskCompletionSource<bool>? _seatedIdleLoopCompletion;
    private long _seatedIdleLoopEndsAtMilliseconds = long.MaxValue;
    private long _characterBubbleSequence;
    private long _messageFeedbackAvailableAtMilliseconds;
    private long _interactionVoiceAvailableAtMilliseconds;
    private long _grabVoiceAvailableAtMilliseconds;
    private long _importantVoiceProtectionUntilMilliseconds;
    private long _dragVoiceStartedAtMilliseconds;
    private bool _dragInteractionLineDelivered;
    private bool _dragMidpointVoiceEvaluated;
    private int _importantSpeechActiveCount;
    private long _multiTaskCuteAngryAvailableAtMilliseconds;
    private int _lastCuteAngryActiveTaskCount = -1;

    private bool IsClickFlickFallPendingOrActive =>
        _clickFlickFallCts is { IsCancellationRequested: false };

    private bool IsSingleClickConfirmationPending =>
        _singleClickCts is { IsCancellationRequested: false };

    private bool IsClickInteractionReserved =>
        IsSingleClickConfirmationPending || IsClickFlickFallPendingOrActive;

    private bool IsCuteAngryPendingOrActive =>
        _cuteAngryCts is { IsCancellationRequested: false };

    public MainWindow()
    {
        InitializeComponent();
        _dialogue = new PetDialogueService(
            _dialogueClient,
            _memoryStore,
            _profileStore,
            _conversationArchive,
            _conversationContext);
        InitializeTtsVoiceMenu();
        _animationTimer.Tick += AnimationTimer_Tick;
        _movementTimer.Interval = TimeSpan.FromMilliseconds(16);
        _movementTimer.Tick += MovementTimer_Tick;
        _dragHoldTimer.Interval =
            TimeSpan.FromMilliseconds(PetPointerGestureRecognizer.DefaultHoldMilliseconds);
        _dragHoldTimer.Tick += DragHoldTimer_Tick;

        _progress = new CodexProgressTracker(_codex);
        _progress.ProgressChanged += Codex_ProgressChanged;

        _desktopMonitor = new CodexDesktopTaskMonitor(_desktopBridge, []);
        _desktopMonitor.SnapshotChanged += DesktopMonitor_SnapshotChanged;
        _dialogueClient.CompanionThreadObserved += DialogueClient_CompanionThreadObserved;

        _codex.ConnectionStateChanged += Codex_ConnectionStateChanged;
        _codex.ApprovalRequested += Codex_ApprovalRequested;
        _codex.TurnCompleted += Codex_TurnCompleted;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = _settingsStore.Load();
            await _settingsStore.SaveAsync(_settings);
            ApplyPetSize(_settings.PetSizeScale, preserveCenter: false);
            RestoreWindowPosition();
            SyncChatOrbVisibility();
            _progressBubble?.HideBubble();
            ((App)Application.Current).NotifyMainWindowReady(this);
            TtsMenuItem.IsChecked = _settings.TtsEnabled;
            DynamicDialogueMenuItem.IsChecked = _settings.DynamicDialogueEnabled;
            TaskContextFeedbackMenuItem.IsChecked = _settings.TaskContextFeedbackEnabled;
            ShareMemoryMenuItem.IsChecked = _settings.ShareMemoryWithDialogue;
            RefreshMemoryMenu();
            var selectedVoice = TtsVoiceCatalog.GetOrDefault(_settings.TtsVoice);
            _speech.ConfigureVoice(
                selectedVoice.Id,
                selectedVoice.Instruction,
                selectedVoice.SpeechRate,
                selectedVoice.Pitch);
            RefreshTtsVoiceMenu();
            ApplyUiOpacity(_settings.UiOpacity);

            var animationsRoot = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "animations");
            _spriteAtlas = new SpriteAtlas(animationsRoot);
            SetPersistentMood(PetMood.Idle);
            var now = Environment.TickCount64;
            ScheduleNextAutonomousAction(now);
            ScheduleNextFollowAction(now, PetFollowScheduleKind.Initial);
            ScheduleNextIdleGesture(now);
            ScheduleInitialSeatedRoutine(now);
            _movementTimer.Start();
            _ = WarmUpAnimationSequencesAsync(
                _spriteAtlas,
                _dialogueLifetimeCts.Token);
            _ = PrepareWorkloadAnimationsAsync(animationsRoot);
            _ = PrepareSelfPlayAnimationsAsync();
        }
        catch (Exception ex)
        {
            _progressBubble?.HideBubble();
            SetStatus("启动失败", Brushes.IndianRed);
            SetPersistentMood(PetMood.Failed);
            App.WriteErrorLog("Window initialization failure", ex);
            try
            {
                EnsurePanel();
                _panel?.ShowStartupError(ex.Message);
                _panel?.ShowNearPet();
            }
            catch (Exception panelException)
            {
                App.WriteErrorLog("Startup error panel failure", panelException);
            }

            return;
        }

        var taskIdCandidates = new List<string>();
        var environmentTaskId = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        if (!string.IsNullOrWhiteSpace(environmentTaskId))
        {
            taskIdCandidates.Add(environmentTaskId);
        }

        try
        {
            await _codex.ConnectAsync();
            var localThreads = await _codex.ListThreadsAsync();
            taskIdCandidates.AddRange(localThreads
                .Where(thread =>
                    !CompanionThreadIdentity.IsCompanionSource(thread.ThreadSource) &&
                    !CompanionThreadIdentity.IsCompanionWorkspace(thread.Cwd))
                .Select(thread => thread.Id));
        }
        catch (Exception ex)
        {
            // The independent app-server is only the backend for explicitly creating a
            // new task.  A failure here must not prevent the read-only desktop task bridge
            // (or the pet itself) from starting.
            App.WriteErrorLog("Independent Codex app-server startup failure", ex);
        }

        _ = WarmUpPetDialogueAsync();

        try
        {
            _desktopMonitor.SetTaskIdCandidates(taskIdCandidates);
            await _desktopMonitor.StartAsync();
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Codex desktop task monitor startup failure", ex);
            _desktopSnapshot = CodexDesktopMonitorSnapshot.Empty with
            {
                Error = ex.GetBaseException().Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        RenderDesktopSnapshot(_desktopSnapshot);
    }

    private static async Task WarmUpAnimationSequencesAsync(
        SpriteAtlas spriteAtlas,
        CancellationToken cancellationToken)
    {
        try
        {
            await AnimationSequencePreloader.WarmUpAsync(
                    spriteAtlas,
                    PetAnimations.AnimationWarmUpMoods,
                    static (mood, exception) => App.WriteErrorLog(
                        $"Animation background warm-up failure ({mood})",
                        exception),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Warm-up is an optimization. It must never alter the current image,
            // mood, owner, scale, timers, or startup success state.
            App.WriteErrorLog("Animation background warm-up failure", exception);
        }
    }

    private async Task WarmUpPetDialogueAsync()
    {
        try
        {
            await _dialogue.WarmUpAsync(_dialogueLifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_dialogueLifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Dynamic dialogue is an enhancement: a failed subscription/model check
            // must never stop the pet or its local fallback lines from working.
            App.WriteErrorLog("Codex Luna dialogue warm-up failure", ex);
        }
    }

    private void DialogueClient_CompanionThreadObserved(
        object? sender,
        CompanionThreadObservation observed)
    {
        if (_isClosing)
        {
            return;
        }

        try
        {
            _desktopMonitor.AddExcludedTaskId(observed.ThreadId);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RestoreWindowPosition()
    {
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            var workArea = SystemParameters.WorkArea;
            SetHostPositionForRestingViewport(
                Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - RestingWindowWidth)),
                Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - RestingWindowHeight)));
        }
        else
        {
            SetHostPositionForRestingViewport(
                SystemParameters.WorkArea.Right - RestingWindowWidth - 28,
                SystemParameters.WorkArea.Bottom - RestingWindowHeight - 22);
        }

        _progressBubble?.PositionNear(this);
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        var sequence = _currentSequence;
        if (sequence is null)
        {
            return;
        }

        var definition = PetAnimations.Definitions[sequence.VisualMood];
        var frameCount = sequence.Frames.Count;
        var frameAdvance = PetAnimationPlaybackPolicy.ClassifyAdvance(
            _frameIndex,
            frameCount,
            definition.Loop);
        _frameIndex++;
        if (frameAdvance != PetAnimationFrameAdvance.NextFrame)
        {
            if (frameAdvance == PetAnimationFrameAdvance.LoopBoundary)
            {
                // A released single click still spends a short interval in
                // double-click arbitration. If the current loop reaches its
                // boundary during that interval, hold its real last frame
                // instead of wrapping or publishing another queued visual for
                // a single frame immediately before ClickFlickFall starts.
                if (IsSingleClickConfirmationPending)
                {
                    _frameIndex = frameCount - 1;
                    _animationHeldForSingleClickConfirmation = true;
                    _animationTimer.Stop();
                    return;
                }

                var seatedIdleLoopDue =
                    _isSeatedRoutineActive &&
                    _currentMood == PetMood.SeatedIdle &&
                    _seatedIdleLoopCompletion is not null &&
                    PetSeatedRoutinePolicy.ShouldReleaseIdleLoopAtBoundary(
                        _seatedExitRequested,
                        Environment.TickCount64,
                        _seatedIdleLoopEndsAtMilliseconds);
                var stopAtBoundary =
                    IsCurrentAnimationBoundaryWaiter(_animationBoundaryWaiter) &&
                    _animationBoundaryWaiter!.StopAtBoundary &&
                    PetAnimationPlaybackPolicy.ShouldStopAtRequestedBoundary(
                        _animationBoundaryWaiter.RemainingBoundaryCount) ||
                    _isSeatedRoutineActive &&
                    _currentMood == PetMood.SeatedIdle &&
                    _seatedAnimationCompletion is not null ||
                    seatedIdleLoopDue;
                if (stopAtBoundary)
                {
                    _frameIndex = frameCount - 1;
                    _animationTimer.Stop();
                }

                if (_isSeatedRoutineActive && _currentMood == PetMood.SeatedIdle)
                {
                    _seatedAnimationCompletion?.TrySetResult(true);
                    if (seatedIdleLoopDue)
                    {
                        _seatedIdleLoopCompletion?.TrySetResult(true);
                    }
                }

                CompleteCurrentAnimationBoundary();
                if (TryApplyPendingVisualTransitionAtBoundary() ||
                    TryCompleteAutonomousLoopBoundary())
                {
                    return;
                }

                if (stopAtBoundary)
                {
                    return;
                }

                if (TryStartPendingIdleBoundaryAction())
                {
                    return;
                }

                if (!ApplyPendingWalkDirectionAtLoopBoundary())
                {
                    _frameIndex = 0;
                    PetImage.Source = sequence.Frames[0];
                }

                return;
            }
            else
            {
                _frameIndex = frameCount - 1;
                PetImage.Source = sequence.Frames[_frameIndex];
                _animationTimer.Stop();
                if (_currentMood == PetMood.RecycleBinPeek)
                    _recycleBinCompletedGeneration = _playbackGeneration;

                if (IsSingleClickConfirmationPending)
                {
                    _animationHeldForSingleClickConfirmation = true;
                    return;
                }

                if (_isSeatedRoutineActive && PetAnimations.IsSeatedMood(_currentMood))
                {
                    _seatedAnimationCompletion?.TrySetResult(true);
                }

                var completedBoundaryWaiter = CompleteCurrentAnimationBoundary();
                if (TryApplyPendingVisualTransitionAtBoundary())
                {
                    return;
                }

                // False may mean Hold, not Empty. Keep the real final frame on
                // screen until the pointer or forced owner releases the request.
                if (_pendingVisualTransition is not null)
                {
                    return;
                }

                if (_pointerGesture.IsPressed)
                {
                    return;
                }

                if (completedBoundaryWaiter ||
                    _visualOwner == PetVisualOwner.WorkloadRoutine ||
                    _visualOwner == PetVisualOwner.ClickFlickFall ||
                    _isTemporaryMoodActive ||
                    _isSeatedRoutineActive)
                {
                    return;
                }

                if (_visualOwner == PetVisualOwner.Autonomous && _isAutonomousMoving)
                {
                    StopAutonomousMovement(restorePersistentMood: true);
                    return;
                }

                ApplyMoodAtAnimationBoundary(_persistentMood, PetVisualOwner.Persistent);
                if (PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood))
                {
                    ScheduleNextIdleGesture(Environment.TickCount64);
                }
                return;
            }
        }

        PetImage.Source = sequence.Frames[_frameIndex];
        TryDeliverRunFallImpactReaction();
    }

    private bool TryCompleteAutonomousLoopBoundary()
    {
        if (!_isAutonomousMoving || _visualOwner != PetVisualOwner.Autonomous)
        {
            return false;
        }

        _autonomousCompletedCycleCount++;
        if (IsClickFlickFallPendingOrActive || IsCuteAngryPendingOrActive)
        {
            return false;
        }

        if (_pointerGesture.IsPressed)
        {
            return false;
        }

        if (!PetAnimations.ShouldStopAutonomousAtLoopBoundary(
                _currentMood,
                _autonomousCompletedCycleCount,
                _autonomousStopRequested,
                enforceWalkingCycleLimit: _activeFollowRoute is null))
        {
            return false;
        }

        StopAutonomousMovement(restorePersistentMood: true);
        return true;
    }

    private bool IsCurrentAnimationBoundaryWaiter(AnimationBoundaryWaiter? waiter) =>
        waiter is not null &&
        waiter.Generation == _playbackGeneration &&
        waiter.Mood == _currentMood &&
        waiter.Owner == _visualOwner;

    private bool CompleteCurrentAnimationBoundary()
    {
        var waiter = _animationBoundaryWaiter;
        if (!IsCurrentAnimationBoundaryWaiter(waiter))
        {
            return false;
        }

        var remainingBoundaryCount =
            PetAnimationPlaybackPolicy.ConsumeRequestedBoundary(
                waiter!.RemainingBoundaryCount);
        if (remainingBoundaryCount > 0)
        {
            waiter.RemainingBoundaryCount = remainingBoundaryCount;
            return false;
        }

        _animationBoundaryWaiter = null;
        waiter.Completion.TrySetResult(true);
        return true;
    }

    private void CancelAnimationBoundary(PetVisualOwner? owner = null)
    {
        var waiter = _animationBoundaryWaiter;
        if (waiter is null || (owner is not null && waiter.Owner != owner))
        {
            return;
        }

        _animationBoundaryWaiter = null;
        waiter.Completion.TrySetCanceled();
    }

    private bool IsAtCurrentAnimationBoundary()
    {
        var sequence = _currentSequence;
        if (sequence is null)
        {
            return true;
        }

        return PetAnimationPlaybackPolicy.IsReadyForNormalTransition(
            _frameIndex,
            sequence.Frames.Count,
            _animationTimer.IsEnabled,
            PetImage.Source is not null);
    }

    private void ResumeAnimationHeldForSingleClickConfirmation()
    {
        if (!_animationHeldForSingleClickConfirmation)
        {
            return;
        }

        _animationHeldForSingleClickConfirmation = false;
        if (_isClosing || _pointerGesture.IsPressed ||
            _currentSequence is null || PetImage.Source is null)
        {
            return;
        }

        // Keep the held last-frame index intact. The next timer tick will pass
        // through the real loop/one-shot boundary once, completing any seated or
        // temporary waiter that the click confirmation deliberately postponed.
        _animationTimer.Start();
    }

    private Task<bool> RequestNormalVisualTransition(
        PetMood mood,
        PetVisualOwner owner,
        CancellationToken cancellationToken,
        Func<bool> apply)
    {
        if (PetVisualTransitionPolicy.GetTiming(owner) !=
            PetVisualTransitionTiming.AnimationBoundary)
        {
            throw new ArgumentOutOfRangeException(
                nameof(owner),
                owner,
                "强制交互必须走立即转场入口。");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        var decision = PetVisualTransitionQueuePolicy.DecideRequest(
            _visualOwner,
            owner,
            IsClickInteractionReserved,
            IsAtCurrentAnimationBoundary(),
            _pointerGesture.IsPressed);
        if (decision == PetNormalTransitionRequestDecision.Reject)
        {
            return Task.FromResult(false);
        }

        if (decision == PetNormalTransitionRequestDecision.ApplyNow)
        {
            CancelPendingVisualTransition();
            return Task.FromResult(apply());
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PendingVisualTransition(
            ++_visualTransitionRequestId,
            mood,
            owner,
            cancellationToken,
            apply,
            completion);
        CancelPendingVisualTransition();
        _pendingVisualTransition = request;
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    private Task<bool> RequestAmbientCuteAngryAtBoundary(
        CancellationToken cancellationToken,
        Func<bool> apply)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        var decision = PetVisualTransitionQueuePolicy.DecideAmbientCuteAngryRequest(
            _visualOwner,
            IsAtCurrentAnimationBoundary(),
            _pointerGesture.IsPressed);
        if (decision == PetNormalTransitionRequestDecision.Reject)
        {
            return Task.FromResult(false);
        }

        if (decision == PetNormalTransitionRequestDecision.ApplyNow)
        {
            CancelPendingVisualTransition();
            return Task.FromResult(apply());
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PendingVisualTransition(
            ++_visualTransitionRequestId,
            PetMood.CuteAngry,
            PetVisualOwner.CuteAngryBoundaryRequest,
            cancellationToken,
            apply,
            completion);
        CancelPendingVisualTransition();
        _pendingVisualTransition = request;
        return completion.Task.WaitAsync(cancellationToken);
    }

    private void CancelPendingVisualTransition(PetVisualOwner? owner = null)
    {
        var pending = _pendingVisualTransition;
        if (pending is null || (owner is not null && pending.Owner != owner))
        {
            return;
        }

        _pendingVisualTransition = null;
        pending.Completion.TrySetResult(false);
    }

    private bool TryApplyPendingVisualTransitionAtBoundary()
    {
        var pending = _pendingVisualTransition;
        if (pending is null)
        {
            return false;
        }

        var decision = PetVisualTransitionQueuePolicy.DecidePendingAtBoundary(
            _visualOwner,
            pending.Owner,
            IsClickInteractionReserved,
            _pointerGesture.IsPressed,
            pending.CancellationToken.IsCancellationRequested);
        if (decision == PetPendingTransitionDecision.DiscardCanceled)
        {
            _pendingVisualTransition = null;
            pending.Completion.TrySetResult(false);
            return false;
        }

        // Pressing alone is neither a click nor a drag. Preserve the queued
        // ordinary request until the recognizer resolves the gesture instead of
        // consuming it at a boundary that happens beneath the held pointer.
        if (decision == PetPendingTransitionDecision.Hold)
        {
            return false;
        }

        _pendingVisualTransition = null;
        try
        {
            var applied = pending.Apply();
            pending.Completion.TrySetResult(applied);
            return applied;
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetResult(false);
            App.WriteErrorLog(
                $"Animation boundary transition failure ({pending.Mood}, request {pending.RequestId})",
                exception);
            return false;
        }
    }

    private bool ApplyForcedInteractionMood(
        PetMood mood,
        PetVisualOwner owner,
        PreparedAnimationSequence? preparedSequence = null,
        bool preservePresentationScale = false)
    {
        if (!PetVisualTransitionPolicy.IsForcedInteraction(owner))
        {
            throw new ArgumentOutOfRangeException(
                nameof(owner),
                owner,
                "普通动画必须等到真实帧边界。");
        }

        if (!PetVisualTransitionPolicy.CanReplace(
                _visualOwner,
                owner,
                IsClickFlickFallPendingOrActive))
        {
            return false;
        }

        // The interaction owns the screen immediately, but an already queued
        // ordinary request remains next in line for the interaction's release.
        return ApplyMoodCore(
            mood,
            owner,
            preparedSequence,
            preservePresentationScale: preservePresentationScale);
    }

    private bool ApplyMoodAtAnimationBoundary(
        PetMood mood,
        PetVisualOwner owner,
        PreparedAnimationSequence? preparedSequence = null)
    {
        if (PetVisualTransitionPolicy.GetTiming(owner) !=
            PetVisualTransitionTiming.AnimationBoundary)
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        if (!IsAtCurrentAnimationBoundary())
        {
            throw new InvalidOperationException(
                $"普通动画 {mood} 只能在当前动画的真实帧边界开始。");
        }

        // A held request may be waiting for a pointer/click/grab lifecycle to
        // resolve. Internal boundary work can continue without erasing it.
        return ApplyMoodCore(mood, owner, preparedSequence);
    }

    private bool EndGrabWithPersistentMood(PreparedAnimationSequence preparedSequence)
    {
        if (_visualOwner != PetVisualOwner.Grab)
        {
            return false;
        }

        var satisfiedPending =
            _pendingVisualTransition is
            {
                Owner: PetVisualOwner.Persistent
            } pending &&
            pending.Mood == _persistentMood
                ? pending
                : null;
        var applied = ApplyMoodCore(
            _persistentMood,
            PetVisualOwner.Persistent,
            preparedSequence,
            releaseGrab: true);
        if (applied && satisfiedPending is not null &&
            ReferenceEquals(_pendingVisualTransition, satisfiedPending))
        {
            _pendingVisualTransition = null;
            satisfiedPending.Completion.TrySetResult(true);
        }

        return applied;
    }

    public void SetPersistentMood(PetMood mood)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetPersistentMood(mood));
            return;
        }

        if (IsWorkloadRoutineActive)
        {
            // Keep live task truth without replacing the desk/rest clip or
            // overwriting its prepared next-boundary request.
            _persistentMood = mood;
            return;
        }

        // Idle, Working, Review, Waiting, and Failed use the same ambient idle
        // artwork. Status updates inside that family may update the persistent
        // truth, but must not tear down a seated gesture already on screen.
        if (_isSeatedRoutineActive &&
            PetSeatedRoutinePolicy.IsAmbientIdleMood(mood))
        {
            _persistentMood = mood;
            return;
        }

        if (_persistentMood == mood &&
            (_currentMood == mood || _currentMood == PetMood.PantingAfterEffort) &&
            PetImage.Source is not null)
        {
            CancelPendingVisualTransition(PetVisualOwner.Persistent);
            return;
        }

        var previousPersistentMood = _persistentMood;
        _persistentMood = mood;
        var previousWasAmbientIdle =
            PetSeatedRoutinePolicy.IsAmbientIdleMood(previousPersistentMood);
        var nextIsAmbientIdle =
            PetSeatedRoutinePolicy.IsAmbientIdleMood(mood);

        // A genuinely non-ambient state should not make a seated pose disappear
        // in the middle of a gesture. Reach the real final frame, stand up, then
        // hand visual ownership to the newest persistent target.
        if (_isSeatedRoutineActive)
        {
            RequestGracefulSeatedExit();
            _nextIdleGestureAtMilliseconds = long.MaxValue;
            _nextSeatedRoutineAtMilliseconds = long.MaxValue;
            return;
        }

        // Progress, message, and task callbacks may arrive while the click
        // reaction is playing. Record the newest truth, but never let those
        // callbacks replace the exclusive visual. Completion restores this value.
        if (IsClickInteractionReserved)
        {
            if (!nextIsAmbientIdle)
            {
                _nextIdleGestureAtMilliseconds = long.MaxValue;
            }

            _ = RequestPersistentMoodTransition(mood);
            return;
        }

        // Keep the newest task truth, but never replace the exclusive cute-angry
        // one-shot before its real final frame. Its completion restores this mood.
        if (IsCuteAngryPendingOrActive)
        {
            if (!nextIsAmbientIdle)
            {
                _nextIdleGestureAtMilliseconds = long.MaxValue;
            }

            _ = RequestPersistentMoodTransition(mood);
            return;
        }

        // A completion signal can advance from Review to Idle while the short
        // recovery animation is still playing. Keep the one-shot uninterrupted;
        // its natural final frame will restore whichever persistent state is newest.
        if (_currentMood == PetMood.PantingAfterEffort &&
            mood is PetMood.Review or PetMood.Idle)
        {
            return;
        }

        if (!nextIsAmbientIdle)
        {
            _nextIdleGestureAtMilliseconds = long.MaxValue;
        }
        else if (!previousWasAmbientIdle)
        {
            var now = Environment.TickCount64;
            ScheduleNextIdleGesture(now);
        }

        if (PetSeatedRoutinePolicy.IsAmbientIdleMood(mood))
        {
            EnsureSeatedRoutineScheduled(Environment.TickCount64);
        }

        var shouldPlayPostEffortPanting =
            previousPersistentMood == PetMood.Working &&
            mood is PetMood.Review or PetMood.Idle &&
            !_pointerGesture.IsPressed &&
            !_isTemporaryMoodActive &&
            !_isAutonomousMoving &&
            _spriteAtlas?.UsesExternalFrames(PetMood.PantingAfterEffort) == true;
        if (shouldPlayPostEffortPanting)
        {
            _ = RequestPersistentMoodTransition(PetMood.PantingAfterEffort);
            return;
        }

        if (_visualOwner == PetVisualOwner.Persistent &&
            _currentSequence?.VisualMood == PetMood.Idle &&
            PetAnimations.ResolveVisualMood(mood) == PetMood.Idle &&
            _temporaryMoodCts is null && !_isTemporaryMoodActive)
        {
            // Working/Waiting/Review/Failed/Idle share the same video. Updating
            // task semantics must not restart its frame clock or consume the
            // wrap reserved for the next gesture. Effort recovery above remains
            // a real distinct animation and deliberately does not take this path.
            var prepared = _spriteAtlas!.PrepareSequence(mood);
            _currentMood = mood;
            _currentSequence = prepared;
            CancelPendingVisualTransition(PetVisualOwner.Persistent);
            if (_nextIdleGestureAtMilliseconds == long.MaxValue)
            {
                ScheduleNextIdleGesture(Environment.TickCount64);
            }
            return;
        }

        if (_temporaryMoodCts is null &&
            !_isTemporaryMoodActive &&
            (_currentMood != mood || PetImage.Source is null))
        {
            _ = RequestPersistentMoodTransition(mood);
        }
        else if (_temporaryMoodCts is null &&
                 !_isTemporaryMoodActive &&
                 _visualOwner == PetVisualOwner.Persistent &&
                 _currentMood == mood)
        {
            // The newest persistent truth already matches the screen. Remove a
            // stale older request instead of letting it apply after release.
            CancelPendingVisualTransition(PetVisualOwner.Persistent);
        }
    }

    private Task<bool> RequestPersistentMoodTransition(
        PetMood mood,
        Action? beforeApply = null)
    {
        var spriteAtlas = _spriteAtlas;
        if (spriteAtlas is null)
        {
            return Task.FromResult(false);
        }

        var prepared = spriteAtlas.PrepareSequence(mood);
        return RequestNormalVisualTransition(
            mood,
            PetVisualOwner.Persistent,
            CancellationToken.None,
            () =>
            {
                if (_isAutonomousMoving)
                {
                    StopAutonomousMovement(restorePersistentMood: false);
                }

                beforeApply?.Invoke();
                return ApplyMoodCore(mood, PetVisualOwner.Persistent, prepared);
            });
    }

    public Task PlayTemporaryMoodAsync(PetMood mood, TimeSpan duration) =>
        PlayTemporaryMoodAsync(
            mood,
            duration,
            CancellationToken.None,
            completeAtAnimationBoundary: false,
            owner: PetVisualOwner.Temporary);

    private async Task PlayTemporaryMoodAsync(
        PetMood mood,
        TimeSpan duration,
        CancellationToken cancellationToken,
        bool completeAtAnimationBoundary,
        int completionBoundaryCount = 1,
        PetVisualOwner owner = PetVisualOwner.Temporary,
        Action? onActivated = null,
        Func<bool>? canActivate = null,
        bool abandonIfSeated = false)
    {
        // Workload clips form a pose-dependent desk/floor chain. Only an actual
        // grab may interrupt; do not queue a stale preview over its next clip.
        if (IsWorkloadRoutineActive) return;
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (completionBoundaryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completionBoundaryCount));
        }

        if (owner is not PetVisualOwner.Temporary and not PetVisualOwner.ClickInteraction)
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _temporaryMoodCts, cts);
        CancelWithoutThrow(previous);
        var playbackGeneration = 0L;
        AnimationBoundaryWaiter? boundaryWaiter = null;
        var watchdogDuration = TimeSpan.Zero;

        try
        {
            if (!PetVisualTransitionPolicy.CanInterruptSeatedRoutine(owner))
            {
                var abandon = false;
                Task seatedExit = Task.CompletedTask;
                await RunOnUiThreadAsync(() =>
                {
                    if (!_isSeatedRoutineActive)
                    {
                        return;
                    }

                    if (abandonIfSeated)
                    {
                        abandon = true;
                        return;
                    }

                    seatedExit = RequestGracefulSeatedExit();
                });
                if (abandon)
                {
                    return;
                }

                await seatedExit.WaitAsync(cts.Token);
            }

            Task<bool> activationTask = Task.FromResult(false);
            await RunOnUiThreadAsync(() =>
            {
                if (cts.IsCancellationRequested ||
                    _isClosing)
                {
                    return;
                }

                var prepared = _spriteAtlas!.PrepareSequence(mood);
                bool ApplyTemporaryTransition()
                {
                    if (!ReferenceEquals(_temporaryMoodCts, cts) ||
                        cts.IsCancellationRequested ||
                        canActivate is not null && !canActivate() ||
                        _pointerGesture.IsPressed ||
                        _isClosing ||
                        IsClickInteractionReserved ||
                        IsCuteAngryPendingOrActive)
                    {
                        return false;
                    }

                    var isClickFlickFall = mood == PetMood.ClickFlickFall;
                    if (PetVisualTransitionPolicy.CanInterruptSeatedRoutine(owner))
                    {
                        CancelSeatedRoutine(restoreWindowScale: !isClickFlickFall);
                    }

                    StopAutonomousMovement(
                        restorePersistentMood: false,
                        restoreWindowScale: !isClickFlickFall);
                    if (mood == PetMood.JumpingDown)
                    {
                        ApplyAutonomousContentScale(
                            PetAnimations.JumpingContentScale,
                            PetAnimations.JumpingCenterOffsetX,
                            PetAnimations.JumpingCenterOffsetY);
                    }
                    else if (IsRunFallMood(mood))
                    {
                        ApplyAutonomousContentScale(
                            PetAnimations.RunFallContentScale);
                    }
                    else if (!isClickFlickFall)
                    {
                        RestoreWindowSizeAfterAutonomousMovement();
                    }

                    if (PetAnimations.IsSeatedMood(mood))
                    {
                        ApplyTemporarySeatedWindowScale();
                    }
                    else if (!isClickFlickFall)
                    {
                        RestoreWindowSizeAfterTemporarySeatedMood();
                    }

                    if (!isClickFlickFall)
                    {
                        RestoreWindowSizeAfterClickFlickFall();
                    }

                    var applied = PetVisualTransitionPolicy.IsForcedInteraction(owner)
                        ? ApplyForcedInteractionMood(
                            mood,
                            owner,
                            prepared,
                            preservePresentationScale: isClickFlickFall)
                        : ApplyMoodCore(
                            mood,
                            owner,
                            prepared,
                            preservePresentationScale: isClickFlickFall);
                    if (!applied)
                    {
                        return false;
                    }

                    // Publish F000 before changing the transparent HWND. This
                    // prevents the previous sprite from being briefly stretched
                    // or shrunk while the click reaction takes over.
                    if (isClickFlickFall)
                    {
                        ApplyClickFlickFallWindowScale();
                    }

                    if (mood == PetMood.CuteAngry)
                    {
                        ApplyCuteAngryWindowScale();
                    }
                    else if (mood == PetMood.DragLanding)
                    {
                        ApplyDragLandingWindowScale();
                    }

                    _isTemporaryMoodActive = true;
                    _temporaryMoodOwner = owner;
                    _temporaryMoodPlaybackGeneration = _playbackGeneration;
                    playbackGeneration = _playbackGeneration;
                    onActivated?.Invoke();

                    if (!PetAnimations.Definitions[prepared.VisualMood].Loop ||
                        completeAtAnimationBoundary)
                    {
                        var requiredBoundaryCount =
                            PetAnimations.Definitions[prepared.VisualMood].Loop &&
                            completeAtAnimationBoundary
                                ? completionBoundaryCount
                                : 1;
                        var completion = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        boundaryWaiter = new AnimationBoundaryWaiter(
                            playbackGeneration,
                            mood,
                            owner,
                            StopAtBoundary: true,
                            completion)
                        {
                            RemainingBoundaryCount = requiredBoundaryCount
                        };
                        _animationBoundaryWaiter = boundaryWaiter;
                        watchdogDuration = PetAnimations.CalculateAnimationWatchdog(
                            checked(prepared.Frames.Count * requiredBoundaryCount),
                            prepared.FrameDuration);
                    }

                    return true;
                }

                activationTask = PetVisualTransitionPolicy.IsForcedInteraction(owner)
                    ? Task.FromResult(ApplyTemporaryTransition())
                    : RequestNormalVisualTransition(
                        mood,
                        owner,
                        cts.Token,
                        ApplyTemporaryTransition);
            });

            if (await activationTask)
            {
                if (boundaryWaiter is not null)
                {
                    await AwaitAnimationBoundaryAsync(
                        boundaryWaiter.Completion,
                        watchdogDuration,
                        mood,
                        cts.Token);
                }
                else
                {
                    await Task.Delay(duration, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_temporaryMoodCts, cts))
                {
                    return;
                }

                _temporaryMoodCts = null;
                var activeOwner = _temporaryMoodOwner;
                var activePlaybackGeneration = _temporaryMoodPlaybackGeneration;
                _isTemporaryMoodActive = false;
                _temporaryMoodOwner = PetVisualOwner.Temporary;
                _temporaryMoodPlaybackGeneration = 0;
                if (_visualOwner == activeOwner &&
                    _playbackGeneration == activePlaybackGeneration &&
                    !_pointerGesture.IsPressed &&
                    !_isClosing &&
                    !IsClickInteractionReserved &&
                    !IsCuteAngryPendingOrActive)
                {
                    _ = RequestPersistentMoodTransition(
                        _persistentMood,
                        () =>
                        {
                            RestoreWindowSizeAfterTemporarySeatedMood();
                            RestoreWindowSizeAfterClickFlickFall();
                            RestoreWindowSizeAfterCuteAngry();
                            RestoreWindowSizeAfterAutonomousMovement();
                        });

                    if (PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood))
                    {
                        var now = Environment.TickCount64;
                        ScheduleNextIdleGesture(now);
                        EnsureSeatedRoutineScheduled(now);
                    }
                }
            });
        }
    }

    private static async Task AwaitAnimationBoundaryAsync(
        TaskCompletionSource<bool> completion,
        TimeSpan watchdogDuration,
        PetMood mood,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        var watchdogReported = false;
        while (true)
        {
            using var watchdogCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watchdogTask = Task.Delay(watchdogDuration, watchdogCts.Token);
            var completedTask = await Task.WhenAny(completion.Task, watchdogTask);
            if (ReferenceEquals(completedTask, completion.Task))
            {
                watchdogCts.Cancel();
                await completion.Task;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!watchdogReported)
            {
                watchdogReported = true;
                App.WriteErrorLog(
                    $"Animation boundary delayed ({mood})",
                    new TimeoutException(
                        $"动画 {mood} 播放超过 {watchdogDuration.TotalSeconds:F2} 秒；继续等待真实帧边界。"));
            }
        }
    }

    private Task PreviewAnimationAsync(PetMood mood, CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher
                .InvokeAsync(() => PreviewAnimationAsync(mood, cancellationToken))
                .Task
                .Unwrap();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (_isClosing)
        {
            return Task.FromException(new InvalidOperationException("柯朵正在退出，无法预览动画。"));
        }

        if (_pointerGesture.IsPressed)
        {
            return Task.FromException(new InvalidOperationException("请先放开柯朵，再查看动画。"));
        }

        if (IsClickFlickFallPendingOrActive)
        {
            return Task.FromException(new InvalidOperationException("弹脑门动作播放中，仅抓取可以打断。"));
        }

        if (IsCuteAngryPendingOrActive)
        {
            return Task.FromException(new InvalidOperationException("生气动作播放中，仅抓取可以打断。"));
        }

        if (!PetAnimations.AnimationPreviewOptions.Any(option => option.Mood == mood))
        {
            return Task.FromException(new ArgumentOutOfRangeException(nameof(mood), mood, "该状态不在动画查看清单中。"));
        }

        var spriteAtlas = _spriteAtlas;
        if (spriteAtlas is null)
        {
            return Task.FromException(new InvalidOperationException("角色动画资源尚未加载完成。"));
        }

        try
        {
            if (PetAnimations.IsSeatedMood(mood))
            {
                // A seated clip is not a valid stand-alone preview: its first
                // frame assumes that the character is already sitting. Warm the
                // complete entry/target/exit chain before reserving the visual,
                // then let the dedicated preview routine cross each real frame
                // boundary in order.
                spriteAtlas.PrepareSequence(PetMood.SeatedSitDown);
                spriteAtlas.PrepareSequence(mood);
                spriteAtlas.PrepareSequence(PetMood.SeatedStandUp);
                if (mood is PetMood.SeatedSitDown or PetMood.SeatedIdle)
                {
                    spriteAtlas.PrepareSequence(PetMood.SeatedIdle);
                }

                return PlaySeatedPreviewAsync(mood, cancellationToken);
            }

            var prepared = spriteAtlas.PrepareSequence(mood);
            var previewCycleCount =
                PetAnimations.GetAnimationPreviewCycleCount(mood);
            var duration = PetAnimations.CalculatePreviewDuration(
                checked(prepared.Frames.Count * previewCycleCount),
                prepared.FrameDuration);
            return PlayTemporaryMoodAsync(
                mood,
                duration,
                cancellationToken,
                completeAtAnimationBoundary: true,
                completionBoundaryCount: previewCycleCount);
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private async Task PlaySeatedPreviewAsync(
        PetMood previewMood,
        CancellationToken cancellationToken)
    {
        if (!PetAnimations.IsSeatedMood(previewMood))
        {
            throw new ArgumentOutOfRangeException(nameof(previewMood));
        }

        // Finish an autonomous seated visit first. Repeated preview clicks use
        // the same gate: cancelling the old Settings request asks that preview
        // to finish its current seated clip and stand up before this one enters.
        await WaitForGracefulSeatedExitAsync(cancellationToken);
        await WaitForTemporaryMoodReleaseAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var cts = new CancellationTokenSource();
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_isClosing || _pointerGesture.IsPressed ||
                    IsClickFlickFallPendingOrActive ||
                    IsCuteAngryPendingOrActive ||
                    _isSeatedRoutineActive)
                {
                    throw new InvalidOperationException("当前交互尚未结束，无法开始坐姿预览。");
                }

                _seatedRoutineCts = cts;
                _isSeatedRoutineActive = true;
                _isSeatedPreviewActive = true;
                _seatedExitRequested = false;
                _seatedRoutineCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _nextIdleGestureAtMilliseconds = long.MaxValue;
                _nextAutonomousActionAtMilliseconds = long.MaxValue;
            });
        }
        catch
        {
            cts.Dispose();
            throw;
        }

        // Settings cancellation is graceful: it suppresses the not-yet-started
        // target clip, but does not tear a seated sprite directly back to the
        // standing idle. Confirmed click/drag interactions may still cancel the
        // internal seated owner immediately.
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            _ = Dispatcher.BeginInvoke(new Action(
                () => _ = RequestGracefulSeatedExit()));
        });

        try
        {
            await PlaySeatedPreviewRoutineAsync(cts, previewMood);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A real grab or shutdown canceled the internal seated owner. That
            // interaction already installed (or is installing) its own visual.
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task WaitForTemporaryMoodReleaseAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var temporaryMoodActive = await Dispatcher.InvokeAsync(() =>
                _temporaryMoodCts is not null || _isTemporaryMoodActive);
            if (!temporaryMoodActive)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private bool ApplyMoodCore(
        PetMood mood,
        PetVisualOwner owner = PetVisualOwner.Persistent,
        PreparedAnimationSequence? preparedSequence = null,
        bool releaseGrab = false,
        bool preservePresentationScale = false)
    {
        var isGrabRelease =
            releaseGrab &&
            _visualOwner == PetVisualOwner.Grab &&
            PetVisualTransitionPolicy.CanReceiveGrabRelease(owner);
        if (!isGrabRelease &&
            !PetVisualTransitionPolicy.CanReplace(
                _visualOwner,
                owner,
                IsClickFlickFallPendingOrActive))
        {
            return false;
        }

        var spriteAtlas = _spriteAtlas;
        if (spriteAtlas is null)
        {
            return false;
        }

        CancelRunFallVoicePreparation();

        // Decode, validate, freeze, and cache every frame before changing any
        // visible playback state. A failed preparation leaves the old animation
        // completely intact rather than publishing a partial sequence.
        preparedSequence ??= spriteAtlas.PrepareSequence(mood);
        if (preparedSequence.RequestedMood != mood)
        {
            throw new ArgumentException(
                $"Prepared sequence {preparedSequence.RequestedMood} cannot play as {mood}.",
                nameof(preparedSequence));
        }

        if (_isSeatedRoutineActive && !PetAnimations.IsSeatedMood(mood))
        {
            if (PetVisualTransitionPolicy.CanInterruptSeatedRoutine(owner))
            {
                CancelSeatedRoutine();
            }
            else
            {
                RequestGracefulSeatedExit();
                return false;
            }
        }

        if (!PetSeatedRoutinePolicy.IsAmbientIdleMood(mood))
        {
            _pendingIdleBoundaryAction = PetIdleBoundaryAction.None;
        }

        if (!preservePresentationScale &&
            _temporarySeatedWindowScaleApplied &&
            !PetAnimations.IsSeatedMood(mood))
        {
            RestoreWindowSizeAfterTemporarySeatedMood();
        }

        // Restore in the same dispatcher transaction that publishes the next
        // animation's first frame. This keeps the character from visibly
        // shrinking while CuteAngry's held final frame is still on screen.
        if (!preservePresentationScale &&
            _cuteAngryWindowScaleApplied &&
            mood != PetMood.CuteAngry)
        {
            RestoreWindowSizeAfterCuteAngry();
        }

        if (!preservePresentationScale && mood != PetMood.DragLanding)
        {
            RestoreWindowSizeAfterDragLanding();
        }
        CancelAnimationBoundary();
        _animationHeldForSingleClickConfirmation = false;
        _playbackGeneration++;
        _visualOwner = owner;
        _currentMood = mood;
        _currentSequence = preparedSequence;
        if (owner == PetVisualOwner.Persistent &&
            preparedSequence.VisualMood == PetMood.Idle &&
            PetSeatedRoutinePolicy.IsAmbientIdleMood(mood) &&
            _nextIdleGestureAtMilliseconds == long.MaxValue)
        {
            // Every return route must re-arm the quiet-pause deadline, including
            // a queued status update that wins before the one-shot completion
            // branch can schedule it. Never postpone an already valid deadline.
            ScheduleNextIdleGesture(Environment.TickCount64);
        }
        _frameIndex = 0;
        _animationTimer.Interval = preparedSequence.FrameDuration;
        PetImage.Source = preparedSequence.Frames[0];
        _animationTimer.Start();
        return true;
    }

    private void PetImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _headStrokeGesture.Reset();
        if (_dragReleaseCts is not null && _visualOwner != PetVisualOwner.DragLanding)
        {
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            _singleClickCts?.Cancel();
            _dragHoldTimer.Stop();
            _pointerGesture.Cancel();
            _dragMood = PetMood.Dragged;
            ResetDragVoiceState();
            if (Mouse.Captured == this)
            {
                Mouse.Capture(null);
            }

            if (!IsWorkloadRoutineActive &&
                !IsClickFlickFallPendingOrActive && !IsCuteAngryPendingOrActive &&
                _visualOwner != PetVisualOwner.DragLanding)
            {
                _ = PlayTemporaryMoodAsync(
                    PetMood.Waving,
                    TimeSpan.FromSeconds(1.3),
                    CancellationToken.None,
                    completeAtAnimationBoundary: false,
                    owner: PetVisualOwner.ClickInteraction);
            }
            TryDeliverInteractionLine(PetDialogueIntent.PetDoubleClicked);
            e.Handled = true;
            return;
        }

        var pointerInWindow = e.GetPosition(this);
        if (!TryClassifyVisibleGrabRegion(pointerInWindow, out _grabRegionAtPointerDown))
        {
            // The Image control is rectangular even though the pet is not.
            // Transparent pixels must not silently fall back to a body grab.
            e.Handled = true;
            return;
        }

        _pointerGesture.Press(Environment.TickCount64);
        ResetDragVoiceState();
        _nextIdleGestureAtMilliseconds = long.MaxValue;
        _dragStartScreen = PointToScreen(pointerInWindow);
        _pointerDownWindowLeft = Left;
        _pointerDownWindowTop = Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        _dragDpiScaleX = Math.Max(0.1, dpi.DpiScaleX);
        _dragDpiScaleY = Math.Max(0.1, dpi.DpiScaleY);
        if (!Mouse.Capture(this))
        {
            CancelPointerGesture();
            e.Handled = true;
            return;
        }

        _dragHoldTimer.Stop();
        _dragHoldTimer.Start();
        e.Handled = true;
    }

    private void DragHoldTimer_Tick(object? sender, EventArgs e)
    {
        _dragHoldTimer.Stop();
        if (!_pointerGesture.IsPressed)
        {
            return;
        }

        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            CancelPointerGesture();
            return;
        }

        var pointerInWindow = Mouse.GetPosition(this);
        var currentScreen = PointToScreen(pointerInWindow);
        var dx = (currentScreen.X - _dragStartScreen.X) / _dragDpiScaleX;
        var dy = (currentScreen.Y - _dragStartScreen.Y) / _dragDpiScaleY;
        if (_pointerGesture.Observe(
                Environment.TickCount64,
                dx,
                dy,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance) != PetPointerGestureUpdate.DragStarted)
        {
            return;
        }

        if (!BeginDrag(pointerInWindow))
        {
            return;
        }
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        ObserveHeadStroke(e);
        if (!_pointerGesture.IsPressed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPointerGesture();
            return;
        }

        var pointerInWindow = e.GetPosition(this);
        var currentScreen = PointToScreen(pointerInWindow);
        var dx = (currentScreen.X - _dragStartScreen.X) / _dragDpiScaleX;
        var dy = (currentScreen.Y - _dragStartScreen.Y) / _dragDpiScaleY;

        var gestureUpdate = _pointerGesture.Observe(
            Environment.TickCount64,
            dx,
            dy,
            SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance);
        if (gestureUpdate == PetPointerGestureUpdate.DragStarted)
        {
            if (!BeginDrag(pointerInWindow))
            {
                return;
            }
            _panel?.FollowPet();
            _progressBubble?.PositionNear(this);
            return;
        }

        if (!_pointerGesture.IsDragging)
        {
            return;
        }

        if (!_dragInteractionLineDelivered && !_dragMidpointVoiceEvaluated &&
            Environment.TickCount64 - _dragVoiceStartedAtMilliseconds >= 1400 &&
            Math.Sqrt((dx * dx) + (dy * dy)) >= 140)
        {
            _dragMidpointVoiceEvaluated = true;
            if (Random.Shared.NextDouble() < 0.45)
            {
                _dragInteractionLineDelivered =
                    TryDeliverInteractionLine(PetGrabDialogueContext.Resolve(
                        _grabRegionAtPointerDown,
                        PetGrabPhase.Holding));
            }
        }

        Left += pointerInWindow.X - _dragAnchorInWindow.X;
        Top += pointerInWindow.Y - _dragAnchorInWindow.Y;

        if (_currentMood != _dragMood)
        {
            ApplyForcedInteractionMood(_dragMood, PetVisualOwner.Grab);
        }
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private bool BeginDrag(Point pointerInWindow)
    {
        _dragHoldTimer.Stop();
        _singleClickCts?.Cancel();
        _dragMood = PetAnimations.DragMoodForGrabRegion(_grabRegionAtPointerDown);

        PreparedAnimationSequence prepared;
        try
        {
            prepared = _spriteAtlas!.PrepareSequence(_dragMood);
        }
        catch (Exception ex)
        {
            // Do not cancel the click owner or restore its scale until the
            // replacement grab sequence is known to be complete and valid.
            App.WriteErrorLog($"Grab animation preparation failure ({_dragMood})", ex);
            _pointerGesture.Cancel();
            if (Mouse.Captured == this)
            {
                Mouse.Capture(null);
            }

            _dragMood = PetMood.Dragged;
            _grabRegionAtPointerDown = PetGrabRegion.Body;
            ResetDragVoiceState();
            ScheduleNextIdleGestureForAmbientState(Environment.TickCount64);
            ResumeAfterNonInteractionPointerRelease();
            return false;
        }

        if (Mouse.Captured == this)
        {
            pointerInWindow = Mouse.GetPosition(this);
        }

        var pointerScreen = PointToScreen(pointerInWindow);
        CancelClickFlickFallForGrab();
        CancelCuteAngryForGrab();
        CancelDragLandingForGrab();
        CancelWorkloadForGrab();
        CancelSeatedRoutine();
        CancelTemporaryMoodAndRestoreScale(preserveQueuedNormalTransition: true);
        StopAutonomousMovement(restorePersistentMood: false);
        pointerInWindow = PointFromScreen(pointerScreen);
        if (!ApplyForcedInteractionMood(_dragMood, PetVisualOwner.Grab, prepared))
        {
            throw new InvalidOperationException("抓取动画已经准备完成，但未能取得播放权。");
        }
        ApplyDragVisualProfile(_dragMood, pointerInWindow);
        _dragVoiceStartedAtMilliseconds = Environment.TickCount64;
        _dragInteractionLineDelivered = true;
        _dragMidpointVoiceEvaluated = false;
        DeliverInstantActionReaction(PetGrabDialogueContext.Resolve(
            _grabRegionAtPointerDown,
            PetGrabPhase.Started));
        StartGrabReleaseLinePreparation(PetGrabDialogueContext.Resolve(
            _grabRegionAtPointerDown,
            PetGrabPhase.Released));
        return true;
    }

    private async void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerGesture.IsPressed)
        {
            return;
        }

        _dragHoldTimer.Stop();
        var completion = _pointerGesture.Release();
        var wasDragging = completion == PetPointerGestureCompletion.Dragged;
        var releasedGrabRegion = _grabRegionAtPointerDown;
        Mouse.Capture(null);
        Task<PreparedAnimationSequence?>? cuteAngryPreparationTask = null;
        if (wasDragging &&
            _spriteAtlas is { } spriteAtlas &&
            PetCuteAngryTriggerPolicy.ShouldTrigger(
                PetCuteAngryTriggerSource.GrabReleased,
                Random.Shared.NextDouble()))
        {
            // Hold the grab pose while preparing the next sequence. The final
            // UI transaction can then publish CuteAngry F000
            // together with its calibrated window size, without an idle flash.
            var preparationToken = _animationPreparationLifetime.Token;
            cuteAngryPreparationTask = Task.Run(
                () => PrepareCuteAngrySequenceOrNull(
                    spriteAtlas,
                    preparationToken),
                preparationToken);
        }

        if (wasDragging)
        {
            DeliverPreparedGrabReleaseReaction(PetGrabDialogueContext.Resolve(
                releasedGrabRegion,
                PetGrabPhase.Released));
            await PlayDragReleaseAsync(cuteAngryPreparationTask);
            // A new real grab may interrupt the reaction while this older mouse-up
            // handler awaits its completion. Never reset that new gesture.
            if (_pointerGesture.IsPressed || _visualOwner == PetVisualOwner.Grab)
            {
                e.Handled = true;
                return;
            }
        }
        _dragMood = PetMood.Dragged;
        _grabRegionAtPointerDown = PetGrabRegion.Body;
        ScheduleNextIdleGestureForAmbientState(Environment.TickCount64);

        if (wasDragging)
        {
            // Persist the equivalent resting-window origin even when the
            // reaction is temporarily using its larger head-matched window.
            _settings.WindowLeft = RestingViewportLeft;
            _settings.WindowTop = RestingViewportTop;
            try
            {
                await _settingsStore.SaveAsync(_settings);
            }
            catch (Exception ex)
            {
                SetStatus("位置保存失败", Brushes.IndianRed);
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
        else if (completion == PetPointerGestureCompletion.Click)
        {
            ScheduleConfirmedSingleClick();
        }
        else
        {
            ResumeAfterNonInteractionPointerRelease();
        }

        _progressBubble?.PositionNear(this);
        ResetDragVoiceState();

        e.Handled = true;
    }

    private async Task PlayDragReleaseAsync(
        Task<PreparedAnimationSequence?>? cuteAngryPreparationTask)
    {
        CancelWithoutThrow(_dragReleaseCts);
        var cts = new CancellationTokenSource();
        _dragReleaseCts = cts;

        try
        {
            var releaseCenterX = CurrentViewportLeft + (ViewportWidth / 2);
            var releaseCenterY = CurrentViewportTop + (ViewportHeight / 2);
            var targetRestingLeft = Clamp(
                releaseCenterX - (RestingWindowWidth / 2),
                SystemParameters.VirtualScreenLeft - RestingWindowWidth + 52,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 52);
            var targetRestingTop = Clamp(
                releaseCenterY - (RestingWindowHeight / 2),
                SystemParameters.VirtualScreenTop - RestingWindowHeight + 52,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 52);

            PreparedAnimationSequence? preparedCuteAngry = null;
            if (cuteAngryPreparationTask is not null)
            {
                try
                {
                    preparedCuteAngry = await cuteAngryPreparationTask;
                }
                catch (OperationCanceledException)
                {
                    cts.Token.ThrowIfCancellationRequested();
                }
                catch (Exception ex)
                {
                    // The external-frame hard gate remains authoritative. A missing
                    // or malformed optional reaction must never strand the grab.
                    App.WriteErrorLog("Post-grab cute-angry preparation failure", ex);
                }
            }

            cts.Token.ThrowIfCancellationRequested();

            // Soft landing and release drift are retired: go directly to the existing
            // optional angry reaction or the current persistent state.
            // The mood and geometry changes below run without a dispatcher yield.
            // WPF therefore presents either the CuteAngry first frame at its
            // head-matched size or the persistent first frame at resting size; the
            // enlarged grab artwork is never rendered inside an intermediate box.
            var startedCuteAngry =
                preparedCuteAngry is not null &&
                TryReleaseGrabIntoPreparedCuteAngryReaction(
                    preparedCuteAngry,
                    targetRestingLeft,
                    targetRestingTop);
            if (!startedCuteAngry)
            {
                var prepared = _spriteAtlas!.PrepareSequence(_persistentMood);
                if (EndGrabWithPersistentMood(prepared))
                {
                    SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
                    SetHostPositionForRestingViewport(targetRestingLeft, targetRestingTop);
                }
            }

            _panel?.FollowPet();
            _progressBubble?.PositionNear(this);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_dragReleaseCts, cts))
            {
                _dragReleaseCts = null;
            }

            cts.Dispose();
        }
    }

    private void ScheduleConfirmedSingleClick()
    {
        if (IsWorkloadRoutineActive)
        {
            ResumeAfterNonInteractionPointerRelease();
            return;
        }
        _singleClickCts?.Cancel();
        var cts = new CancellationTokenSource();
        _singleClickCts = cts;
        _ = ConfirmSingleClickAfterDoubleClickWindowAsync(cts);
    }

    private async Task ConfirmSingleClickAfterDoubleClickWindowAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SingleClickConfirmationDelay, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                _ = PlayClickFlickFallAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_singleClickCts, cts))
            {
                _singleClickCts = null;
                if (!cts.IsCancellationRequested &&
                    !IsClickFlickFallPendingOrActive)
                {
                    ResumeAnimationHeldForSingleClickConfirmation();
                }
            }

            cts.Dispose();
        }
    }

    private Task PlayClickFlickFallAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher
                .InvokeAsync(PlayClickFlickFallAsync)
                .Task
                .Unwrap();
        }

        if (_isClosing || IsWorkloadRoutineActive ||
            IsClickFlickFallPendingOrActive || IsCuteAngryPendingOrActive ||
            _visualOwner == PetVisualOwner.DragLanding)
        {
            return Task.CompletedTask;
        }

        var spriteAtlas = _spriteAtlas;
        if (spriteAtlas is null)
        {
            return Task.CompletedTask;
        }

        var cts = new CancellationTokenSource();
        _clickFlickFallCts = cts;

        // Confirmation reserves the exclusive slot immediately, but the visible
        // owner, scale, and current sequence remain intact until every click
        // frame has been decoded and validated. A failed preparation therefore
        // cannot tear down the animation already on screen.
        return PlayClickFlickFallCoreAsync(spriteAtlas, cts);
    }

    private async Task PlayClickFlickFallCoreAsync(
        SpriteAtlas spriteAtlas,
        CancellationTokenSource cts)
    {
        var playbackGeneration = 0L;
        var started = false;
        var completedNaturally = false;
        PreparedAnimationSequence? preparedCuteAngry = null;
        Task<PreparedAnimationSequence?>? cuteAngryPreparationTask = null;
        try
        {
            if (PetCuteAngryTriggerPolicy.ShouldTrigger(
                    PetCuteAngryTriggerSource.ClickCompleted,
                    Random.Shared.NextDouble()))
            {
                cuteAngryPreparationTask = Task.Run(
                    () => PrepareCuteAngrySequenceOrNull(spriteAtlas, cts.Token),
                    cts.Token);
            }

            var prepared = await Task.Run(
                () => spriteAtlas.PrepareSequence(PetMood.ClickFlickFall),
                cts.Token);

            AnimationBoundaryWaiter? boundaryWaiter = null;
            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_clickFlickFallCts, cts) ||
                    cts.IsCancellationRequested ||
                    _isClosing)
                {
                    throw new OperationCanceledException(cts.Token);
                }

                // Frame preparation is the only work allowed before the atomic
                // interaction commit. A confirmed click may replace any normal
                // animation immediately; a later true drag may replace it too.
                CancelCuteAngryBoundaryRequest();
                CancelSeatedRoutine(restoreWindowScale: false);
                CancelTemporaryMoodAndRestoreScale(
                    preserveQueuedNormalTransition: true,
                    restoreWindowScale: false);
                StopAutonomousMovement(
                    restorePersistentMood: false,
                    restoreWindowScale: false);
                if (!ApplyForcedInteractionMood(
                        PetMood.ClickFlickFall,
                        PetVisualOwner.ClickFlickFall,
                        prepared,
                        preservePresentationScale: true))
                {
                    RestoreWindowSizeAfterSeatedRoutine();
                    RestoreWindowSizeAfterTemporarySeatedMood();
                    RestoreWindowSizeAfterCuteAngry();
                    RestoreWindowSizeAfterAutonomousMovement();
                    throw new InvalidOperationException("弹脑门动作未能取得动画播放权。");
                }

                started = true;
                playbackGeneration = _playbackGeneration;
                // The click frame is now visible. Adopt the previous geometry in
                // one direct resize instead of showing the old frame at resting
                // size and then enlarging it again.
                ApplyClickFlickFallWindowScale();
                DeliverInstantActionReaction(PetDialogueIntent.HeadFlicked);
                TryDeliverInteractionLine(PetDialogueIntent.HeadFlickAngry);
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                boundaryWaiter = new AnimationBoundaryWaiter(
                    playbackGeneration,
                    PetMood.ClickFlickFall,
                    PetVisualOwner.ClickFlickFall,
                    StopAtBoundary: true,
                    completion);
                _animationBoundaryWaiter = boundaryWaiter;
            });

            var watchdogDuration = PetAnimations.CalculateAnimationWatchdog(
                prepared.Frames.Count,
                prepared.FrameDuration);
            await AwaitAnimationBoundaryAsync(
                boundaryWaiter!.Completion,
                watchdogDuration,
                PetMood.ClickFlickFall,
                cts.Token);
            completedNaturally = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Click-flick-fall animation failure", ex);
        }
        finally
        {
            if (cuteAngryPreparationTask is not null)
            {
                try
                {
                    preparedCuteAngry = await cuteAngryPreparationTask;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    App.WriteErrorLog("Post-click cute-angry preparation failure", ex);
                }
            }

            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_clickFlickFallCts, cts))
                {
                    return;
                }

                _clickFlickFallCts = null;
                if (!started)
                {
                    // Preparation failed or was cancelled before the atomic UI
                    // commit. If the old one-shot reached its boundary while the
                    // interaction was preparing, release that held final frame.
                    RestoreWindowSizeAfterClickFlickFall();
                    if (IsAtCurrentAnimationBoundary() &&
                        TryApplyPendingVisualTransitionAtBoundary())
                    {
                        return;
                    }

                    var orphanedTemporary =
                        _temporaryMoodCts is null &&
                        (_visualOwner is PetVisualOwner.Temporary or
                            PetVisualOwner.ClickInteraction);
                    if (_pendingVisualTransition is null &&
                        (orphanedTemporary ||
                         IsAtCurrentAnimationBoundary() &&
                         _visualOwner != PetVisualOwner.Grab &&
                         !(_visualOwner == PetVisualOwner.SeatedRoutine &&
                           _isSeatedRoutineActive)))
                    {
                        _ = RequestPersistentMoodTransition(_persistentMood);
                    }

                    ResumeAnimationHeldForSingleClickConfirmation();
                    return;
                }

                CancelAnimationBoundary(PetVisualOwner.ClickFlickFall);

                if (_visualOwner == PetVisualOwner.ClickFlickFall &&
                    _playbackGeneration == playbackGeneration)
                {
                    // The owner lock is deliberately released only at the true
                    // timer boundary, then the newest queued ordinary request
                    // (or persistent task state) may render.
                    _visualOwner = PetVisualOwner.Persistent;
                }

                if (!_pointerGesture.IsDragging && !_isClosing)
                {
                    var startedCuteAngry =
                        completedNaturally &&
                        preparedCuteAngry is not null &&
                        TryStartPreparedCuteAngryReaction(
                            preparedCuteAngry,
                            allowClickFinalFrame: true,
                            allowPointerPress: true,
                            applyWindowScale:
                                TransferClickFlickFallScaleToCuteAngry);
                    if (!startedCuteAngry)
                    {
                        RestoreWindowSizeAfterClickFlickFall();
                        var appliedQueuedNormal =
                            IsAtCurrentAnimationBoundary() &&
                            TryApplyPendingVisualTransitionAtBoundary();
                        if (!appliedQueuedNormal &&
                            _pendingVisualTransition is null)
                        {
                            _ = RequestPersistentMoodTransition(_persistentMood);
                        }

                        var now = Environment.TickCount64;
                        ScheduleNextAutonomousAction(now);
                        if (PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood))
                        {
                            ScheduleNextIdleGesture(now);
                            EnsureSeatedRoutineScheduled(now);
                        }
                        else
                        {
                            _nextIdleGestureAtMilliseconds = long.MaxValue;
                        }
                    }
                }
            });

            cts.Dispose();
        }
    }

    private void CancelClickFlickFallForGrab()
    {
        var cts = Interlocked.Exchange(ref _clickFlickFallCts, null);
        if (cts is null)
        {
            return;
        }

        CancelAnimationBoundary(PetVisualOwner.ClickFlickFall);
        CancelWithoutThrow(cts);
        RestoreWindowSizeAfterClickFlickFall();
    }

    private async Task PlayAutomaticCuteAngryReactionAsync()
    {
        CancellationTokenSource? boundaryRequestCts = null;
        try
        {
            SpriteAtlas? spriteAtlas = null;
            var preparationToken = CancellationToken.None;
            await RunOnUiThreadAsync(() =>
            {
                if (_cuteAngryBoundaryRequestCts is null &&
                    CanStartCuteAngryReaction(
                        allowClickFinalFrame: false,
                        allowPointerPress: false))
                {
                    spriteAtlas = _spriteAtlas;
                    preparationToken = _animationPreparationLifetime.Token;
                }
            });
            if (spriteAtlas is null)
            {
                return;
            }

            var prepared = await Task.Run(
                () => PrepareCuteAngrySequenceOrNull(
                    spriteAtlas,
                    preparationToken),
                preparationToken);
            if (prepared is null)
            {
                return;
            }

            Task<bool> activationTask = Task.FromResult(false);
            await RunOnUiThreadAsync(() =>
            {
                if (_cuteAngryBoundaryRequestCts is not null ||
                    !CanStartCuteAngryReaction(
                        allowClickFinalFrame: false,
                        allowPointerPress: false))
                {
                    return;
                }

                boundaryRequestCts = new CancellationTokenSource();
                _cuteAngryBoundaryRequestCts = boundaryRequestCts;
                activationTask = RequestAmbientCuteAngryAtBoundary(
                    boundaryRequestCts.Token,
                    () =>
                    {
                        if (!ReferenceEquals(
                                _cuteAngryBoundaryRequestCts,
                                boundaryRequestCts) ||
                            boundaryRequestCts.IsCancellationRequested)
                        {
                            return false;
                        }

                        // The ambient request waited as an ordinary boundary
                        // item. Only now, at the real idle loop boundary, may it
                        // create the exclusive reaction CTS and forced owner.
                        _cuteAngryBoundaryRequestCts = null;
                        return TryStartPreparedCuteAngryReaction(
                            prepared,
                            allowClickFinalFrame: true,
                            allowPointerPress: false);
                    });
            });

            await activationTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Automatic cute-angry animation failure", ex);
        }
        finally
        {
            if (boundaryRequestCts is not null)
            {
                await RunOnUiThreadAsync(() =>
                {
                    if (ReferenceEquals(
                            _cuteAngryBoundaryRequestCts,
                            boundaryRequestCts))
                    {
                        _cuteAngryBoundaryRequestCts = null;
                        CancelPendingVisualTransition(
                            PetVisualOwner.CuteAngryBoundaryRequest);
                    }
                });
                boundaryRequestCts.Dispose();
            }
        }
    }

    private static PreparedAnimationSequence? PrepareCuteAngrySequenceOrNull(
        SpriteAtlas spriteAtlas,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!spriteAtlas.UsesExternalFrames(PetMood.CuteAngry))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return spriteAtlas.PrepareSequence(PetMood.CuteAngry);
    }

    private bool CanStartCuteAngryReaction(
        bool allowClickFinalFrame,
        bool allowPointerPress)
    {
        var hasLiveTemporaryRequest =
            _temporaryMoodCts is { IsCancellationRequested: false };
        var liveTemporaryRequestIsQueued =
            _temporaryMoodCts is { IsCancellationRequested: false } temporaryCts &&
            _pendingVisualTransition is
            {
                Owner: PetVisualOwner.Temporary
            } pendingTemporary &&
            !pendingTemporary.CancellationToken.IsCancellationRequested &&
            pendingTemporary.CancellationToken == temporaryCts.Token;
        if (_spriteAtlas is null ||
            _isClosing ||
            IsCuteAngryPendingOrActive ||
            IsClickFlickFallPendingOrActive ||
            (!allowPointerPress && _pointerGesture.IsPressed) ||
            _singleClickCts is not null ||
            PetVisualTransitionPolicy.DoesTemporaryMoodBlockCuteAngryReaction(
                allowClickFinalFrame,
                _isTemporaryMoodActive,
                hasLiveTemporaryRequest,
                liveTemporaryRequestIsQueued) ||
            _isSeatedRoutineActive ||
            _isAutonomousMoving ||
            _visualOwner != PetVisualOwner.Persistent)
        {
            return false;
        }

        return allowClickFinalFrame
            ? IsAtCurrentAnimationBoundary()
            : _currentSequence?.VisualMood == PetMood.Idle;
    }

    private bool TryStartPreparedCuteAngryReaction(
        PreparedAnimationSequence prepared,
        bool allowClickFinalFrame,
        bool allowPointerPress,
        Action? applyWindowScale = null)
    {
        if (prepared.RequestedMood != PetMood.CuteAngry)
        {
            throw new ArgumentException(
                $"Prepared sequence {prepared.RequestedMood} cannot play as {PetMood.CuteAngry}.",
                nameof(prepared));
        }

        if (!CanStartCuteAngryReaction(allowClickFinalFrame, allowPointerPress))
        {
            return false;
        }

        var cts = new CancellationTokenSource();
        _cuteAngryCts = cts;
        if (!ApplyForcedInteractionMood(
                PetMood.CuteAngry,
                PetVisualOwner.CuteAngryReaction,
                prepared))
        {
            _cuteAngryCts = null;
            cts.Dispose();
            return false;
        }

        (applyWindowScale ?? ApplyCuteAngryWindowScale)();
        StartCuteAngryBoundaryLifecycle(cts, prepared);
        return true;
    }

    private bool TryReleaseGrabIntoPreparedCuteAngryReaction(
        PreparedAnimationSequence prepared,
        double targetRestingLeft,
        double targetRestingTop)
    {
        if (prepared.RequestedMood != PetMood.CuteAngry)
        {
            throw new ArgumentException(
                $"Prepared sequence {prepared.RequestedMood} cannot play as {PetMood.CuteAngry}.",
                nameof(prepared));
        }

        if (_isClosing ||
            _pointerGesture.IsPressed ||
            IsCuteAngryPendingOrActive ||
            IsClickFlickFallPendingOrActive ||
            _visualOwner != PetVisualOwner.Grab)
        {
            return false;
        }

        var cts = new CancellationTokenSource();
        _cuteAngryCts = cts;
        if (!ApplyMoodCore(
                PetMood.CuteAngry,
                PetVisualOwner.CuteAngryReaction,
                prepared,
                releaseGrab: true))
        {
            _cuteAngryCts = null;
            cts.Dispose();
            return false;
        }

        // Establish the resting viewport location without changing the fixed
        // host; CuteAngry then selects its own centered viewport.
        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        SetHostPositionForRestingViewport(targetRestingLeft, targetRestingTop);
        ApplyCuteAngryWindowScale();
        StartCuteAngryBoundaryLifecycle(cts, prepared);
        return true;
    }

    private void StartCuteAngryBoundaryLifecycle(
        CancellationTokenSource cts,
        PreparedAnimationSequence prepared)
    {
        var playbackGeneration = _playbackGeneration;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var boundaryWaiter = new AnimationBoundaryWaiter(
            playbackGeneration,
            PetMood.CuteAngry,
            PetVisualOwner.CuteAngryReaction,
            StopAtBoundary: true,
            completion);
        _animationBoundaryWaiter = boundaryWaiter;
        var watchdogDuration = PetAnimations.CalculateAnimationWatchdog(
            prepared.Frames.Count,
            prepared.FrameDuration);
        _ = CompleteCuteAngryReactionAsync(
            cts,
            boundaryWaiter,
            watchdogDuration,
            playbackGeneration);
    }

    private async Task CompleteCuteAngryReactionAsync(
        CancellationTokenSource cts,
        AnimationBoundaryWaiter boundaryWaiter,
        TimeSpan watchdogDuration,
        long playbackGeneration)
    {
        try
        {
            await AwaitAnimationBoundaryAsync(
                boundaryWaiter.Completion,
                watchdogDuration,
                PetMood.CuteAngry,
                cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Cute-angry animation failure", ex);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_cuteAngryCts, cts))
                {
                    return;
                }

                _cuteAngryCts = null;
                CancelAnimationBoundary(PetVisualOwner.CuteAngryReaction);
                if (_visualOwner != PetVisualOwner.CuteAngryReaction ||
                    _playbackGeneration != playbackGeneration)
                {
                    return;
                }

                _visualOwner = PetVisualOwner.Persistent;
                var appliedQueuedNormal =
                    IsAtCurrentAnimationBoundary() &&
                    TryApplyPendingVisualTransitionAtBoundary();
                if (!appliedQueuedNormal && _pendingVisualTransition is null)
                {
                    _ = RequestPersistentMoodTransition(_persistentMood);
                }

                var now = Environment.TickCount64;
                ScheduleNextAutonomousAction(now);
                if (PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood))
                {
                    ScheduleNextIdleGesture(now);
                    EnsureSeatedRoutineScheduled(now);
                }
                else
                {
                    _nextIdleGestureAtMilliseconds = long.MaxValue;
                }
            });

            cts.Dispose();
        }
    }

    private void CancelCuteAngryForGrab()
    {
        CancelCuteAngryBoundaryRequest();
        var cts = Interlocked.Exchange(ref _cuteAngryCts, null);
        if (cts is not null)
        {
            CancelAnimationBoundary(PetVisualOwner.CuteAngryReaction);
            CancelWithoutThrow(cts);
        }

        RestoreWindowSizeAfterCuteAngry();
    }

    private void CancelCuteAngryBoundaryRequest()
    {
        var cts = Interlocked.Exchange(
            ref _cuteAngryBoundaryRequestCts,
            null);
        if (cts is null)
        {
            return;
        }

        CancelPendingVisualTransition(PetVisualOwner.CuteAngryBoundaryRequest);
        CancelWithoutThrow(cts);
    }

    private void Window_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_pointerGesture.IsPressed || Mouse.Captured == this)
        {
            return;
        }

        CancelPointerGesture();
    }

    private void CancelPointerGesture()
    {
        if (!_pointerGesture.IsPressed)
        {
            return;
        }

        _dragHoldTimer.Stop();
        var wasDragging = _pointerGesture.IsDragging;
        _pointerGesture.Cancel();
        if (Mouse.Captured == this)
        {
            Mouse.Capture(null);
        }

        ResetDragVoiceState();
        if (wasDragging)
        {
            RestoreWindowSizeAfterDrag(restorePointerDownPosition: false);
            var prepared = _spriteAtlas!.PrepareSequence(_persistentMood);
            EndGrabWithPersistentMood(prepared);
        }
        else
        {
            // A lost/cancelled press is not an interaction outcome, so it cannot
            // restart or pre-empt the animation already on screen.
            ResumeAfterNonInteractionPointerRelease();
        }
        _dragMood = PetMood.Dragged;
        _grabRegionAtPointerDown = PetGrabRegion.Body;
        ScheduleNextIdleGestureForAmbientState(Environment.TickCount64);
        _progressBubble?.PositionNear(this);
    }

    private void ResumeAfterNonInteractionPointerRelease()
    {
        // A normal request may have reached a real boundary while the pointer
        // was held. Drain it before lifecycle guards because that request may
        // itself own the temporary or seated lifecycle.
        if (IsAtCurrentAnimationBoundary() &&
            TryApplyPendingVisualTransitionAtBoundary())
        {
            return;
        }

        if (_isSeatedRoutineActive ||
            IsClickFlickFallPendingOrActive ||
            IsCuteAngryPendingOrActive ||
            _temporaryMoodCts is not null ||
            _isTemporaryMoodActive)
        {
            return;
        }

        if (_isAutonomousMoving)
        {
            if (IsAtCurrentAnimationBoundary())
            {
                StopAutonomousMovement(restorePersistentMood: true);
            }
            else if (!PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood) &&
                     _pendingVisualTransition is null)
            {
                _ = RequestPersistentMoodTransition(_persistentMood);
            }

            return;
        }

        if (_pendingVisualTransition is null &&
            (_visualOwner != PetVisualOwner.Persistent ||
             _currentMood != _persistentMood))
        {
            _ = RequestPersistentMoodTransition(
                _persistentMood,
                () =>
                {
                    RestoreWindowSizeAfterTemporarySeatedMood();
                    RestoreWindowSizeAfterClickFlickFall();
                    RestoreWindowSizeAfterCuteAngry();
                });
        }
    }

    private void PetImage_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        TryDeliverInteractionLine(PetDialogueIntent.PetRightClicked);
    }

    private void ResetDragVoiceState()
    {
        CancelGrabReleaseLinePreparation();
        _dragVoiceStartedAtMilliseconds = 0;
        _dragInteractionLineDelivered = false;
        _dragMidpointVoiceEvaluated = false;
    }

    private void ApplyDragVisualProfile(PetMood mood, Point pointerInWindow)
    {
        var profile = PetAnimations.DragVisualProfiles[mood];
        SetPetViewportSize(
            RestingWindowWidth * profile.WindowScale,
            RestingWindowHeight * profile.WindowScale);
        // The drag anchor must be measured in the newly enlarged viewport.
        // Width/Height invalidate layout but ActualWidth and TranslatePoint keep
        // the previous resting geometry until layout is updated.
        PetViewport.UpdateLayout();
        _dragAnchorInWindow = CalculateFramePoint(profile.AnchorX, profile.AnchorY);

        Left += pointerInWindow.X - _dragAnchorInWindow.X;
        Top += pointerInWindow.Y - _dragAnchorInWindow.Y;
    }

    private Point CalculateFramePoint(double normalizedX, double normalizedY)
    {
        var imageWidth = Math.Max(1, PetImage.ActualWidth);
        var imageHeight = Math.Max(1, PetImage.ActualHeight);
        var sourceScale = Math.Min(
            imageWidth / PetAnimations.CellWidth,
            imageHeight / PetAnimations.CellHeight);
        var renderedWidth = PetAnimations.CellWidth * sourceScale;
        var renderedHeight = PetAnimations.CellHeight * sourceScale;
        var imageOrigin = PetImage.TranslatePoint(new Point(0, 0), this);
        var renderedLeft = imageOrigin.X + ((imageWidth - renderedWidth) / 2);
        var renderedTop = imageOrigin.Y + ((imageHeight - renderedHeight) / 2);
        return new Point(
            renderedLeft + (Math.Clamp(normalizedX, 0, 1) * renderedWidth),
            renderedTop + (Math.Clamp(normalizedY, 0, 1) * renderedHeight));
    }

    private Point CalculateNormalizedFramePoint(Point pointerInWindow)
    {
        var imageWidth = Math.Max(1, PetImage.ActualWidth);
        var imageHeight = Math.Max(1, PetImage.ActualHeight);
        var sourceScale = Math.Min(
            imageWidth / PetAnimations.CellWidth,
            imageHeight / PetAnimations.CellHeight);
        var renderedWidth = Math.Max(1, PetAnimations.CellWidth * sourceScale);
        var renderedHeight = Math.Max(1, PetAnimations.CellHeight * sourceScale);
        var imageOrigin = PetImage.TranslatePoint(new Point(0, 0), this);
        var renderedLeft = imageOrigin.X + ((imageWidth - renderedWidth) / 2);
        var renderedTop = imageOrigin.Y + ((imageHeight - renderedHeight) / 2);
        return new Point(
            Math.Clamp((pointerInWindow.X - renderedLeft) / renderedWidth, 0, 1),
            Math.Clamp((pointerInWindow.Y - renderedTop) / renderedHeight, 0, 1));
    }

    private bool TryClassifyVisibleGrabRegion(
        Point pointerInWindow,
        out PetGrabRegion region)
    {
        var normalizedPointer = CalculateNormalizedFramePoint(pointerInWindow);
        if (PetImage.Source is not BitmapSource source ||
            source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            region = PetGrabHitTest.Classify(
                normalizedPointer.X,
                normalizedPointer.Y);
            return true;
        }

        try
        {
            BitmapSource bgraSource = source;
            if (source.Format != PixelFormats.Bgra32 &&
                source.Format != PixelFormats.Pbgra32)
            {
                bgraSource = new FormatConvertedBitmap(
                    source,
                    PixelFormats.Bgra32,
                    null,
                    0);
            }

            const byte alphaThreshold = 16;
            var width = bgraSource.PixelWidth;
            var height = bgraSource.PixelHeight;
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            bgraSource.CopyPixels(pixels, stride, 0);

            var pixelX = Math.Clamp(
                (int)Math.Floor(normalizedPointer.X * width),
                0,
                width - 1);
            var pixelY = Math.Clamp(
                (int)Math.Floor(normalizedPointer.Y * height),
                0,
                height - 1);
            var pointerAlpha = pixels[(pixelY * stride) + (pixelX * 4) + 3];
            if (pointerAlpha < alphaThreshold)
            {
                region = PetGrabRegion.Body;
                return false;
            }

            // Furniture changes the total alpha bounds and a prone head is not
            // in their upper half. Use reviewed video-space semantic landmarks
            // for these scenes, without changing the displayed frame geometry.
            var workloadHit = TryClassifyWorkloadGrabPoint(pixelX + 0.5, pixelY + 0.5, out region);
            if (workloadHit is { } hit) return hit;

            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (pixels[rowOffset + (x * 4) + 3] < alphaThreshold)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
            {
                region = PetGrabRegion.Body;
                return false;
            }

            var sampleX = (pixelX + 0.5) / width;
            var sampleY = (pixelY + 0.5) / height;
            region = PetGrabHitTest.ClassifyVisiblePoint(
                sampleX,
                sampleY,
                minX / (double)width,
                minY / (double)height,
                (maxX + 1d) / width,
                (maxY + 1d) / height);
            return true;
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Current-frame grab hit-test failed; using the calm-pose contour", ex);
            region = PetGrabHitTest.Classify(
                normalizedPointer.X,
                normalizedPointer.Y);
            return true;
        }
    }

    private void RestoreWindowSizeAfterDrag(bool restorePointerDownPosition)
    {
        if (Math.Abs(ViewportWidth - RestingWindowWidth) < 0.01 &&
            Math.Abs(ViewportHeight - RestingWindowHeight) < 0.01)
        {
            return;
        }

        var centerX = CurrentViewportLeft + (ViewportWidth / 2);
        var centerY = CurrentViewportTop + (ViewportHeight / 2);
        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        if (restorePointerDownPosition)
        {
            Left = _pointerDownWindowLeft;
            Top = _pointerDownWindowTop;
            return;
        }

        Left = Clamp(
            centerX - (RestingWindowWidth / 2) - RestingViewportInsetX,
            SystemParameters.VirtualScreenLeft - RestingWindowWidth + 52 - RestingViewportInsetX,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth -
            52 - RestingViewportInsetX);
        Top = Clamp(
            centerY - (RestingWindowHeight / 2) - RestingViewportInsetY,
            SystemParameters.VirtualScreenTop - RestingViewportInsetY,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight -
            52 - RestingViewportInsetY);
    }

    private void ApplyAutonomousWindowScale(double scale)
    {
        if (!double.IsFinite(scale) || scale < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        ApplyAutonomousWindowGeometry(
            RestingWindowWidth * scale,
            RestingWindowHeight * scale,
            centerOffsetX: 0,
            centerOffsetY: 0);
    }

    private void ApplyAutonomousContentScale(
        double contentScale,
        double centerOffsetX = 0,
        double centerOffsetY = 0)
    {
        if (!double.IsFinite(contentScale) || contentScale < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contentScale));
        }

        ApplyAutonomousWindowGeometry(
            PetAnimations.CalculateContentScaledWindowDimension(
                RestingWindowWidth,
                contentScale),
            PetAnimations.CalculateContentScaledWindowDimension(
                RestingWindowHeight,
                contentScale),
            centerOffsetX,
            centerOffsetY);
    }

    private void ApplyAutonomousWindowGeometry(
        double targetWidth,
        double targetHeight,
        double centerOffsetX,
        double centerOffsetY)
    {
        if (!double.IsFinite(targetWidth) || targetWidth < RestingWindowWidth ||
            !double.IsFinite(targetHeight) || targetHeight < RestingWindowHeight ||
            !double.IsFinite(centerOffsetX) || !double.IsFinite(centerOffsetY))
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }

        if (Math.Abs(ViewportWidth - targetWidth) < 0.01 &&
            Math.Abs(ViewportHeight - targetHeight) < 0.01 &&
            Math.Abs(_autonomousWindowCenterOffsetX - centerOffsetX) < 0.01 &&
            Math.Abs(_autonomousWindowCenterOffsetY - centerOffsetY) < 0.01)
        {
            _autonomousWindowScaleApplied =
                targetWidth > RestingWindowWidth ||
                targetHeight > RestingWindowHeight ||
                centerOffsetX != 0 || centerOffsetY != 0;
            return;
        }

        var centerX = Left + (HostWindowWidth / 2) - _autonomousWindowCenterOffsetX;
        var centerY = Top + (HostWindowHeight / 2) - _autonomousWindowCenterOffsetY;
        SetPetViewportSize(targetWidth, targetHeight);
        var targetInsetX = (HostWindowWidth - targetWidth) / 2;
        var targetInsetY = (HostWindowHeight - targetHeight) / 2;
        Left = Clamp(
            centerX - (HostWindowWidth / 2) + centerOffsetX,
            SystemParameters.VirtualScreenLeft - targetInsetX,
            Math.Max(
                SystemParameters.VirtualScreenLeft - targetInsetX,
                SystemParameters.VirtualScreenLeft +
                SystemParameters.VirtualScreenWidth - targetWidth - targetInsetX));
        Top = Clamp(
            centerY - (HostWindowHeight / 2) + centerOffsetY,
            SystemParameters.VirtualScreenTop - targetInsetY,
            Math.Max(
                SystemParameters.VirtualScreenTop - targetInsetY,
                SystemParameters.VirtualScreenTop +
                SystemParameters.VirtualScreenHeight - targetHeight - targetInsetY));
        // Screen-edge clamping can shave off a requested sub-pixel offset.
        // Remember the offset that was actually applied so restoration removes
        // presentation geometry without erasing real autonomous movement.
        _autonomousWindowCenterOffsetX = (Left + (HostWindowWidth / 2)) - centerX;
        _autonomousWindowCenterOffsetY = (Top + (HostWindowHeight / 2)) - centerY;
        _autonomousWindowScaleApplied =
            targetWidth > RestingWindowWidth ||
            targetHeight > RestingWindowHeight ||
            centerOffsetX != 0 || centerOffsetY != 0;
    }

    private void RestoreWindowSizeAfterAutonomousMovement()
    {
        if (!_autonomousWindowScaleApplied)
        {
            return;
        }

        var centerX = Left + (HostWindowWidth / 2) - _autonomousWindowCenterOffsetX;
        var centerY = Top + (HostWindowHeight / 2) - _autonomousWindowCenterOffsetY;
        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        Left = Clamp(
            centerX - (HostWindowWidth / 2),
            SystemParameters.VirtualScreenLeft - RestingViewportInsetX,
            Math.Max(
                SystemParameters.VirtualScreenLeft - RestingViewportInsetX,
                SystemParameters.VirtualScreenLeft +
                SystemParameters.VirtualScreenWidth - RestingWindowWidth - RestingViewportInsetX));
        Top = Clamp(
            centerY - (HostWindowHeight / 2),
            SystemParameters.VirtualScreenTop - RestingViewportInsetY,
            Math.Max(
                SystemParameters.VirtualScreenTop - RestingViewportInsetY,
                SystemParameters.VirtualScreenTop +
                SystemParameters.VirtualScreenHeight - RestingWindowHeight - RestingViewportInsetY));
        _autonomousWindowScaleApplied = false;
        _autonomousWindowCenterOffsetX = 0;
        _autonomousWindowCenterOffsetY = 0;
    }

    private void ApplySeatedWindowScale()
    {
        var targetWidth = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowWidth,
            PetAnimations.SeatedContentScale);
        var targetHeight = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowHeight,
            PetAnimations.SeatedContentScale);
        if (Math.Abs(ViewportWidth - targetWidth) < 0.01 &&
            Math.Abs(ViewportHeight - targetHeight) < 0.01)
        {
            _seatedWindowScaleApplied = true;
            return;
        }

        SetPetViewportSize(targetWidth, targetHeight);
        _seatedWindowScaleApplied = true;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void RestoreWindowSizeAfterSeatedRoutine()
    {
        if (!_seatedWindowScaleApplied)
        {
            return;
        }

        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        _seatedWindowScaleApplied = false;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void ApplyTemporarySeatedWindowScale()
    {
        var targetWidth = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowWidth,
            PetAnimations.SeatedContentScale);
        var targetHeight = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowHeight,
            PetAnimations.SeatedContentScale);
        if (Math.Abs(ViewportWidth - targetWidth) < 0.01 &&
            Math.Abs(ViewportHeight - targetHeight) < 0.01)
        {
            _temporarySeatedWindowScaleApplied = true;
            return;
        }

        SetPetViewportSize(targetWidth, targetHeight);
        _temporarySeatedWindowScaleApplied = true;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void RestoreWindowSizeAfterTemporarySeatedMood()
    {
        if (!_temporarySeatedWindowScaleApplied)
        {
            return;
        }

        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        _temporarySeatedWindowScaleApplied = false;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void ApplyClickFlickFallWindowScale()
    {
        var targetWidth = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowWidth,
            PetAnimations.ClickFlickFallContentScale);
        var targetHeight = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowHeight,
            PetAnimations.ClickFlickFallContentScale);
        // Only the centered visual viewport changes.  The transparent native
        // host remains fixed, so a confirmed click cannot move or resize it.
        SetPetViewportSize(targetWidth, targetHeight);
        // The click window now directly owns the resulting geometry. Clearing
        // the former owners prevents their canceled async finalizers from
        // restoring the window underneath ClickFlickFall's first frames.
        _autonomousWindowScaleApplied = false;
        _autonomousWindowCenterOffsetX = 0;
        _autonomousWindowCenterOffsetY = 0;
        _seatedWindowScaleApplied = false;
        _temporarySeatedWindowScaleApplied = false;
        _cuteAngryWindowScaleApplied = false;
        _clickFlickFallWindowScaleApplied = true;
        _dragLandingWindowScaleApplied = false;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void RestoreWindowSizeAfterClickFlickFall()
    {
        if (!_clickFlickFallWindowScaleApplied)
        {
            return;
        }

        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        _clickFlickFallWindowScaleApplied = false;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void TransferClickFlickFallScaleToCuteAngry()
    {
        // Do not restore to the resting size between the two one-shots. Clear
        // the old ownership flag and resize directly around the same center so
        // CuteAngry F000 and its calibrated geometry are presented together.
        _clickFlickFallWindowScaleApplied = false;
        ApplyCuteAngryWindowScale();
    }

    private void ApplyCuteAngryWindowScale()
    {
        var targetWidth = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowWidth,
            PetAnimations.CuteAngryWindowScale);
        var targetHeight = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowHeight,
            PetAnimations.CuteAngryWindowScale);
        if (Math.Abs(ViewportWidth - targetWidth) < 0.01 &&
            Math.Abs(ViewportHeight - targetHeight) < 0.01)
        {
            _cuteAngryWindowScaleApplied = true;
            return;
        }

        SetPetViewportSize(targetWidth, targetHeight);
        _cuteAngryWindowScaleApplied = true;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void RestoreWindowSizeAfterCuteAngry()
    {
        if (!_cuteAngryWindowScaleApplied)
        {
            return;
        }

        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        _cuteAngryWindowScaleApplied = false;
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void CancelTemporaryMoodAndRestoreScale(
        bool preserveQueuedNormalTransition = false,
        bool restoreWindowScale = true)
    {
        var owner = _temporaryMoodOwner;
        var currentCts = _temporaryMoodCts;
        var pending = _pendingVisualTransition;
        var preserveCurrentCts =
            preserveQueuedNormalTransition &&
            currentCts is not null &&
            pending is not null &&
            pending.CancellationToken == currentCts.Token;
        if (!preserveCurrentCts)
        {
            var cts = Interlocked.Exchange(ref _temporaryMoodCts, null);
            CancelWithoutThrow(cts);
        }

        if (!preserveQueuedNormalTransition)
        {
            CancelPendingVisualTransition();
        }

        CancelAnimationBoundary(owner);
        _isTemporaryMoodActive = false;
        _temporaryMoodOwner = PetVisualOwner.Temporary;
        _temporaryMoodPlaybackGeneration = 0;
        if (restoreWindowScale)
        {
            RestoreWindowSizeAfterTemporarySeatedMood();
            RestoreWindowSizeAfterCuteAngry();
            if (_visualOwner == owner)
            {
                RestoreWindowSizeAfterClickFlickFall();
            }
        }
    }

    private void MovementTimer_Tick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        UpdateWorkloadRoutine(now);
        TryPresentTaskContextFeedback(now);
        if (IsWorkloadRoutineActive) return;
        if ((_isSeatedRoutineActive &&
             !(_isSeatedPreviewActive &&
               _visualOwner == PetVisualOwner.Autonomous)) ||
            IsClickFlickFallPendingOrActive ||
            IsCuteAngryPendingOrActive ||
            _singleClickCts is not null)
        {
            return;
        }

        // A press alone is not yet a click or a grab. Keep the current visual
        // owner and scale intact until the recognizer reaches a real outcome.
        if (_pointerGesture.IsPressed)
        {
            return;
        }

        if (!_isAutonomousMoving)
        {
            // Ambient task states share the same real idle video. Keep every
            // choice on a loop boundary, but let an overdue seated routine
            // supersede a lower-priority action that has not started yet.
            var canStartOrdinaryIdleAction = UpdatePendingStandingBoundaryAction(now);
            var ordinaryAutonomousActionDue =
                now >= _nextAutonomousActionAtMilliseconds;

            if (_pendingIdleBoundaryAction == PetIdleBoundaryAction.None &&
                ordinaryAutonomousActionDue &&
                !canStartOrdinaryIdleAction &&
                !PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood))
            {
                // A due movement remains due while an ambient gesture or other
                // non-forcing visual finishes. Otherwise every 2-5 second
                // gesture could postpone the rarer walk/run slot by 12-20
                // seconds and starve locomotion indefinitely.
                ScheduleNextAutonomousAction(now);
            }

            return;
        }

        if (_isClosing || !IsAutonomousMovementMood(_currentMood))
        {
            StopAutonomousMovement(
                restorePersistentMood: !_isTemporaryMoodActive && !_isClosing);
            return;
        }

        if (now >= _autonomousActionEndsAtMilliseconds)
        {
            if (IsWalkingMood(_currentMood) && !_autonomousStopRequested)
            {
                // The random wall-clock duration requests a stop; the animation
                // timer performs it at the next real 8-frame wrap. This deadline
                // is only the fallback if that wrap never arrives.
                _autonomousStopRequested = true;
                var sequence = _currentSequence!;
                _autonomousActionEndsAtMilliseconds = now +
                    (long)Math.Round(PetAnimations.CalculateAnimationWatchdog(
                        sequence.Frames.Count,
                        sequence.FrameDuration).TotalMilliseconds);
            }
            else if (PetAnimations.Definitions[_currentSequence!.VisualMood].Loop)
            {
                _autonomousStopRequested = true;
                _autonomousActionEndsAtMilliseconds = now +
                    (long)Math.Round(PetAnimations.CalculateAnimationWatchdog(
                        _currentSequence.Frames.Count,
                        _currentSequence.FrameDuration).TotalMilliseconds);
            }
            else
            {
                // A wall-clock watchdog may notice dispatcher delay, but it is
                // never an animation boundary. Keep the one-shot alive until the
                // timer publishes its real final frame.
                _autonomousActionEndsAtMilliseconds = now +
                    (long)Math.Round(PetAnimations.CalculateAnimationWatchdog(
                        _currentSequence!.Frames.Count,
                        _currentSequence.FrameDuration).TotalMilliseconds);
            }
        }

        var previousMovementTickMilliseconds = _lastMovementTickMilliseconds;
        var elapsedSeconds = Math.Clamp(
            (now - previousMovementTickMilliseconds) / 1000.0,
            0,
            0.1);
        _lastMovementTickMilliseconds = now;

        if (IsWalkingMood(_currentMood))
        {
            if (_activeFollowRoute is not null)
            {
                AdvanceFollowTargetWalk(elapsedSeconds);
            }
            else
            {
                AdvanceHorizontalWalk(elapsedSeconds);
            }
        }
        else if (_currentMood == PetMood.ClimbingUp)
        {
            var minimumTop = SystemParameters.VirtualScreenTop - ViewportInsetY;
            Top = Math.Max(minimumTop, Top - (_autonomousMovementSpeed * elapsedSeconds));
        }
        else if (_currentMood == PetMood.JumpingDown)
        {
            // Keep the window fixed during anticipation and landing recovery.
            // The frame gate follows what is actually visible even if the UI
            // dispatcher briefly stalls and the absolute wall clock drifts.
            if (PetAnimations.IsJumpingDownTravelFrame(_frameIndex))
            {
                var petHeight = ViewportHeight;
                var maximumTop = Math.Max(
                    SystemParameters.VirtualScreenTop - ViewportInsetY,
                    SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight -
                    petHeight - ViewportInsetY);
                Top = Math.Min(
                    maximumTop,
                    Top + (_autonomousMovementSpeed * elapsedSeconds));
            }
        }
        else if (IsRunFallMood(_currentMood) &&
                 PetAnimations.IsRunFallTravelFrame(_frameIndex))
        {
            var minimumLeft = SystemParameters.VirtualScreenLeft - ViewportInsetX;
            var maximumLeft = Math.Max(
                minimumLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth -
                ViewportWidth - ViewportInsetX);
            var direction = _currentMood == PetMood.RunFallLeft ? -1 : 1;
            Left = Clamp(
                Left + (direction * _autonomousMovementSpeed * elapsedSeconds),
                minimumLeft,
                maximumLeft);
        }

        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
    }

    private void AdvanceHorizontalWalk(double elapsedSeconds)
    {
        var petWidth = ViewportWidth;
        var minimumLeft = SystemParameters.VirtualScreenLeft - ViewportInsetX;
        var maximumLeft = Math.Max(
            minimumLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth -
            petWidth - ViewportInsetX);
        var nextLeft = Left + (_walkDirection * _autonomousMovementSpeed * elapsedSeconds);

        if (nextLeft <= minimumLeft)
        {
            nextLeft = minimumLeft;
            QueueWalkDirectionAtLoopBoundary(1);
        }
        else if (nextLeft >= maximumLeft)
        {
            nextLeft = maximumLeft;
            QueueWalkDirectionAtLoopBoundary(-1);
        }

        Left = nextLeft;
    }

    private void AdvanceFollowTargetWalk(double elapsedSeconds)
    {
        var route = _activeFollowRoute;
        if (route is null)
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (!DesktopPointerTargetService.TryGetWindowBounds(
                windowHandle,
                out var currentBounds))
        {
            _autonomousStopRequested = true;
            return;
        }

        var dpiScale = DesktopPointerTargetService.GetWindowDpiScale(windowHandle);
        var viewportWidthPixels = ViewportWidth * dpiScale;
        var viewportHeightPixels = ViewportHeight * dpiScale;
        var viewportInsetXPixels = ViewportInsetX * dpiScale;
        var viewportInsetYPixels = ViewportInsetY * dpiScale;
        PetFollowPoint targetTopLeft;
        try
        {
            targetTopLeft = PetFollowMovementPolicy.CalculateTargetTopLeft(
                route.TargetKind,
                viewportWidthPixels,
                viewportHeightPixels,
                route.Snapshot.Cursor,
                route.Snapshot.WorkArea,
                dpiScale);
        }
        catch (ArgumentException)
        {
            _autonomousStopRequested = true;
            return;
        }

        var currentTopLeft = new PetFollowPoint(
            currentBounds.Left + viewportInsetXPixels,
            currentBounds.Top + viewportInsetYPixels);
        if (PetFollowMovementPolicy.HasArrived(
                currentTopLeft,
                targetTopLeft,
                dpiScale))
        {
            if (!DesktopPointerTargetService.TryMoveWindow(
                    windowHandle,
                    new PetFollowPoint(
                        targetTopLeft.X - viewportInsetXPixels,
                        targetTopLeft.Y - viewportInsetYPixels)))
            {
                _autonomousStopRequested = true;
                return;
            }

            _autonomousStopRequested = true;
            return;
        }

        var horizontalDelta = targetTopLeft.X - currentTopLeft.X;
        var horizontalDeadzone =
            PetFollowMovementPolicy.HorizontalFacingDeadzoneDips * dpiScale;
        var desiredDirection = horizontalDelta < -horizontalDeadzone
            ? -1
            : horizontalDelta > horizontalDeadzone
                ? 1
                : _walkDirection;
        if (desiredDirection != _walkDirection)
        {
            // The two directional clips switch only at a real stride wrap.
            // Hold position until that wrap rather than sliding backwards.
            QueueWalkDirectionAtLoopBoundary(desiredDirection);
            return;
        }

        var nextTopLeft = PetFollowMovementPolicy.AdvanceToward(
            currentTopLeft,
            targetTopLeft,
            _autonomousMovementSpeed * elapsedSeconds);
        if (!DesktopPointerTargetService.TryMoveWindow(
                windowHandle,
                new PetFollowPoint(
                    nextTopLeft.X - viewportInsetXPixels,
                    nextTopLeft.Y - viewportInsetYPixels)))
        {
            _autonomousStopRequested = true;
            return;
        }

        if (PetFollowMovementPolicy.HasArrived(
                nextTopLeft,
                targetTopLeft,
                dpiScale))
        {
            _autonomousStopRequested = true;
        }
    }

    private bool CanStartStandingAmbientAction() =>
        !_isClosing &&
        !IsWorkloadRoutineActive &&
        !IsClickFlickFallPendingOrActive &&
        !IsCuteAngryPendingOrActive &&
        _singleClickCts is null &&
        _temporaryMoodCts is null &&
        !_pointerGesture.IsPressed &&
        !_isTemporaryMoodActive &&
        !_isSeatedRoutineActive &&
        !_isAutonomousMoving &&
        _visualOwner == PetVisualOwner.Persistent &&
        _currentSequence?.VisualMood == PetMood.Idle &&
        PetSeatedRoutinePolicy.CanRunSeatedRoutine(
            _persistentMood,
            _currentMood);

    private bool CanQueueSeatedRoutine() =>
        !_isClosing &&
        !IsWorkloadRoutineActive &&
        !IsClickFlickFallPendingOrActive &&
        !IsCuteAngryPendingOrActive &&
        _singleClickCts is null &&
        _temporaryMoodCts is null &&
        !_pointerGesture.IsPressed &&
        !_isTemporaryMoodActive &&
        !_isSeatedRoutineActive &&
        !_isAutonomousMoving &&
        _visualOwner == PetVisualOwner.Persistent &&
        _currentSequence?.VisualMood == PetMood.Idle &&
        PetSeatedRoutinePolicy.CanRunSeatedRoutine(
            _persistentMood,
            _currentMood);

    private bool StartAutonomousMovement(long now)
    {
        var spriteAtlas = _spriteAtlas;
        if (spriteAtlas is null)
        {
            return false;
        }

        var ordinaryActionDue = now >= _nextAutonomousActionAtMilliseconds;
        var followActionDue =
            !ForceRightWalkPreview &&
            PetFollowMovementPolicy.IsDeadlineDue(
                now,
                _nextFollowActionAtMilliseconds);
        if (followActionDue)
        {
            if (TryStartFollowMovement(now, spriteAtlas))
            {
                return true;
            }

            ScheduleNextFollowAction(now, PetFollowScheduleKind.Retry);
            if (!ordinaryActionDue)
            {
                // A failed cursor/center target lookup belongs only to the
                // independent follow clock. It must not trigger an ordinary
                // walk or consume that walk/run deadline early.
                return false;
            }
        }

        if (!ordinaryActionDue)
        {
            return false;
        }

        var petWidth = ViewportWidth;
        var petHeight = ViewportHeight;
        var minimumLeft = SystemParameters.VirtualScreenLeft - ViewportInsetX;
        var maximumLeft = Math.Max(
            minimumLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth -
            petWidth - ViewportInsetX);
        var minimumTop = SystemParameters.VirtualScreenTop - ViewportInsetY;
        var maximumTop = Math.Max(
            minimumTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight -
            petHeight - ViewportInsetY);
        var runFallWidth = PetAnimations.CalculateContentScaledWindowDimension(
            RestingWindowWidth,
            PetAnimations.RunFallContentScale);
        var runFallMaximumLeft = Math.Max(
            minimumLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - runFallWidth);
        var runFallProjectedLeft = Clamp(
                CurrentViewportLeft + (petWidth / 2) - (runFallWidth / 2),
            minimumLeft,
            runFallMaximumLeft);
        var runFallAvailableLeft = runFallProjectedLeft - minimumLeft;
        var runFallAvailableRight = runFallMaximumLeft - runFallProjectedLeft;

        var allowVerticalAmbientActions = _persistentMood == PetMood.Idle;
        var candidates = new List<PetMood>
        {
            PetMood.Walking,
            PetMood.Walking,
            PetMood.Walking,
            PetMood.Walking
        };
        if (allowVerticalAmbientActions &&
            !ForceRightWalkPreview && spriteAtlas.UsesExternalFrames(PetMood.ClimbingUp) &&
            Top > minimumTop + MinimumVerticalActionTravel)
        {
            candidates.Add(PetMood.ClimbingUp);
        }

        if (allowVerticalAmbientActions &&
            !ForceRightWalkPreview && spriteAtlas.UsesExternalFrames(PetMood.JumpingDown) &&
            Top < maximumTop - MinimumVerticalActionTravel)
        {
            candidates.Add(PetMood.JumpingDown);
        }

        var availableRunFallMoods = new List<PetMood>(2);
        if (PetAnimations.IsRunFallRightAutonomousCandidate(
                ForceRightWalkPreview,
                runFallAvailableRight,
                spriteAtlas.UsesExternalFrames(PetMood.RunFallRight)))
        {
            availableRunFallMoods.Add(PetMood.RunFallRight);
        }

        if (PetAnimations.IsRunFallLeftAutonomousCandidate(
                ForceRightWalkPreview,
                runFallAvailableLeft,
                spriteAtlas.UsesExternalFrames(PetMood.RunFallLeft)))
        {
            availableRunFallMoods.Add(PetMood.RunFallLeft);
        }

        if (availableRunFallMoods.Count > 0)
        {
            // Preserve the existing single RunFall candidate weight. When both
            // sides have room, choose the direction inside that one slot.
            candidates.Add(availableRunFallMoods[
                Random.Shared.Next(availableRunFallMoods.Count)]);
        }

        var selectedMood = candidates[Random.Shared.Next(candidates.Count)];

        _activeFollowRoute = null;
        _isAutonomousMoving = true;
        _autonomousStopRequested = false;
        _autonomousCompletedCycleCount = 0;
        _pendingWalkDirection = 0;
        _nextIdleGestureAtMilliseconds = long.MaxValue;
        _lastMovementTickMilliseconds = now;

        if (selectedMood == PetMood.ClimbingUp)
        {
            var cycleDuration = TimeSpan.FromMilliseconds(
                spriteAtlas.GetFrameCount(PetMood.ClimbingUp) *
                spriteAtlas.GetFrameDuration(PetMood.ClimbingUp).TotalMilliseconds);
            var actionDuration = TimeSpan.FromMilliseconds(
                cycleDuration.TotalMilliseconds * PetAnimations.ClimbingAutonomousCycleCount);
            var watchdogDuration = PetAnimations.CalculateAnimationWatchdog(
                checked(spriteAtlas.GetFrameCount(PetMood.ClimbingUp) *
                        PetAnimations.ClimbingAutonomousCycleCount),
                spriteAtlas.GetFrameDuration(PetMood.ClimbingUp));
            _autonomousMovementSpeed = PetAnimations.FitMovementSpeedToDistance(
                RandomBetween(36, 46),
                Top - minimumTop,
                actionDuration);
            ApplyAutonomousWindowScale(PetAnimations.ClimbingWindowScale);
            ApplyMoodAtAnimationBoundary(PetMood.ClimbingUp, PetVisualOwner.Autonomous);
            _autonomousActionEndsAtMilliseconds = Environment.TickCount64 +
                (long)Math.Round(watchdogDuration.TotalMilliseconds);
            return true;
        }

        if (selectedMood == PetMood.JumpingDown)
        {
            var frameDuration = spriteAtlas.GetFrameDuration(PetMood.JumpingDown);
            var watchdogDuration = PetAnimations.CalculateAnimationWatchdog(
                spriteAtlas.GetFrameCount(PetMood.JumpingDown),
                frameDuration);
            var travelDuration = TimeSpan.FromMilliseconds(
                (PetAnimations.JumpingDownTravelEndFrameExclusive -
                 PetAnimations.JumpingDownTravelStartFrame) *
                frameDuration.TotalMilliseconds);
            ApplyAutonomousContentScale(
                PetAnimations.JumpingContentScale,
                PetAnimations.JumpingCenterOffsetX,
                PetAnimations.JumpingCenterOffsetY);
            var scaledMaximumTop = Math.Max(
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight -
                ViewportHeight - ViewportInsetY);
            _autonomousMovementSpeed = PetAnimations.FitMovementSpeedToDistance(
                RandomBetween(72, 92),
                scaledMaximumTop - Top,
                travelDuration);
            // Completion is owned by the animation timer's real final-frame
            // boundary. Wall clock is only a stuck-timer watchdog.
            ApplyMoodAtAnimationBoundary(PetMood.JumpingDown, PetVisualOwner.Autonomous);
            _autonomousActionEndsAtMilliseconds = Environment.TickCount64 +
                (long)Math.Round(watchdogDuration.TotalMilliseconds);
            return true;
        }

        if (IsRunFallMood(selectedMood))
        {
            // Run/fall is its own one-shot action. The standing Idle boundary
            // starts its first real frame directly; Walking never acts as a
            // prelude or hands off into this state.
            StartRunFallAtAnimationBoundary(
                now,
                PetAnimations.ResolveAutonomousEntryMood(
                    selectedMood,
                    _walkDirection));
            StartRunFallVoicePreparation();
            if (PetRunDialoguePolicy.ShouldSpeakRunningEffort(
                    Random.Shared.NextDouble()))
            {
                DeliverInstantActionReaction(PetDialogueIntent.RunningEffort);
            }

            return true;
        }

        _autonomousMovementSpeed = RandomBetween(32, 48);
        var direction = ForceRightWalkPreview
            ? 1
            : Left <= minimumLeft + 1
                ? 1
                : Left >= maximumLeft - 1
                    ? -1
                    : Random.Shared.Next(2) == 0 ? -1 : 1;
        var walkingMood = PetAnimations.ResolveAutonomousEntryMood(
            PetMood.Walking,
            direction);
        _autonomousActionEndsAtMilliseconds = now +
            (long)Math.Round(PetAnimations.CalculateAnimationWatchdog(
                checked(
                    spriteAtlas.GetFrameCount(walkingMood) *
                    PetAnimations.WalkingPlaybackCycleCount),
                spriteAtlas.GetFrameDuration(walkingMood)).TotalMilliseconds);
        SetWalkDirection(direction);
        ApplyMoodAtAnimationBoundary(
            walkingMood,
            PetVisualOwner.Autonomous);
        return true;
    }

    private bool TryStartFollowMovement(long now, SpriteAtlas spriteAtlas)
    {
        if (!spriteAtlas.UsesExternalFrames(PetMood.Walking) ||
            !spriteAtlas.UsesExternalFrames(PetMood.WalkingLeft))
        {
            return false;
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (!DesktopPointerTargetService.TryCapturePointerMonitor(
                out var pointerMonitor) ||
            !DesktopPointerTargetService.TryGetWindowBounds(
                windowHandle,
                out var currentBounds))
        {
            return false;
        }

        var dpiScale = DesktopPointerTargetService.GetWindowDpiScale(windowHandle);
        var currentViewportBounds = new PetFollowBounds(
            currentBounds.Left + (ViewportInsetX * dpiScale),
            currentBounds.Top + (ViewportInsetY * dpiScale),
            ViewportWidth * dpiScale,
            ViewportHeight * dpiScale);
        var preferredTarget = ForceFollowPreview
            ? PetFollowTargetKind.Cursor
            : PetFollowMovementPolicy.SelectPreferredTarget(
                Random.Shared.NextDouble());
        if (!PetFollowMovementPolicy.TryCreatePlan(
                preferredTarget,
                currentViewportBounds,
                pointerMonitor.Cursor,
                [pointerMonitor.WorkArea],
                dpiScale,
                _walkDirection,
                out var plan))
        {
            return false;
        }

        var walkingMood = WalkingMoodForDirection(plan.FacingDirection);
        var walkingLoopDuration = TimeSpan.FromMilliseconds(
            spriteAtlas.GetFrameCount(walkingMood) *
            spriteAtlas.GetFrameDuration(walkingMood).TotalMilliseconds);
        var movementSpeed = PetFollowMovementPolicy.CalculateLoopAlignedSpeed(
            plan.Distance,
            walkingLoopDuration,
            dpiScale);
        var watchdog = PetFollowMovementPolicy.CalculateWatchdog(
            plan.Distance,
            movementSpeed,
            walkingLoopDuration);

        _activeFollowRoute = new ActiveFollowRoute(
            plan.TargetKind,
            new DesktopPointerMonitorSnapshot(
                pointerMonitor.Cursor,
                plan.WorkArea));
        _isAutonomousMoving = true;
        _autonomousStopRequested = false;
        _autonomousCompletedCycleCount = 0;
        _pendingWalkDirection = 0;
        _nextIdleGestureAtMilliseconds = long.MaxValue;
        _lastMovementTickMilliseconds = now;
        _autonomousMovementSpeed = movementSpeed;
        _autonomousActionEndsAtMilliseconds = now +
            (long)Math.Round(watchdog.TotalMilliseconds);
        ScheduleNextFollowAction(now, PetFollowScheduleKind.Recurring);
        SetWalkDirection(plan.FacingDirection);
        ApplyMoodAtAnimationBoundary(walkingMood, PetVisualOwner.Autonomous);
        return true;
    }

    private void StopAutonomousMovement(
        bool restorePersistentMood,
        bool restoreWindowScale = true)
    {
        var wasMoving = _isAutonomousMoving;
        var wasFollowMovement = _activeFollowRoute is not null;
        var canRestorePersistentMood =
            wasMoving && restorePersistentMood && !_pointerGesture.IsPressed &&
            !_isTemporaryMoodActive && !_isClosing &&
            !IsClickFlickFallPendingOrActive &&
            !IsCuteAngryPendingOrActive;
        // Decode/cache the target before changing the enlarged autonomous
        // geometry. At a real frame boundary this lets the normal-size target
        // frame and the restored window size commit in one dispatcher turn,
        // so the final run/fall frame is never rendered briefly shrunk.
        var preparedPersistentSequence = canRestorePersistentMood
            ? _spriteAtlas?.PrepareSequence(_persistentMood)
            : null;
        _isAutonomousMoving = false;
        _autonomousActionEndsAtMilliseconds = 0;
        _lastMovementTickMilliseconds = 0;
        _autonomousStopRequested = false;
        CancelRunFallVoicePreparation();
        _autonomousCompletedCycleCount = 0;
        _activeFollowRoute = null;
        _pendingWalkDirection = 0;
        if (restoreWindowScale)
        {
            RestoreWindowSizeAfterAutonomousMovement();
        }
        if (!wasFollowMovement)
        {
            ScheduleNextAutonomousAction(Environment.TickCount64);
        }
        ScheduleNextIdleGestureForAmbientState(Environment.TickCount64);

        if (canRestorePersistentMood)
        {
            if (preparedPersistentSequence is not null &&
                IsAtCurrentAnimationBoundary())
            {
                ApplyMoodAtAnimationBoundary(
                    _persistentMood,
                    PetVisualOwner.Persistent,
                    preparedPersistentSequence);
            }
            else
            {
                _ = RequestPersistentMoodTransition(_persistentMood);
            }
        }
    }

    private void StartRunFallAtAnimationBoundary(long now, PetMood runFallMood)
    {
        if (!IsRunFallMood(runFallMood))
        {
            throw new ArgumentOutOfRangeException(nameof(runFallMood));
        }

        var spriteAtlas = _spriteAtlas!;
        var watchdogDuration = PetAnimations.CalculateAnimationWatchdog(
            spriteAtlas.GetFrameCount(runFallMood),
            spriteAtlas.GetFrameDuration(runFallMood));
        var frameDuration = spriteAtlas.GetFrameDuration(runFallMood);
        var travelDuration = TimeSpan.FromMilliseconds(
            (PetAnimations.RunFallTravelEndFrameExclusive -
             PetAnimations.RunFallTravelStartFrame) *
            frameDuration.TotalMilliseconds);
        ApplyAutonomousContentScale(PetAnimations.RunFallContentScale);
        var minimumLeft = SystemParameters.VirtualScreenLeft;
        var visibleMaximumLeft = Math.Max(
            minimumLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth -
            ViewportWidth);
        var availableDistance = runFallMood == PetMood.RunFallLeft
            ? CurrentViewportLeft - SystemParameters.VirtualScreenLeft
            : visibleMaximumLeft - CurrentViewportLeft;
        _autonomousMovementSpeed = PetAnimations.FitMovementSpeedToDistance(
            RandomBetween(42, 52),
            availableDistance,
            travelDuration);
        ApplyMoodAtAnimationBoundary(runFallMood, PetVisualOwner.Autonomous);
        _autonomousActionEndsAtMilliseconds = now +
            (long)Math.Round(watchdogDuration.TotalMilliseconds);
    }

    private void StartRunFallVoicePreparation()
    {
        CancelRunFallVoicePreparation();
        var line = PetActionDialogueContext.ChooseInstantReaction(
            PetDialogueIntent.RunFallImpact);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _dialogueLifetimeCts.Token);
        _runFallVoicePreparationCts = cts;
        _preparedRunFallLine = line;
        _runFallImpactVoiceDelivered = false;
        _preparedRunFallSpeechTask = PrepareRunFallSpeechAsync(line, cts.Token);
    }

    private async Task<SpeechService.PreparedSpeech?> PrepareRunFallSpeechAsync(
        string line,
        CancellationToken cancellationToken)
    {
        if (!_settings.TtsEnabled)
        {
            return null;
        }

        try
        {
            return await _speech.PrepareAsync(
                    line,
                    PetActionDialogueContext.SpeechPerformance(
                        PetDialogueIntent.RunFallImpact),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Run-fall voice preparation failed: {ex.GetType().Name}");
            return null;
        }
    }

    private void TryDeliverRunFallImpactReaction()
    {
        if (_runFallImpactVoiceDelivered ||
            _visualOwner != PetVisualOwner.Autonomous ||
            !IsRunFallMood(_currentMood) ||
            !PetAnimations.IsRunFallImpactFrame(_frameIndex))
        {
            return;
        }

        _runFallImpactVoiceDelivered = true;
        var line = _preparedRunFallLine;
        var speechTask = _preparedRunFallSpeechTask;
        var cts = _runFallVoicePreparationCts;
        if (string.IsNullOrWhiteSpace(line) || cts is null)
        {
            DeliverInstantActionReaction(PetDialogueIntent.RunFallImpact);
            return;
        }

        _ = DeliverRunFallImpactReactionAsync(line, speechTask, cts.Token);
    }

    private async Task DeliverRunFallImpactReactionAsync(
        string line,
        Task<SpeechService.PreparedSpeech?>? speechTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var preparedSpeech = speechTask is null
                ? null
                : await speechTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var stillFalling = await Dispatcher.InvokeAsync(() =>
                !_isClosing &&
                _visualOwner == PetVisualOwner.Autonomous &&
                IsRunFallMood(_currentMood));
            if (!stillFalling)
            {
                return;
            }

            _speech.Stop();
            await PresentCharacterLineAsync(
                line,
                isImportant: false,
                visibleDuration: TimeSpan.FromSeconds(3),
                cancellationToken: cancellationToken,
                speechPerformance: PetActionDialogueContext.SpeechPerformance(
                    PetDialogueIntent.RunFallImpact),
                preparedSpeech: preparedSpeech);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Run-fall impact reaction failed: {ex.GetType().Name}");
        }
    }

    private void CancelRunFallVoicePreparation()
    {
        var cts = _runFallVoicePreparationCts;
        var task = _preparedRunFallSpeechTask;
        _runFallVoicePreparationCts = null;
        _preparedRunFallSpeechTask = null;
        _preparedRunFallLine = null;
        _runFallImpactVoiceDelivered = false;
        CancelWithoutThrow(cts);
        if (cts is null)
        {
            return;
        }

        if (task is null || task.IsCompleted)
        {
            cts.Dispose();
            return;
        }

        _ = task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SetWalkDirection(int direction)
    {
        _pendingWalkDirection = 0;
        _walkDirection = direction < 0 ? -1 : 1;
        if (!_isAutonomousMoving || !IsWalkingMood(_currentMood))
        {
            return;
        }

        var nextMood = WalkingMoodForDirection(_walkDirection);
        if (_currentMood == nextMood)
        {
            return;
        }

        var preservedFrameIndex = _frameIndex;
        var prepared = _spriteAtlas!.PrepareSequence(nextMood);
        var frameCount = prepared.Frames.Count;
        CancelAnimationBoundary();
        _playbackGeneration++;
        _currentMood = nextMood;
        _currentSequence = prepared;
        _frameIndex = Math.Clamp(preservedFrameIndex, 0, frameCount - 1);
        _animationTimer.Interval = prepared.FrameDuration;
        PetImage.Source = prepared.Frames[_frameIndex];
    }

    private void QueueWalkDirectionAtLoopBoundary(int direction)
    {
        var normalizedDirection = direction < 0 ? -1 : 1;
        if (normalizedDirection == _walkDirection)
        {
            _pendingWalkDirection = 0;
            return;
        }

        // The left/right videos are independently generated because the outfit
        // is asymmetric. Switching them in the middle of a stride produces a
        // visible pose jump, so hold at the screen edge until the current real
        // 8-frame cycle reaches its wrap point.
        _pendingWalkDirection = normalizedDirection;
    }

    private bool ApplyPendingWalkDirectionAtLoopBoundary()
    {
        if (_pendingWalkDirection == 0 || !IsWalkingMood(_currentMood))
        {
            return false;
        }

        var direction = _pendingWalkDirection;
        _pendingWalkDirection = 0;
        _walkDirection = direction;
        var nextMood = WalkingMoodForDirection(direction);
        if (_currentMood == nextMood)
        {
            return false;
        }

        var prepared = _spriteAtlas!.PrepareSequence(nextMood);
        CancelAnimationBoundary();
        _playbackGeneration++;
        _currentMood = nextMood;
        _currentSequence = prepared;
        _frameIndex = 0;
        _animationTimer.Interval = prepared.FrameDuration;
        PetImage.Source = prepared.Frames[0];
        return true;
    }

    private bool UpdatePendingStandingBoundaryAction(long now)
    {
        var canStartOrdinaryIdleAction = CanStartStandingAmbientAction();
        var autonomousActionDue = now >= _nextAutonomousActionAtMilliseconds ||
            !ForceRightWalkPreview &&
            PetFollowMovementPolicy.IsDeadlineDue(now, _nextFollowActionAtMilliseconds);
        _pendingIdleBoundaryAction = PetSeatedRoutinePolicy.SelectBoundaryAction(
            _pendingIdleBoundaryAction,
            PetSeatedRoutinePolicy.IsDeadlineDue(now, _nextSeatedRoutineAtMilliseconds),
            CanQueueSeatedRoutine(),
            now >= _nextIdleGestureAtMilliseconds,
            autonomousActionDue,
            canStartOrdinaryIdleAction);
        return canStartOrdinaryIdleAction;
    }

    private bool TryStartPendingIdleBoundaryAction()
    {
        // Re-evaluate at the actual wrap as well as on the movement timer.
        // Dispatcher load must not turn a missed 16 ms poll into another full
        // quiet loop. The shared selector keeps seated/movement priorities.
        UpdatePendingStandingBoundaryAction(Environment.TickCount64);
        if (_pendingIdleBoundaryAction == PetIdleBoundaryAction.None)
        {
            return false;
        }

        var action = _pendingIdleBoundaryAction;
        var canStart = action == PetIdleBoundaryAction.SeatedRoutine
            ? CanQueueSeatedRoutine()
            : CanStartStandingAmbientAction();
        if (!canStart)
        {
            return false;
        }

        _pendingIdleBoundaryAction = PetIdleBoundaryAction.None;
        return action switch
        {
            PetIdleBoundaryAction.IdleGesture => TryStartIdleGesture(),
            PetIdleBoundaryAction.SeatedRoutine => TryStartSeatedRoutine(),
            PetIdleBoundaryAction.AutonomousMovement => TryStartAutonomousMovementAtBoundary(),
            _ => false
        };
    }

    private bool TryStartAutonomousMovementAtBoundary()
    {
        var now = Environment.TickCount64;
        if (!CanStartStandingAmbientAction())
        {
            ScheduleNextAutonomousAction(now);
            return false;
        }

        return StartAutonomousMovement(now);
    }

    private static bool IsWalkingMood(PetMood mood) =>
        mood is PetMood.Walking or PetMood.WalkingLeft;

    private static bool IsRunFallMood(PetMood mood) =>
        mood is PetMood.RunFallRight or PetMood.RunFallLeft;

    private static bool IsAutonomousMovementMood(PetMood mood) =>
        IsWalkingMood(mood) ||
        mood is PetMood.ClimbingUp or PetMood.JumpingDown || IsRunFallMood(mood);

    private static PetMood WalkingMoodForDirection(int direction) =>
        direction < 0 ? PetMood.WalkingLeft : PetMood.Walking;

    private void ScheduleNextAutonomousAction(long now) =>
        _nextAutonomousActionAtMilliseconds = now + (ForceRightWalkPreview
            ? 900
            : (long)RandomBetween(12000, 20000));

    private void ScheduleNextFollowAction(
        long now,
        PetFollowScheduleKind scheduleKind)
    {
        _nextFollowActionAtMilliseconds = ForceRightWalkPreview
            ? PetFollowMovementPolicy.UnscheduledDeadline
            : ForceFollowPreview
                ? now + 900
                : PetFollowMovementPolicy.CalculateDeadline(
                    now,
                    PetFollowMovementPolicy.CalculateDelay(
                        scheduleKind,
                        Random.Shared.NextDouble()));
    }

    private bool TryStartIdleGesture()
    {
        if (_spriteAtlas is null || !CanStartStandingAmbientAction())
        {
            return false;
        }

        // Reserve occasional rummaging here; its async preload queues a normal
        // boundary transition without cutting short the following idle gesture.
        TryPresentAutomaticRecycleBinFeedback(Environment.TickCount64);

        var candidates = PetAnimations.CreateIdleGestureCandidates(
            PetAnimations.IdleGestureMoods.Where(_spriteAtlas.UsesExternalFrames),
            _lastIdleGestureMood);

        if (candidates.Count == 0)
        {
            ScheduleNextIdleGesture(Environment.TickCount64);
            return false;
        }

        var mood = PetAnimations.SelectWeightedIdleGesture(
            candidates,
            Random.Shared.NextDouble());
        _lastIdleGestureMood = mood;
        _nextIdleGestureAtMilliseconds = long.MaxValue;
        ApplyMoodAtAnimationBoundary(mood, PetVisualOwner.IdleGesture);
        return true;
    }

    private void ScheduleNextIdleGesture(long now) =>
        _nextIdleGestureAtMilliseconds = now + (long)Math.Round(
            PetAnimations.CalculateIdleGestureDelay(Random.Shared.NextDouble()).TotalMilliseconds);

    private void ScheduleNextIdleGestureForAmbientState(long now)
    {
        if (PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood))
        {
            ScheduleNextIdleGesture(now);
        }
        else
        {
            _nextIdleGestureAtMilliseconds = long.MaxValue;
        }
    }

    private void ScheduleInitialSeatedRoutine(long now) =>
        _nextSeatedRoutineAtMilliseconds = PetSeatedRoutinePolicy.CalculateDeadline(
            now,
            PetSeatedRoutinePolicy.CalculateDelay(
                PetSeatedRoutineScheduleKind.Initial,
                Random.Shared.NextDouble()));

    private void ScheduleNextSeatedRoutine(long now) =>
        _nextSeatedRoutineAtMilliseconds = PetSeatedRoutinePolicy.CalculateDeadline(
            now,
            PetSeatedRoutinePolicy.CalculateDelay(
                PetSeatedRoutineScheduleKind.Recurring,
                Random.Shared.NextDouble()));

    private void EnsureSeatedRoutineScheduled(long now)
    {
        if (_isSeatedRoutineActive ||
            _nextSeatedRoutineAtMilliseconds !=
            PetSeatedRoutinePolicy.UnscheduledDeadline)
        {
            return;
        }

        var candidate = PetSeatedRoutinePolicy.CalculateDeadline(
            now,
            PetSeatedRoutinePolicy.CalculateDelay(
                PetSeatedRoutineScheduleKind.Recurring,
                Random.Shared.NextDouble()));
        _nextSeatedRoutineAtMilliseconds =
            PetSeatedRoutinePolicy.PreserveScheduledDeadline(
                _nextSeatedRoutineAtMilliseconds,
                candidate);
    }

    private bool TryStartSeatedRoutine()
    {
        var spriteAtlas = _spriteAtlas;
        if (spriteAtlas is null || !CanQueueSeatedRoutine())
        {
            return false;
        }

        if (!PetAnimations.SeatedSequenceMoods.All(spriteAtlas.UsesExternalFrames))
        {
            ScheduleNextSeatedRoutine(Environment.TickCount64);
            return false;
        }

        ApplySeatedWindowScale();
        var cts = new CancellationTokenSource();
        _seatedRoutineCts = cts;
        _isSeatedRoutineActive = true;
        _seatedExitRequested = false;
        _seatedRoutineCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _nextSeatedRoutineAtMilliseconds =
            PetSeatedRoutinePolicy.UnscheduledDeadline;
        _nextIdleGestureAtMilliseconds = long.MaxValue;
        _nextAutonomousActionAtMilliseconds = long.MaxValue;

        _ = PlaySeatedRoutineAsync(cts, CreateRandomSeatedRoutinePlan());
        return true;
    }

    private static SeatedRoutinePlan CreateRandomSeatedRoutinePlan()
        => new(PetSeatedRoutinePolicy.CalculateVisitDuration(
            Random.Shared.NextDouble()));

    private async Task PlaySeatedRoutineAsync(
        CancellationTokenSource cts,
        SeatedRoutinePlan plan)
    {
        var standUpCompleted = false;
        try
        {
            await PlaySeatedAnimationOnceAsync(PetMood.SeatedSitDown, cts);
            var visitEndsAtMilliseconds = PetSeatedRoutinePolicy.CalculateDeadline(
                Environment.TickCount64,
                plan.VisitDuration);
            PetMood? previousGesture = null;
            while (!_seatedExitRequested &&
                   !PetSeatedRoutinePolicy.IsDeadlineDue(
                       Environment.TickCount64,
                       visitEndsAtMilliseconds))
            {
                var now = Environment.TickCount64;
                var remaining = TimeSpan.FromMilliseconds(
                    Math.Max(1, visitEndsAtMilliseconds - now));
                var opportunityDelay =
                    PetSeatedRoutinePolicy.CalculateGestureOpportunityDelay(
                        Random.Shared.NextDouble());
                await PlaySeatedIdleLoopForDurationAsync(
                    cts,
                    opportunityDelay <= remaining
                        ? opportunityDelay
                        : remaining);
                if (_seatedExitRequested ||
                    PetSeatedRoutinePolicy.IsDeadlineDue(
                        Environment.TickCount64,
                        visitEndsAtMilliseconds))
                {
                    break;
                }

                if (!PetSeatedRoutinePolicy.ShouldPlayGestureOpportunity(
                        Random.Shared.NextDouble()))
                {
                    continue;
                }

                var gesture = PetSeatedRoutinePolicy.SelectGesture(
                    Random.Shared.NextDouble(),
                    previousGesture);
                await PlaySeatedAnimationOnceAsync(gesture, cts);
                previousGesture = gesture;
            }

            await PlaySeatedAnimationOnceAsync(PetMood.SeatedStandUp, cts);
            standUpCompleted = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Seated animation routine failure", ex);
            if (!standUpCompleted)
            {
                await TryRecoverSeatedStandUpAfterFailureAsync(cts);
            }
        }
        finally
        {
            await CompleteSeatedSessionAsync(
                cts,
                consumedScheduledVisit: true);
        }
    }

    private async Task PlaySeatedPreviewRoutineAsync(
        CancellationTokenSource cts,
        PetMood previewMood)
    {
        var standUpCompleted = false;
        try
        {
            await PlaySeatedAnimationOnceAsync(
                PetMood.SeatedSitDown,
                cts,
                waitForCurrentBoundary: true,
                beforeApply: () =>
                {
                    // Movement, geometry, and the first seated frame change in
                    // one boundary transaction, never midway through a cycle.
                    StopAutonomousMovement(restorePersistentMood: false);
                    ApplySeatedWindowScale();
                });

            if (!_seatedExitRequested)
            {
                if (previewMood is PetMood.SeatedSitDown or PetMood.SeatedIdle)
                {
                    await PlayOneFullSeatedIdleCycleAsync(cts);
                }
                else if (previewMood != PetMood.SeatedStandUp)
                {
                    await PlaySeatedAnimationOnceAsync(previewMood, cts);
                }
            }

            await PlaySeatedAnimationOnceAsync(PetMood.SeatedStandUp, cts);
            standUpCompleted = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Seated animation preview failure", ex);
            if (!standUpCompleted)
            {
                await TryRecoverSeatedStandUpAfterFailureAsync(cts);
            }

            throw;
        }
        finally
        {
            await CompleteSeatedSessionAsync(
                cts,
                consumedScheduledVisit: false);
        }
    }

    private async Task TryRecoverSeatedStandUpAfterFailureAsync(
        CancellationTokenSource cts)
    {
        var mayRecover = false;
        await RunOnUiThreadAsync(() =>
        {
            mayRecover = ReferenceEquals(_seatedRoutineCts, cts) &&
                !cts.IsCancellationRequested &&
                _isSeatedRoutineActive &&
                !_isClosing &&
                !_pointerGesture.IsDragging;
        });
        if (!mayRecover)
        {
            return;
        }

        try
        {
            await PlayRecoverySeatedStandUpAsync(cts);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception recoveryException)
        {
            App.WriteErrorLog(
                "Seated stand-up recovery after animation failure",
                recoveryException);
        }
    }

    private async Task PlayRecoverySeatedStandUpAsync(
        CancellationTokenSource cts)
    {
        await PlaySeatedAnimationOnceAsync(
            PetMood.SeatedStandUp,
            cts,
            waitForCurrentBoundary: true);
    }

    private async Task CompleteSeatedSessionAsync(
        CancellationTokenSource cts,
        bool consumedScheduledVisit)
    {
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_seatedRoutineCts, cts))
                {
                    return;
                }

                RestoreWindowSizeAfterSeatedRoutine();
                _seatedRoutineCts = null;
                _seatedAnimationCompletion?.TrySetCanceled();
                _seatedAnimationCompletion = null;
                _seatedIdleLoopCompletion?.TrySetCanceled();
                _seatedIdleLoopCompletion = null;
                _seatedIdleLoopEndsAtMilliseconds = long.MaxValue;
                _isSeatedRoutineActive = false;
                _isSeatedPreviewActive = false;
                _seatedExitRequested = false;
                var routineCompletion = _seatedRoutineCompletion;
                _seatedRoutineCompletion = null;

                var now = Environment.TickCount64;
                if (consumedScheduledVisit)
                {
                    // Only an autonomous visit consumes and renews its deadline.
                    // A Settings preview preserves the already scheduled visit.
                    ScheduleNextSeatedRoutine(now);
                }
                else
                {
                    EnsureSeatedRoutineScheduled(now);
                }

                ScheduleNextAutonomousAction(now);
                ScheduleNextIdleGestureForAmbientState(now);

                if (!_pointerGesture.IsPressed &&
                    _temporaryMoodCts is null &&
                    !_isTemporaryMoodActive &&
                    !_isAutonomousMoving &&
                    !_isClosing &&
                    !IsClickFlickFallPendingOrActive &&
                    !IsCuteAngryPendingOrActive &&
                    _currentMood != _persistentMood)
                {
                    if (IsAtCurrentAnimationBoundary())
                    {
                        ApplyMoodAtAnimationBoundary(
                            _persistentMood,
                            PetVisualOwner.Persistent);
                    }
                    else
                    {
                        _ = RequestPersistentMoodTransition(_persistentMood);
                    }
                }

                routineCompletion?.TrySetResult(true);
            });
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task PlaySeatedAnimationOnceAsync(
        PetMood mood,
        CancellationTokenSource cts,
        bool waitForCurrentBoundary = false,
        Action? beforeApply = null)
    {
        if (!PetAnimations.IsSeatedMood(mood) ||
            mood == PetMood.SeatedIdle ||
            PetAnimations.Definitions[mood].Loop)
        {
            throw new ArgumentOutOfRangeException(nameof(mood), mood, "需要一次性坐姿动画。");
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdogDuration = TimeSpan.Zero;
        Task<bool> activationTask = Task.FromResult(true);
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                if (!CanContinueSeatedRoutine(
                        cts,
                        allowAutonomousPreviewUntilActivation:
                            waitForCurrentBoundary && _isSeatedPreviewActive))
                {
                    throw new OperationCanceledException(cts.Token);
                }

                var spriteAtlas = _spriteAtlas!;
                var prepared = spriteAtlas.PrepareSequence(mood);
                watchdogDuration = PetAnimations.CalculateSeatedAnimationWatchdog(
                    prepared.Frames.Count,
                    prepared.FrameDuration);

                if (waitForCurrentBoundary)
                {
                    activationTask = RequestNormalVisualTransition(
                        mood,
                        PetVisualOwner.SeatedRoutine,
                        cts.Token,
                        () =>
                        {
                            beforeApply?.Invoke();
                            var applied = ApplyMoodCore(
                                mood,
                                PetVisualOwner.SeatedRoutine,
                                prepared);
                            if (applied)
                            {
                                _seatedAnimationCompletion = completion;
                            }

                            return applied;
                        });
                    return;
                }

                _seatedAnimationCompletion = completion;
                beforeApply?.Invoke();
                if (!ApplyMoodAtAnimationBoundary(
                        mood,
                        PetVisualOwner.SeatedRoutine,
                        prepared))
                {
                    throw new InvalidOperationException(
                        $"坐姿动画 {mood} 未能取得播放权。");
                }
            });

            if (!await activationTask)
            {
                throw new InvalidOperationException($"坐姿动画 {mood} 未能取得播放权。");
            }

            await AwaitSeatedAnimationCompletionAsync(
                completion,
                watchdogDuration,
                mood,
                cts);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_seatedAnimationCompletion, completion))
                {
                    _seatedAnimationCompletion = null;
                }
            });
        }
    }

    private async Task PlaySeatedIdleLoopForDurationAsync(
        CancellationTokenSource cts,
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await RunOnUiThreadAsync(() =>
        {
            if (!CanContinueSeatedRoutine(cts))
            {
                throw new OperationCanceledException(cts.Token);
            }

            var prepared = _spriteAtlas!.PrepareSequence(PetMood.SeatedIdle);
            if (!ApplyMoodAtAnimationBoundary(
                    PetMood.SeatedIdle,
                    PetVisualOwner.SeatedRoutine,
                    prepared))
            {
                throw new InvalidOperationException("坐姿待机循环未能取得播放权。");
            }

            // Let the complete SeatedIdle animation keep looping. The timer
            // releases this interval only at the first real loop boundary on
            // or after the deadline (or after a graceful-exit request).
            _seatedIdleLoopEndsAtMilliseconds =
                PetSeatedRoutinePolicy.CalculateDeadline(
                    Environment.TickCount64,
                    duration);
            _seatedIdleLoopCompletion = completion;
        });

        try
        {
            using var registration = cts.Token.Register(
                () => completion.TrySetCanceled(cts.Token));
            await completion.Task;
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_seatedIdleLoopCompletion, completion))
                {
                    _seatedIdleLoopCompletion = null;
                    _seatedIdleLoopEndsAtMilliseconds = long.MaxValue;
                }
            });
        }
    }

    private async Task PlayOneFullSeatedIdleCycleAsync(CancellationTokenSource cts)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdogDuration = TimeSpan.Zero;
        await RunOnUiThreadAsync(() =>
        {
            if (!CanContinueSeatedRoutine(cts))
            {
                throw new OperationCanceledException(cts.Token);
            }

            var spriteAtlas = _spriteAtlas!;
            watchdogDuration = PetAnimations.CalculateSeatedAnimationWatchdog(
                spriteAtlas.GetFrameCount(PetMood.SeatedIdle),
                spriteAtlas.GetFrameDuration(PetMood.SeatedIdle));
            _seatedAnimationCompletion = completion;
            if (!ApplyMoodAtAnimationBoundary(
                    PetMood.SeatedIdle,
                    PetVisualOwner.SeatedRoutine))
            {
                _seatedAnimationCompletion = null;
                throw new InvalidOperationException("坐姿待机未能取得播放权。");
            }
        });

        try
        {
            await AwaitSeatedAnimationCompletionAsync(
                completion,
                watchdogDuration,
                PetMood.SeatedIdle,
                cts);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_seatedAnimationCompletion, completion))
                {
                    _seatedAnimationCompletion = null;
                }
            });
        }
    }

    private static async Task AwaitSeatedAnimationCompletionAsync(
        TaskCompletionSource<bool> completion,
        TimeSpan watchdogDuration,
        PetMood mood,
        CancellationTokenSource cts)
    {
        using var registration = cts.Token.Register(
            () => completion.TrySetCanceled(cts.Token));
        var watchdogReported = false;
        while (true)
        {
            using var watchdogCts =
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var watchdogTask = Task.Delay(watchdogDuration, watchdogCts.Token);
            var completedTask = await Task.WhenAny(completion.Task, watchdogTask);
            if (ReferenceEquals(completedTask, completion.Task))
            {
                watchdogCts.Cancel();
                await completion.Task;
                return;
            }

            cts.Token.ThrowIfCancellationRequested();
            if (!watchdogReported)
            {
                watchdogReported = true;
                App.WriteErrorLog(
                    $"Seated animation boundary delayed ({mood})",
                    new TimeoutException(
                        $"坐姿动画 {mood} 播放超过 {watchdogDuration.TotalSeconds:F2} 秒；继续等待真实帧边界。"));
            }
        }
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(action).Task;
    }

    private bool CanContinueSeatedRoutine(
        CancellationTokenSource cts,
        bool allowAutonomousPreviewUntilActivation = false) =>
        ReferenceEquals(_seatedRoutineCts, cts) &&
        !cts.IsCancellationRequested &&
        _isSeatedRoutineActive &&
        !_isClosing &&
        !_isTemporaryMoodActive &&
        (!_isAutonomousMoving || allowAutonomousPreviewUntilActivation) &&
        (_isSeatedPreviewActive ||
         PetSeatedRoutinePolicy.IsAmbientIdleMood(_persistentMood) ||
         _seatedExitRequested);

    private Task RequestGracefulSeatedExit()
    {
        if (!_isSeatedRoutineActive)
        {
            return Task.CompletedTask;
        }

        _seatedExitRequested = true;
        _nextIdleGestureAtMilliseconds = long.MaxValue;
        if (!_isSeatedPreviewActive)
        {
            _nextSeatedRoutineAtMilliseconds = long.MaxValue;
        }
        _nextAutonomousActionAtMilliseconds = long.MaxValue;
        _seatedRoutineCompletion ??= new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _seatedRoutineCompletion.Task;
    }

    private async Task WaitForGracefulSeatedExitAsync(CancellationToken cancellationToken)
    {
        Task completion = Task.CompletedTask;
        await RunOnUiThreadAsync(() => completion = RequestGracefulSeatedExit());
        await completion.WaitAsync(cancellationToken);
    }

    private void CancelSeatedRoutine(bool restoreWindowScale = true)
    {
        var wasActive = _isSeatedRoutineActive;
        var wasPreview = _isSeatedPreviewActive;
        var cts = Interlocked.Exchange(ref _seatedRoutineCts, null);
        _isSeatedRoutineActive = false;
        _isSeatedPreviewActive = false;
        _seatedExitRequested = false;
        var completion = _seatedAnimationCompletion;
        _seatedAnimationCompletion = null;
        var idleLoopCompletion = _seatedIdleLoopCompletion;
        _seatedIdleLoopCompletion = null;
        _seatedIdleLoopEndsAtMilliseconds = long.MaxValue;
        var routineCompletion = _seatedRoutineCompletion;
        _seatedRoutineCompletion = null;
        CancelWithoutThrow(cts);
        completion?.TrySetCanceled();
        idleLoopCompletion?.TrySetCanceled();
        if (restoreWindowScale)
        {
            RestoreWindowSizeAfterSeatedRoutine();
        }
        routineCompletion?.TrySetResult(true);
        if (wasActive && !_isClosing)
        {
            var now = Environment.TickCount64;
            if (wasPreview)
            {
                EnsureSeatedRoutineScheduled(now);
            }
            else
            {
                ScheduleNextSeatedRoutine(now);
            }
        }
    }

    private static double RandomBetween(double minimum, double maximum) =>
        minimum + (Random.Shared.NextDouble() * (maximum - minimum));

    private void TogglePanel()
    {
        if (_isClosing)
        {
            return;
        }

        EnsurePanel();
        if (_panel!.IsVisible)
        {
            _panel.Hide();
        }
        else
        {
            _panel.ShowNearPet();
        }
    }

    private void EnsurePanel()
    {
        if (_panel is not null)
        {
            return;
        }

        _panel = new PanelWindow(
            this,
            _codex,
            _progress,
            _desktopBridge,
            _desktopMonitor,
            _settingsStore,
            _settings);
        _panel.Opacity = _settings.UiOpacity;
        _panel.IsVisibleChanged += Panel_IsVisibleChanged;
    }

    private ChatOrbWindow EnsureChatOrb()
    {
        if (_chatOrb is not null)
        {
            return _chatOrb;
        }

        _chatOrb = new ChatOrbWindow(this)
        {
            Opacity = _settings.UiOpacity
        };
        _chatOrb.ChatRequested += ChatOrb_ChatRequested;
        return _chatOrb;
    }

    private void ChatOrb_ChatRequested(object? sender, EventArgs e) => TogglePanel();

    private void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        SyncChatOrbVisibility();
    }

    private void SyncChatOrbVisibility()
    {
        if (_isClosing)
        {
            return;
        }

        if (_panel?.IsVisible == true)
        {
            _chatOrb?.HideOrb();
        }
        else
        {
            EnsureChatOrb().ShowNearPet();
        }
    }

    private ProgressBubbleWindow EnsureProgressBubble()
    {
        if (_progressBubble is null)
        {
            _progressBubble = new ProgressBubbleWindow();
            _progressBubble.Opacity = _settings.UiOpacity;
        }

        _progressBubble.PositionNear(this);
        return _progressBubble;
    }

    private void ShowReadyBubble(
        string message,
        string semanticKey,
        TimeSpan visibleDuration)
    {
        if (!ShouldRenderBubbleEvent(semanticKey))
        {
            return;
        }

        EnsureProgressBubble().ShowReady(message);
        ScheduleBubbleHide(semanticKey, visibleDuration);
    }

    private bool ShouldRenderBubbleEvent(string semanticKey)
    {
        var sameEventIsStillVisible = _bubbleHideCts is { IsCancellationRequested: false } &&
                                      string.Equals(
                                          _activeBubbleSemanticKey,
                                          semanticKey,
                                          StringComparison.Ordinal);
        return sameEventIsStillVisible ||
               !string.Equals(_lastDisplayedBubbleSemanticKey, semanticKey, StringComparison.Ordinal);
    }

    private void ScheduleBubbleHide(string semanticKey, TimeSpan visibleDuration)
    {
        if (_bubbleHideCts is { IsCancellationRequested: false } &&
            string.Equals(_activeBubbleSemanticKey, semanticKey, StringComparison.Ordinal))
        {
            return;
        }

        CancelBubbleHide();
        var cts = new CancellationTokenSource();
        _bubbleHideCts = cts;
        _activeBubbleSemanticKey = semanticKey;
        _lastDisplayedBubbleSemanticKey = semanticKey;
        _ = HideBubbleAfterDelayAsync(cts, visibleDuration);
    }

    private async Task HideBubbleAfterDelayAsync(
        CancellationTokenSource cts,
        TimeSpan visibleDuration)
    {
        try
        {
            await Task.Delay(visibleDuration, cts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_isClosing &&
                    !cts.IsCancellationRequested &&
                    ReferenceEquals(_bubbleHideCts, cts))
                {
                    _progressBubble?.HideBubble();
                    _bubbleHideCts = null;
                    _activeBubbleSemanticKey = null;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_bubbleHideCts, cts))
            {
                _bubbleHideCts = null;
                _activeBubbleSemanticKey = null;
            }

            cts.Dispose();
        }
    }

    private void CancelBubbleHide()
    {
        _bubbleHideCts?.Cancel();
    }

    internal void ActivateFromLauncher()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ActivateFromLauncher);
            return;
        }

        if (_isClosing)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        var workArea = SystemParameters.WorkArea;
        SetHostPositionForRestingViewport(
            Math.Max(workArea.Left, workArea.Right - RestingWindowWidth - 28),
            Math.Max(workArea.Top, workArea.Bottom - RestingWindowHeight - 22));
        _settings.WindowLeft = RestingViewportLeft;
        _settings.WindowTop = RestingViewportTop;
        _ = SaveActivatedPositionAsync();
        _panel?.FollowPet();
        _progressBubble?.PositionNear(this);
        RenderDesktopSnapshot(_desktopSnapshot);
        SyncChatOrbVisibility();

        Topmost = false;
        Topmost = true;
        Activate();
        _ = PlayTemporaryMoodAsync(PetMood.Waving, TimeSpan.FromSeconds(1.3));

    }

    private async Task SaveActivatedPositionAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activated position save failed: {ex}");
        }
    }

    private void DesktopMonitor_SnapshotChanged(object? sender, CodexDesktopMonitorSnapshot snapshot)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            _desktopSnapshot = snapshot;
            RenderDesktopSnapshot(snapshot);
        });
    }

    private void RenderDesktopSnapshot(CodexDesktopMonitorSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RenderDesktopSnapshot(snapshot));
            return;
        }

        if (_isClosing)
        {
            return;
        }

        ApplyDesktopAggregateState(snapshot);
        ObserveWorkloadDesktopCompletions(snapshot);
        ObserveTaskContextFeedback(snapshot);

        if (!snapshot.IsConnected)
        {
            return;
        }

        if (!_desktopSnapshotInitialized)
        {
            RememberDesktopTasks(snapshot.Tasks);
            _desktopSnapshotInitialized = true;
            return;
        }

        var changes = new List<(CodexDesktopTask Task, CodexDesktopTask? Previous, int Priority)>();
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawTask in snapshot.Tasks)
        {
            currentIds.Add(rawTask.Id);
            _desktopTasksById.TryGetValue(rawTask.Id, out var previous);
            var task = StabilizeDesktopTask(rawTask, previous);
            var fingerprint = CreateDesktopTaskFingerprint(task);
            var isNewNotableTask = previous is null &&
                                    (IsDesktopTaskActive(task) ||
                                     IsDesktopTaskWaiting(task) ||
                                     IsDesktopTaskError(task));
            var didChange = _desktopTaskFingerprints.TryGetValue(task.Id, out var oldFingerprint) &&
                            !string.Equals(oldFingerprint, fingerprint, StringComparison.Ordinal);

            if (isNewNotableTask || didChange)
            {
                changes.Add((task, previous, DesktopChangePriority(task, previous)));
            }

            _desktopTaskFingerprints[task.Id] = fingerprint;
            _desktopTasksById[task.Id] = task;
        }

        foreach (var staleTaskId in _desktopTasksById.Keys
                     .Where(taskId => !currentIds.Contains(taskId))
                     .ToArray())
        {
            _desktopTasksById.Remove(staleTaskId);
            _desktopTaskFingerprints.Remove(staleTaskId);
        }

        var orderedChanges = changes
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.Task.UpdatedAt)
            .ToArray();
        foreach (var change in orderedChanges)
        {
            if (ShowDesktopTaskEvent(change.Task, change.Previous, changes.Count - 1))
            {
                break;
            }
        }
    }

    private void RememberDesktopTasks(IReadOnlyList<CodexDesktopTask> tasks)
    {
        _desktopTaskFingerprints.Clear();
        _desktopTasksById.Clear();
        foreach (var task in tasks)
        {
            _desktopTaskFingerprints[task.Id] = CreateDesktopTaskFingerprint(task);
            _desktopTasksById[task.Id] = task;
        }
    }

    private bool ShowDesktopTaskEvent(
        CodexDesktopTask task,
        CodexDesktopTask? previous,
        int additionalChangeCount)
    {
        if (CompanionThreadIdentity.IsCompanionWorkspace(task.Cwd) ||
            _desktopMonitor.IsTaskExcluded(task.Id) ||
            string.Equals(
                task.Id,
                _dialogueClient.ConversationThreadId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var completed = previous is not null &&
                        IsDesktopTaskActive(previous) &&
                        (NormalizeDesktopSignal(task.Status) == "idle" ||
                         !IsDesktopTaskActive(task) ||
                         HasDesktopSignal(task, "completed"));
        CodexTaskProgressState state;
        string statusText;
        string progressLabel;

        if (IsDesktopTaskWaiting(task))
        {
            state = CodexTaskProgressState.Waiting;
            statusText = task.DisplayStatus == "待输入" ? "任务等待你的输入" : "任务等待你的确认";
            progressLabel = task.DisplayStatus;
        }
        else if (IsDesktopTaskError(task))
        {
            state = CodexTaskProgressState.Failed;
            statusText = "任务出现异常";
            progressLabel = "异常";
        }
        else if (completed)
        {
            state = CodexTaskProgressState.Completed;
            statusText = "任务刚刚完成";
            progressLabel = "已完成";
        }
        else if (IsDesktopTaskActive(task))
        {
            state = CodexTaskProgressState.Running;
            statusText = previous is null || !IsDesktopTaskActive(previous)
                ? "任务开始运行"
                : "任务有新进展";
            progressLabel = "进行中";
        }
        else
        {
            state = CodexTaskProgressState.Completed;
            statusText = "任务状态已更新";
            progressLabel = task.DisplayStatus;
        }

        var currentStep = CompactTaskText(task.DisplayName, "Codex 任务", 48);
        var currentOperation = !string.IsNullOrWhiteSpace(task.CurrentOperation)
            ? CompactTaskText(task.CurrentOperation, statusText, 88)
            : CompactTaskText(task.Summary, statusText, 88);
        if (additionalChangeCount > 0)
        {
            currentOperation = $"{currentOperation}\n另 {additionalChangeCount} 项任务也有更新";
        }

        var progressSnapshot = new CodexTaskProgressSnapshot(
            task.Id,
            null,
            state,
            true,
            state is CodexTaskProgressState.Running or CodexTaskProgressState.Waiting,
            statusText,
            currentStep,
            currentOperation,
            null,
            progressLabel,
            [],
            []);

        var visibleDuration = state switch
        {
            CodexTaskProgressState.Waiting or CodexTaskProgressState.Failed => TimeSpan.FromSeconds(10),
            CodexTaskProgressState.Completed => TimeSpan.FromSeconds(8),
            _ => TimeSpan.FromSeconds(6)
        };
        var activityKey = string.IsNullOrWhiteSpace(task.CurrentActivityKey)
            ? NormalizeDesktopSignal(task.CurrentTurnStatus)
            : NormalizeDesktopText(task.CurrentActivityKey);
        var eventKind = IsDesktopTaskWaiting(task)
            ? HasDesktopSignal(task, "waitingOnApproval") ? "approval" : "input"
            : IsDesktopTaskError(task) ? "error"
            : completed ? "completed"
            : IsDesktopTaskActive(task) ? "active"
            : "updated";
        var semanticKey = $"desktop:{task.Id}:{eventKind}:{activityKey}";
        if (!CanRenderTaskProgress(state) || !ShouldRenderBubbleEvent(semanticKey))
        {
            return false;
        }

        EnsureProgressBubble().Render(progressSnapshot);
        ScheduleBubbleHide(semanticKey, visibleDuration);
        PetImage.ToolTip =
            $"{PetInteractionHint}\n\n{statusText}\n{currentStep}\n{currentOperation}";

        if (!string.Equals(task.Id, _codex.CurrentThreadId, StringComparison.Ordinal))
        {
            // Do not interpret unload, interrupted, or generic idle as success.
            if (!WorkloadReactionsReady && eventKind == "completed" && HasDesktopSignal(task, "completed"))
            {
                TryCelebrateTaskCompletion($"desktop:{task.Id}:{task.CurrentActivityKey}:{task.UpdatedAt}");
            }

            var dialogueIntent = eventKind switch
            {
                "approval" => PetDialogueIntent.ApprovalRequired,
                "error" => PetDialogueIntent.TaskFailed,
                "completed" => ResolveTaskCompletedIntent(task.Id),
                "active" when previous is null || !IsDesktopTaskActive(previous) => ResolveTaskStartedIntent(),
                _ => (PetDialogueIntent?)null
            };
            if (dialogueIntent is { } intent)
            {
                _ = DeliverCharacterLineAsync(intent);
            }
        }

        return true;
    }

    private void ApplyDesktopAggregateState(CodexDesktopMonitorSnapshot snapshot)
    {
        if (!snapshot.IsConnected)
        {
            _lastCuteAngryActiveTaskCount = -1;
            if (!string.IsNullOrWhiteSpace(snapshot.Error))
            {
                SetStatus("连接异常", Brushes.IndianRed);
                SetPersistentMood(PetMood.Failed);
            }
            else
            {
                SetStatus("同步任务", Brushes.Goldenrod);
                SetPersistentMood(PetMood.Idle);
            }

            return;
        }

        string badgeText;
        Brush statusBrush;
        PetMood mood;
        if (snapshot.WaitingCount > 0)
        {
            badgeText = "等待处理";
            statusBrush = Brushes.DarkOrange;
            mood = PetMood.Waiting;
        }
        else if (snapshot.ErrorCount > 0)
        {
            badgeText = "任务异常";
            statusBrush = Brushes.IndianRed;
            mood = PetMood.Failed;
        }
        else if (snapshot.ReadyCount > 0)
        {
            badgeText = "刚刚完成";
            statusBrush = new SolidColorBrush(Color.FromRgb(86, 190, 151));
            mood = PetMood.Review;
        }
        else if (snapshot.ActiveCount > 0)
        {
            badgeText = snapshot.ActiveCount > 1 ? $"{snapshot.ActiveCount}项进行中" : "任务进行中";
            statusBrush = new SolidColorBrush(Color.FromRgb(117, 101, 206));
            mood = PetMood.Working;
        }
        else
        {
            badgeText = "Codex 就绪";
            statusBrush = new SolidColorBrush(Color.FromRgb(86, 190, 151));
            mood = PetMood.Idle;
        }

        SetStatus(badgeText, statusBrush);
        SetPersistentMood(mood);
        if (!WorkloadReactionsReady)
            ConsiderMultiTaskCuteAngryReaction(snapshot.ActiveCount);
        var aggregate = new List<string>();
        if (snapshot.ActiveCount > 0) aggregate.Add($"进行中 {snapshot.ActiveCount}");
        if (snapshot.WaitingCount > 0) aggregate.Add($"待处理 {snapshot.WaitingCount}");
        if (snapshot.ErrorCount > 0) aggregate.Add($"异常 {snapshot.ErrorCount}");
        PetImage.ToolTip = aggregate.Count == 0
            ? PetInteractionHint
            : $"{PetInteractionHint}\n\n{string.Join(" · ", aggregate)}";
    }

    private void ConsiderMultiTaskCuteAngryReaction(int activeCount)
    {
        var previousActiveCount = _lastCuteAngryActiveTaskCount;
        _lastCuteAngryActiveTaskCount = activeCount;
        if (!PetCuteAngryTriggerPolicy.IsMultiTaskIncrease(
                previousActiveCount,
                activeCount))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < _multiTaskCuteAngryAvailableAtMilliseconds ||
            !CanStartCuteAngryReaction(
                allowClickFinalFrame: false,
                allowPointerPress: false) ||
            !PetCuteAngryTriggerPolicy.ShouldTrigger(
                PetCuteAngryTriggerSource.MultiTaskIncrease,
                Random.Shared.NextDouble()))
        {
            return;
        }

        var cooldown = PetCuteAngryTriggerPolicy.CalculateMultiTaskCooldown(
            Random.Shared.NextDouble());
        _multiTaskCuteAngryAvailableAtMilliseconds = now +
            (long)Math.Round(cooldown.TotalMilliseconds);
        // This is an ambient trigger, not a confirmed pointer interaction. It
        // must join the ordinary transition queue and wait for a real loop or
        // one-shot boundary before publishing CuteAngry F000.
        _ = PlayAutomaticCuteAngryReactionAsync();
    }

    private static CodexDesktopTask StabilizeDesktopTask(
        CodexDesktopTask task,
        CodexDesktopTask? previous)
    {
        if (previous is null)
        {
            return task;
        }

        var taskIsStillActive = NormalizeDesktopSignal(task.Status) == "active";
        return task with
        {
            Summary = string.IsNullOrWhiteSpace(task.Summary) ? previous.Summary : task.Summary,
            CurrentTurnStatus = taskIsStillActive && string.IsNullOrWhiteSpace(task.CurrentTurnStatus)
                ? previous.CurrentTurnStatus
                : task.CurrentTurnStatus,
            CurrentOperation = string.IsNullOrWhiteSpace(task.CurrentOperation)
                ? previous.CurrentOperation
                : task.CurrentOperation,
            CurrentActivityKey = taskIsStillActive && string.IsNullOrWhiteSpace(task.CurrentActivityKey)
                ? previous.CurrentActivityKey
                : task.CurrentActivityKey
        };
    }

    private static string CreateDesktopTaskFingerprint(CodexDesktopTask task)
    {
        var activityKey = NormalizeDesktopText(task.CurrentActivityKey);
        return string.Join('\u001f',
            NormalizeDesktopSignal(task.Status),
            string.Join('\u001e', task.ActiveFlags
                .Select(NormalizeDesktopSignal)
                .OrderBy(flag => flag, StringComparer.Ordinal)),
            NormalizeDesktopSignal(task.CurrentTurnStatus),
            activityKey,
            string.IsNullOrWhiteSpace(activityKey) ? NormalizeDesktopText(task.CurrentOperation) : string.Empty,
            string.IsNullOrWhiteSpace(activityKey) ? NormalizeDesktopText(task.Summary) : string.Empty);
    }

    private static int DesktopChangePriority(CodexDesktopTask task, CodexDesktopTask? previous)
    {
        if (HasDesktopSignal(task, "waitingOnApproval")) return 6;
        if (HasDesktopSignal(task, "waitingOnUserInput")) return 5;
        if (IsDesktopTaskError(task)) return 4;
        if (previous is not null && IsDesktopTaskActive(previous) &&
            (NormalizeDesktopSignal(task.Status) == "idle" ||
             !IsDesktopTaskActive(task) ||
             HasDesktopSignal(task, "completed"))) return 3;
        if (IsDesktopTaskActive(task)) return 2;
        return 1;
    }

    private static bool IsDesktopTaskWaiting(CodexDesktopTask task) =>
        HasDesktopSignal(task, "waitingOnApproval") || HasDesktopSignal(task, "waitingOnUserInput");

    private static bool IsDesktopTaskError(CodexDesktopTask task) =>
        HasDesktopSignal(task, "systemError") || HasDesktopSignal(task, "blocked") ||
        HasDesktopSignal(task, "failed") || HasDesktopSignal(task, "error");

    private static bool IsDesktopTaskActive(CodexDesktopTask task) =>
        HasDesktopSignal(task, "active") || HasDesktopSignal(task, "running") ||
        HasDesktopSignal(task, "inProgress") || HasDesktopSignal(task, "started");

    private static bool HasDesktopSignal(CodexDesktopTask task, string signal)
    {
        var expected = NormalizeDesktopSignal(signal);
        return NormalizeDesktopSignal(task.Status) == expected ||
               NormalizeDesktopSignal(task.CurrentTurnStatus) == expected ||
               task.ActiveFlags.Any(flag => NormalizeDesktopSignal(flag) == expected);
    }

    private static string NormalizeDesktopSignal(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string NormalizeDesktopText(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(' ', value.Split(
            ['\r', '\n', '\t', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string CompactTaskText(string? text, string fallback, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var compact = string.Join(' ', text
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maximumLength
            ? compact
            : $"{compact[..Math.Max(1, maximumLength - 1)]}…";
    }

    private void Codex_ConnectionStateChanged(object? sender, CodexConnectionState state)
    {
        Dispatcher.Invoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            if (_desktopSnapshot.IsConnected)
            {
                ApplyDesktopAggregateState(_desktopSnapshot);
                return;
            }

            switch (state)
            {
                case CodexConnectionState.Connecting:
                    SetStatus("连接 Codex", Brushes.Goldenrod);
                    SetPersistentMood(PetMood.Idle);
                    break;
                case CodexConnectionState.Connected:
                    SetStatus("同步任务", Brushes.Goldenrod);
                    SetPersistentMood(PetMood.Idle);
                    break;
                case CodexConnectionState.Error:
                    SetStatus("连接异常", Brushes.IndianRed);
                    SetPersistentMood(PetMood.Failed);
                    break;
                default:
                    SetStatus("Codex 离线", Brushes.Gray);
                    SetPersistentMood(PetMood.Idle);
                    break;
            }
        });
    }

    private void Codex_ProgressChanged(object? sender, CodexTaskProgressSnapshot snapshot)
    {
        Dispatcher.Invoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            if (!snapshot.IsVisible)
            {
                _localProgressFingerprint = null;
                return;
            }

            var fingerprint = CreateLocalProgressFingerprint(snapshot);
            if (string.Equals(_localProgressFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _localProgressFingerprint = fingerprint;
            var semanticKey = CreateLocalProgressSemanticKey(snapshot);
            if (CanRenderTaskProgress(snapshot.State) && ShouldRenderBubbleEvent(semanticKey))
            {
                var progressBubble = EnsureProgressBubble();
                progressBubble.Render(snapshot);
                ScheduleBubbleHide(semanticKey, LocalProgressVisibleDuration(snapshot.State));
            }

            PetImage.ToolTip = $"{PetInteractionHint}\n\n{snapshot.StatusText}\n{snapshot.CurrentStep}\n{snapshot.CurrentOperation}";

            switch (snapshot.State)
            {
                case CodexTaskProgressState.Running:
                    SetStatus(CompactStatus(snapshot.StatusText), new SolidColorBrush(Color.FromRgb(117, 101, 206)));
                    SetPersistentMood(PetMood.Working);
                    break;
                case CodexTaskProgressState.Waiting:
                    SetStatus(snapshot.StatusText == "等待你的输入" ? "等待输入" : "等待确认", Brushes.DarkOrange);
                    SetPersistentMood(PetMood.Waiting);
                    break;
                case CodexTaskProgressState.Completed:
                    SetStatus("任务完成", new SolidColorBrush(Color.FromRgb(86, 190, 151)));
                    SetPersistentMood(PetMood.Review);
                    break;
                case CodexTaskProgressState.Failed:
                    SetStatus("任务失败", Brushes.IndianRed);
                    SetPersistentMood(PetMood.Failed);
                    break;
                case CodexTaskProgressState.Interrupted:
                    SetStatus("已停止", Brushes.Gray);
                    SetPersistentMood(PetMood.Idle);
                    break;
            }
        });
    }

    private static string CreateLocalProgressFingerprint(CodexTaskProgressSnapshot snapshot)
    {
        var plan = string.Join('\u001e', snapshot.PlanSteps.Select(step =>
            $"{NormalizeDesktopText(step.Step)}\u001d{NormalizeDesktopSignal(step.Status)}"));
        var activities = string.Join('\u001e', snapshot.RecentActivities.Select(activity =>
            $"{activity.Id}\u001d{NormalizeDesktopSignal(activity.Status)}"));
        return string.Join('\u001f',
            snapshot.ThreadId ?? string.Empty,
            snapshot.TurnId ?? string.Empty,
            snapshot.State.ToString(),
            snapshot.Percent?.ToString() ?? string.Empty,
            plan,
            activities);
    }

    private static string CreateLocalProgressSemanticKey(
        string? threadId,
        string? turnId,
        CodexTaskProgressState state,
        string? activityKey = null) =>
        $"local:{threadId ?? "-"}:{turnId ?? "-"}:{state}:{activityKey ?? "-"}";

    private static string CreateLocalProgressSemanticKey(CodexTaskProgressSnapshot snapshot)
    {
        var activityKey = snapshot.RecentActivities
            .FirstOrDefault(activity => activity.Status is "进行中" or "等待确认")?.Id ??
            snapshot.RecentActivities.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(activityKey) && snapshot.State == CodexTaskProgressState.Waiting)
        {
            activityKey = NormalizeDesktopSignal(snapshot.StatusText);
        }

        return CreateLocalProgressSemanticKey(
            snapshot.ThreadId,
            snapshot.TurnId,
            snapshot.State,
            activityKey);
    }

    private static TimeSpan LocalProgressVisibleDuration(CodexTaskProgressState state) => state switch
    {
        CodexTaskProgressState.Waiting or CodexTaskProgressState.Failed => TimeSpan.FromSeconds(10),
        CodexTaskProgressState.Completed or CodexTaskProgressState.Interrupted => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(6)
    };

    private static string CompactStatus(string status) => status switch
    {
        "正在执行命令" => "执行命令",
        "正在处理文件" => "处理文件",
        "正在调用工具" => "调用工具",
        "正在分析" => "分析中",
        "正在搜索" => "搜索中",
        "正在制定计划" => "制定计划",
        _ when status.StartsWith("已完成 ", StringComparison.Ordinal) => "任务进行中",
        _ => status.Length <= 6 ? status : "任务进行中"
    };

    private void Codex_ApprovalRequested(object? sender, CodexApprovalRequest request)
    {
        if (_isClosing)
        {
            return;
        }

        CancelAutomaticDialogue();
        BeginImportantVoice();

        Dispatcher.Invoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            var snapshot = _progress.Snapshot;
            if (snapshot.IsVisible)
            {
                var semanticKey = CreateLocalProgressSemanticKey(snapshot);
                if (ShouldRenderBubbleEvent(semanticKey))
                {
                    EnsureProgressBubble().Render(snapshot);
                    ScheduleBubbleHide(semanticKey, TimeSpan.FromSeconds(10));
                }
            }
            else
            {
                ShowReadyBubble(
                    "有一项操作需要你确认",
                    CreateLocalProgressSemanticKey(request.ThreadId, request.TurnId, CodexTaskProgressState.Waiting),
                    TimeSpan.FromSeconds(10));
            }

            SetStatus("需要你确认", Brushes.DarkOrange);
            SetPersistentMood(PetMood.Waiting);
            EnsurePanel();
            _panel!.ShowApproval(request);
            _panel.ShowNearPet();
            _ = DeliverCharacterLineAsync(PetDialogueIntent.ApprovalRequired);
        });
    }

    private void Codex_TurnCompleted(object? sender, CodexTurnCompleted completed)
    {
        if (_isClosing)
        {
            return;
        }

        CancelAutomaticDialogue();
        BeginImportantVoice();
        var intent = completed.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            ? ResolveTaskCompletedIntent(completed.ThreadId)
            : completed.Status.Equals("interrupted", StringComparison.OrdinalIgnoreCase)
                ? PetDialogueIntent.TaskInterrupted
                : PetDialogueIntent.TaskFailed;

        Dispatcher.Invoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            if (completed.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("任务完成", new SolidColorBrush(Color.FromRgb(86, 190, 151)));
                SetPersistentMood(PetMood.Review);
                TryCelebrateTaskCompletion($"local:{completed.ThreadId}:{completed.TurnId}");
                if (!_progress.Snapshot.IsVisible)
                {
                    ShowReadyBubble(
                        "任务完成啦",
                        CreateLocalProgressSemanticKey(
                            completed.ThreadId,
                            completed.TurnId,
                            CodexTaskProgressState.Completed),
                        TimeSpan.FromSeconds(8));
                }
            }
            else if (completed.Status.Equals("interrupted", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("已停止", Brushes.Gray);
                SetPersistentMood(PetMood.Idle);
                if (!_progress.Snapshot.IsVisible)
                {
                    ShowReadyBubble(
                        "任务已经停止",
                        CreateLocalProgressSemanticKey(
                            completed.ThreadId,
                            completed.TurnId,
                            CodexTaskProgressState.Interrupted),
                        TimeSpan.FromSeconds(8));
                }
            }
            else
            {
                SetStatus("任务失败", Brushes.IndianRed);
                SetPersistentMood(PetMood.Failed);
                if (!_progress.Snapshot.IsVisible)
                {
                    ShowReadyBubble(
                        "任务遇到问题了",
                        CreateLocalProgressSemanticKey(
                            completed.ThreadId,
                            completed.TurnId,
                            CodexTaskProgressState.Failed),
                        TimeSpan.FromSeconds(10));
                }
            }

            _ = DeliverCharacterLineAsync(intent);
        });
    }

    private void SetStatus(string text, Brush dotBrush)
    {
        StatusText.Text = text;
        StatusDot.Fill = dotBrush;
        var connectionError = _desktopSnapshot.Error ?? _desktopBridge.LastError ?? _codex.LastError;
        StatusBadge.ToolTip = connectionError;
        if (!string.IsNullOrWhiteSpace(connectionError))
        {
            PetImage.ToolTip = $"{PetInteractionHint}\n\n{text}：{connectionError}";
        }
    }

    private void OpenPanelMenuItem_Click(object sender, RoutedEventArgs e) => TogglePanel();

    private void WaveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _ = PlayTemporaryMoodAsync(PetMood.Waving, TimeSpan.FromSeconds(1.3));
        _ = DeliverCharacterLineAsync(PetDialogueIntent.Greeting);
    }

    private async void DynamicDialogueMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var previous = _settings.DynamicDialogueEnabled;
        _settings.DynamicDialogueEnabled = DynamicDialogueMenuItem.IsChecked;
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _settings.DynamicDialogueEnabled = previous;
            DynamicDialogueMenuItem.IsChecked = previous;
            SetStatus("台词设置保存失败", Brushes.IndianRed);
            System.Diagnostics.Debug.WriteLine($"Dynamic dialogue setting save failed: {ex}");
        }
    }

    private async void ShareMemoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var previous = _settings.ShareMemoryWithDialogue;
        var previousConsent = _settings.MemorySharingConsentRecorded;
        var previousAutomatic = _settings.AutomaticMemoryEnabled;
        _settings.ShareMemoryWithDialogue = ShareMemoryMenuItem.IsChecked;
        _settings.AutomaticMemoryEnabled = ShareMemoryMenuItem.IsChecked;
        _settings.MemorySharingConsentRecorded = true;
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _settings.ShareMemoryWithDialogue = previous;
            _settings.MemorySharingConsentRecorded = previousConsent;
            _settings.AutomaticMemoryEnabled = previousAutomatic;
            ShareMemoryMenuItem.IsChecked = previous;
            SetStatus("记忆设置保存失败", Brushes.IndianRed);
            System.Diagnostics.Debug.WriteLine($"Memory sharing setting save failed: {ex}");
            return;
        }
    }

    private void ViewMemoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _memoryStore.Snapshot;
        var lines = snapshot.Entries.Count == 0
            ? "柯朵还没有保存任何记忆。"
            : string.Join(
                Environment.NewLine,
                snapshot.Entries
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .Select((entry, index) => $"{index + 1}. {entry.Content}"));
        var notice = string.IsNullOrWhiteSpace(snapshot.Notice)
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}{snapshot.Notice}";

        MessageBox.Show(
            $"{lines}{notice}{Environment.NewLine}{Environment.NewLine}" +
            "柯朵的常驻房间负责主要上下文记忆；这里是本地重点索引，来自主人明确说“记住”的内容，或 Luna 从直接聊天中提出且经程序核对原话后保存的偏好、习惯与重要事件。\n" +
            "密码、Token、API Key 和私钥不会保存；语音删除指令不会直接执行。\n" +
            "角色设定示例：柯朵，设定：说话再害羞一点。",
            "柯朵的本地记忆",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ClearMemoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var decision = MessageBox.Show(
            "要清空柯朵当前使用的全部长期记忆吗？\n\n" +
            "程序会退役当前房间并清空本地重点索引；为方便主人查看，已加密的本地聊天备份仍会保留。",
            "确认清空记忆",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (decision != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _dialogue.ResetSessionAsync(_dialogueLifetimeCts.Token);
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Luna session reset before memory clear failed", ex);
            MessageBox.Show(
                "旧对话房间暂时无法安全退役，所以本次没有清空记忆。请稍后重试。",
                "清空失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var result = await _memoryStore.ClearAsync();
        RefreshMemoryMenu();
        if (result.Status is PetMemoryMutationStatus.Cleared or PetMemoryMutationStatus.AlreadyEmpty)
        {
            _ = DeliverCharacterLineAsync(PetDialogueIntent.MemoryCleared);
        }
        else
        {
            MessageBox.Show(
                "记忆没有清空。请检查记忆文件是否处于只读或不可写状态。",
                "清空失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RefreshMemoryMenu()
    {
        var snapshot = _memoryStore.Snapshot;
        MemoryMenuItem.Header = snapshot.IsReadOnly
            ? $"柯朵记忆 · {snapshot.Count} 条（只读）"
            : $"柯朵记忆 · {snapshot.Count} 条";
        MemoryMenuItem.ToolTip = snapshot.Notice ??
            "柯朵的常驻房间负责主要上下文记忆；本机只保存重点索引，并在启动或切换房间后恢复一次，不再随每句话重复发送。";
    }

    private void AppearanceSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var settingsWindow = new SettingsWindow(
            _settings.UiOpacity,
            _settings.PetSizeScale,
            ApplyUiOpacity,
            scale => ApplyPetSize(scale, preserveCenter: true),
            ShowBubblePreview,
            SaveAppearanceAsync,
            GetAvailableAnimationPreviews(),
            PreviewAnimationAsync)
        {
            Owner = this
        };
        settingsWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, settingsWindow))
            {
                _settingsWindow = null;
            }
        };

        _settingsWindow = settingsWindow;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    private static IReadOnlyList<PetAnimationPreviewOption> GetAvailableAnimationPreviews()
    {
        var animationsRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "animations");
        return PetAnimations.AnimationPreviewOptions
            .Where(option =>
            {
                if (!PetAnimations.ExternalFrameDirectories.TryGetValue(option.Mood, out var directory))
                {
                    return false;
                }

                var sequenceDirectory = Path.Combine(animationsRoot, directory);
                try
                {
                    return Directory.Exists(sequenceDirectory) &&
                           Directory.EnumerateFiles(
                               sequenceDirectory,
                               "*.png",
                               SearchOption.TopDirectoryOnly).Any();
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            })
            .ToArray();
    }

    private void ShowBubblePreview()
    {
        CancelBubbleHide();
        var semanticKey = $"bubble-preview:{Interlocked.Increment(ref _characterBubbleSequence)}";
        EnsureProgressBubble().ShowPetSpeech("这是新的透明圆形气泡，滑杆可以继续调整不透明度。");
        ScheduleBubbleHide(semanticKey, TimeSpan.FromSeconds(10));
    }

    private void ApplyUiOpacity(double opacity)
    {
        var value = double.IsFinite(opacity) ? Math.Clamp(opacity, 0.60, 1.00) : 1.00;
        if (_panel is not null)
        {
            _panel.Opacity = value;
        }

        if (_progressBubble is not null)
        {
            _progressBubble.Opacity = value;
        }

        if (_chatOrb is not null)
        {
            _chatOrb.Opacity = value;
        }

        StatusBadge.Opacity = value;
        if (ContextMenu is not null)
        {
            ContextMenu.Opacity = value;
        }
    }

    private async Task SaveUiOpacityAsync(double opacity)
    {
        var value = double.IsFinite(opacity) ? Math.Clamp(opacity, 0.60, 1.00) : 1.00;
        var previousValue = _settings.UiOpacity;
        _settings.UiOpacity = value;
        ApplyUiOpacity(value);

        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _settings.UiOpacity = previousValue;
            ApplyUiOpacity(previousValue);
            SetStatus("透明度保存失败", Brushes.IndianRed);
            System.Diagnostics.Debug.WriteLine($"UI opacity save failed: {ex}");
            throw;
        }
    }

    private async Task SaveAppearanceAsync(double opacity, double petSizeScale)
    {
        var previousOpacity = _settings.UiOpacity;
        var previousPetSizeScale = _settings.PetSizeScale;
        _settings.UiOpacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0.60, 1.00) : 1.00;
        _settings.PetSizeScale = double.IsFinite(petSizeScale)
            ? Math.Clamp(
                petSizeScale,
                PetSettings.MinimumPetSizeScale,
                PetSettings.MaximumPetSizeScale)
            : PetSettings.DefaultPetSizeScale;
        ApplyUiOpacity(_settings.UiOpacity);

        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch
        {
            _settings.UiOpacity = previousOpacity;
            _settings.PetSizeScale = previousPetSizeScale;
            ApplyUiOpacity(previousOpacity);
            ApplyPetSize(previousPetSizeScale, preserveCenter: true);
            throw;
        }
    }

    private async void TtsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await UpdateTtsEnabledAsync(TtsMenuItem.IsChecked, announce: false);
    }

    private void InitializeTtsVoiceMenu()
    {
        var menuItemStyle = (Style)FindResource("MoonMenuItemStyle");
        foreach (var voice in TtsVoiceCatalog.All)
        {
            var menuItem = new MenuItem
            {
                Header = voice.DisplayName,
                IsCheckable = true,
                Style = menuItemStyle,
                Tag = voice.Id
            };
            menuItem.Click += TtsVoiceMenuItem_Click;
            TtsVoiceMenuItem.Items.Add(menuItem);
        }

        RefreshTtsVoiceMenu();
    }

    private void RefreshTtsVoiceMenu()
    {
        var selectedVoice = TtsVoiceCatalog.GetOrDefault(_settings.TtsVoice);
        TtsVoiceMenuItem.Header = $"方舟音色 · {selectedVoice.DisplayName}";

        foreach (var menuItem in TtsVoiceMenuItem.Items.OfType<MenuItem>())
        {
            menuItem.IsChecked = string.Equals(
                menuItem.Tag as string,
                selectedVoice.Id,
                StringComparison.Ordinal);
        }
    }

    private async void TtsVoiceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string voiceId })
        {
            return;
        }

        var selectedVoice = TtsVoiceCatalog.GetOrDefault(voiceId);
        if (string.Equals(_settings.TtsVoice, selectedVoice.Id, StringComparison.Ordinal))
        {
            return;
        }

        _settings.TtsVoice = selectedVoice.Id;
        _speech.Stop();
        _speech.ConfigureVoice(
            selectedVoice.Id,
            selectedVoice.Instruction,
            selectedVoice.SpeechRate,
            selectedVoice.Pitch);
        RefreshTtsVoiceMenu();

        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            SetStatus("音色保存失败", Brushes.IndianRed);
            System.Diagnostics.Debug.WriteLine($"TTS voice save failed: {ex}");
        }

        if (_settings.TtsEnabled)
        {
            await DeliverCharacterLineAsync(PetDialogueIntent.VoicePreview);
        }
    }

    internal async Task UpdateTtsEnabledAsync(bool enabled, bool announce)
    {
        _settings.TtsEnabled = enabled;
        if (!enabled)
        {
            _speech.Stop();
        }

        TtsMenuItem.IsChecked = enabled;
        _panel?.SyncTtsEnabled(enabled);

        try
        {
            await _settingsStore.SaveAsync(_settings);
            if (enabled && announce)
            {
                await DeliverCharacterLineAsync(PetDialogueIntent.TtsEnabled);
            }
        }
        catch (Exception ex)
        {
            SetStatus("设置保存失败", Brushes.IndianRed);
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    internal async Task<PetInteractionResult?> TryHandlePetInputAsync(
        string input,
        CancellationToken cancellationToken = default,
        IProgress<VoiceConversationProgress>? progress = null,
        bool awaitSpeech = false,
        PetInputSource inputSource = PetInputSource.Text)
    {
        using var taskFeedbackPause = PauseTaskContextForConversation();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _dialogueLifetimeCts.Token,
            cancellationToken);
        CancelAutomaticDialogue();
        BeginImportantVoice();
        var result = await _dialogue.TryHandleInputAsync(
            input,
            _settings.DynamicDialogueEnabled,
            _settings.ShareMemoryWithDialogue,
            _settings.AutomaticMemoryEnabled,
            inputSource,
            linkedCts.Token);
        if (result is null || _isClosing)
        {
            return result;
        }

        progress?.Report(new VoiceConversationProgress(
            VoiceConversationStage.Speaking,
            "柯朵在回答…"));
        linkedCts.Token.ThrowIfCancellationRequested();
        await PresentCharacterLineAsync(
            result.Reply,
            isImportant: true,
            cancellationToken: linkedCts.Token,
            awaitSpeech: awaitSpeech,
            playMessageFeedback: true,
            messageFeedbackProbability:
                PetMessageFeedbackPolicy.GeneralSpeechProbability);
        await Dispatcher.InvokeAsync(RefreshMemoryMenu);
        return result;
    }

    internal Task<IReadOnlyList<ConversationArchiveEntry>> ReadRecentCharacterConversationAsync(
        int count,
        CancellationToken cancellationToken = default) =>
        _dialogue.ReadRecentConversationAsync(count, cancellationToken);

    internal Task<PetInteractionResult?> SendCompanionChatAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var content = input.Trim();
        if (content.Length == 0)
        {
            return Task.FromResult<PetInteractionResult?>(null);
        }

        var routedInput = PetCommandRouter.RouteToPetConversation(content);
        return TryHandlePetInputAsync(
            routedInput,
            cancellationToken,
            inputSource: PetInputSource.Text);
    }

    internal async Task<VoiceConversationResult> RunVoiceConversationTurnAsync(
        IProgress<VoiceConversationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _voiceConversationGate.WaitAsync(cancellationToken);
        string? audioPath = null;
        Task? fallbackWarmUpTask = null;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _dialogueLifetimeCts.Token,
                cancellationToken);
            var token = linkedCts.Token;

            var agentPlanApiKey = _settings.PreferAgentPlanAsr
                ? WindowsCredentialStore.GetArkAgentPlanApiKey()
                : null;
            var useAgentPlanAsr = !string.IsNullOrWhiteSpace(agentPlanApiKey);
            progress?.Report(new VoiceConversationProgress(
                VoiceConversationStage.Preparing,
                useAgentPlanAsr ? "正在连接豆包语音识别…" : "正在唤醒本地 Whisper…"));
            CancelAutomaticDialogue();
            CancelInteractionDialogue();
            _speech.Stop();
            if (!useAgentPlanAsr)
            {
                await _transcription.WarmUpAsync(token);
            }
            else
            {
                // Hide most of the local-model cold start behind listening and the
                // cloud request, so a cloud failure can fall back without a long pause.
                fallbackWarmUpTask = _transcription.WarmUpAsync(token);
                _ = fallbackWarmUpTask.ContinueWith(
                    task => System.Diagnostics.Debug.WriteLine(
                        $"Background Whisper warm-up failed: {task.Exception?.GetBaseException().GetType().Name}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            await Task.Delay(140, token);

            progress?.Report(new VoiceConversationProgress(
                VoiceConversationStage.Listening,
                "在听啦，说完后停一下就好…"));
            var capture = await _microphone.CaptureUtteranceAsync(token);
            audioPath = capture.AudioPath;

            progress?.Report(new VoiceConversationProgress(
                VoiceConversationStage.Transcribing,
                useAgentPlanAsr
                    ? "豆包正在听懂这句话…"
                    : "Whisper 正在听懂这句话…"));
            string transcript;
            string recognitionBackend;
            try
            {
                if (useAgentPlanAsr)
                {
                    try
                    {
                        transcript = await _agentPlanAsr.TranscribeAsync(
                            agentPlanApiKey!,
                            audioPath,
                            token);
                        recognitionBackend = _agentPlanAsr.BackendDescription;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        App.WriteErrorLog("Agent Plan ASR fallback to local Whisper", exception);
                        progress?.Report(new VoiceConversationProgress(
                            VoiceConversationStage.Transcribing,
                            "云端没听稳，正在用本机 Whisper 再听一次…"));
                        await (fallbackWarmUpTask ?? _transcription.WarmUpAsync(token));
                        transcript = await _transcription.TranscribeAsync(audioPath, token);
                        recognitionBackend = $"{_transcription.BackendDescription} · 云端回退";
                    }
                }
                else
                {
                    transcript = await _transcription.TranscribeAsync(audioPath, token);
                    recognitionBackend = _transcription.BackendDescription;
                }
            }
            finally
            {
                if (await MicrophoneCaptureService.TryDeleteWithRetryAsync(audioPath))
                {
                    audioPath = null;
                }
            }

            progress?.Report(new VoiceConversationProgress(
                VoiceConversationStage.Thinking,
                "Luna 正在想怎么回答…"));
            var interaction = await TryHandlePetInputAsync(
                $"柯朵，{transcript}",
                token,
                progress,
                awaitSpeech: true,
                inputSource: PetInputSource.Voice) ??
                throw new InvalidOperationException("这句话没有进入柯朵对话。");

            return new VoiceConversationResult(
                transcript,
                interaction,
                recognitionBackend);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                if (!await MicrophoneCaptureService.TryDeleteWithRetryAsync(audioPath))
                {
                    App.WriteErrorLog(
                        "Voice recording cleanup failed",
                        new IOException("临时录音暂时无法删除；下次启动会再次清理。"));
                }
            }

            _voiceConversationGate.Release();
        }
    }

    internal void CancelVoiceConversation()
    {
        _microphone.Cancel();
        _speech.Stop();
    }

    internal void NotifyTaskSubmitted() =>
        _ = DeliverCharacterLineAsync(ResolveTaskStartedIntent());

    private PetDialogueIntent ResolveTaskStartedIntent() =>
        PetWorkloadObservationPolicy.StartIntent(CountActiveUserTasks());

    private PetDialogueIntent ResolveTaskCompletedIntent(string? completedThreadId) =>
        CountActiveUserTasks(completedThreadId) >= 2
            ? PetDialogueIntent.TaskCompletedStillBusy
            : PetDialogueIntent.TaskCompleted;

    private int CountActiveUserTasks(string? excludedThreadId = null)
    {
        var activeTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in _desktopSnapshot.Tasks)
        {
            if (!IsDesktopTaskActive(task) ||
                string.Equals(task.Id, excludedThreadId, StringComparison.Ordinal) ||
                CompanionThreadIdentity.IsCompanionWorkspace(task.Cwd) ||
                _desktopMonitor.IsTaskExcluded(task.Id) ||
                string.Equals(
                    task.Id,
                    _dialogueClient.ConversationThreadId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            activeTaskIds.Add(task.Id);
        }

        var localProgress = _progress.Snapshot;
        if (localProgress.IsActive &&
            !string.Equals(localProgress.ThreadId, excludedThreadId, StringComparison.Ordinal))
        {
            activeTaskIds.Add(localProgress.ThreadId ?? "local-active-task");
        }

        return activeTaskIds.Count;
    }

    private async Task DeliverCharacterLineAsync(
        PetDialogueIntent intent,
        string? userText = null,
        IReadOnlyList<string>? memories = null)
    {
        if (_isClosing)
        {
            return;
        }

        // These lines are paired with the actual workload animation entry.
        // Polls and completion messages must not repeat speech over each loop.
        if (WorkloadReactionsReady && intent is PetDialogueIntent.TaskStarted or
            PetDialogueIntent.TaskModeratelyBusy or PetDialogueIntent.TaskOverloaded or
            PetDialogueIntent.TaskCompleted or PetDialogueIntent.TaskCompletedStillBusy)
            return;

        var cts = new CancellationTokenSource();
        lock (_dialogueRequestGate)
        {
            CancelWithoutThrow(_automaticDialogueCts);
            _automaticDialogueCts = cts;
        }

        BeginImportantVoice();

        try
        {
            var line = await _dialogue.CreateLineAsync(
                intent,
                userText,
                memories,
                _settings.DynamicDialogueEnabled,
                _settings.ShareMemoryWithDialogue,
                cts.Token);
            if (cts.IsCancellationRequested || _isClosing)
            {
                return;
            }

            lock (_dialogueRequestGate)
            {
                if (!ReferenceEquals(_automaticDialogueCts, cts))
                {
                    return;
                }
            }

            await PresentCharacterLineAsync(
                line,
                isImportant: true,
                // Every unsolicited task/status line is a new message from the
                // pet, not only a successful completion. Pair the speech bubble
                // with the approved visual response once the current action is
                // safely available.
                playMessageFeedback: true,
                messageFeedbackProbability:
                    PetMessageFeedbackPolicy.ProbabilityForIntent(intent),
                isStillCurrent: () =>
                {
                    lock (_dialogueRequestGate)
                    {
                        return ReferenceEquals(_automaticDialogueCts, cts);
                    }
                });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Character dialogue failed: {ex.GetType().Name}");
        }
        finally
        {
            lock (_dialogueRequestGate)
            {
                if (ReferenceEquals(_automaticDialogueCts, cts))
                {
                    _automaticDialogueCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task PresentCharacterLineAsync(
        string line,
        bool isImportant,
        TimeSpan? visibleDuration = null,
        Func<bool>? isStillCurrent = null,
        CancellationToken cancellationToken = default,
        bool awaitSpeech = false,
        bool playMessageFeedback = false,
        double messageFeedbackProbability =
            PetMessageFeedbackPolicy.GeneralSpeechProbability,
        string? speechPerformance = null,
        SpeechService.PreparedSpeech? preparedSpeech = null,
        bool protectTaskContextBubble = false)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var shown = await Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing ||
                cancellationToken.IsCancellationRequested ||
                isStillCurrent is not null && !isStillCurrent() ||
                !isImportant && IsInteractionVoiceBlocked(Environment.TickCount64))
            {
                return false;
            }

            var speechSequence = Interlocked.Increment(ref _characterBubbleSequence);
            var semanticKey = $"pet-speech:{speechSequence}";
            var displayDuration = visibleDuration ??
                TimeSpan.FromSeconds(isImportant ? 8 : 4);
            EnsureProgressBubble().ShowPetSpeech(line);
            _lastCharacterSpeechAt = Environment.TickCount64;
            _taskContextBubbleProtectedUntil = protectTaskContextBubble
                ? _lastCharacterSpeechAt + (long)displayDuration.TotalMilliseconds : 0;
            ScheduleBubbleHide(
                semanticKey,
                displayDuration);
            BeginMessageFeedbackOpportunity(
                speechSequence,
                displayDuration,
                playMessageFeedback,
                messageFeedbackProbability);
            return true;
        });
        if (shown)
        {
            if (cancellationToken.IsCancellationRequested ||
                isStillCurrent is not null && !isStillCurrent())
            {
                return;
            }

            Task speechTask;
            if (isImportant)
            {
                lock (_voicePriorityGate)
                {
                    if (_isClosing ||
                        cancellationToken.IsCancellationRequested ||
                        isStillCurrent is not null && !isStillCurrent())
                    {
                        return;
                    }

                    CancelWithoutThrow(_interactionDialogueCts);
                    ExtendImportantVoiceProtection(ImportantVoiceProtectionMilliseconds);
                    _speech.Stop();
                    speechTask = SpeakSafelyAsync(
                        line,
                        isImportant: true,
                        cancellationToken: cancellationToken,
                        speechPerformance: speechPerformance,
                        preparedSpeech: preparedSpeech);
                }
            }
            else
            {
                lock (_voicePriorityGate)
                {
                    if (cancellationToken.IsCancellationRequested ||
                        IsInteractionVoiceBlocked(Environment.TickCount64))
                    {
                        return;
                    }

                    speechTask = SpeakSafelyAsync(
                        line,
                        isImportant: false,
                        cancellationToken: cancellationToken,
                        speechPerformance: speechPerformance,
                        preparedSpeech: preparedSpeech);
                }
            }

            if (awaitSpeech)
            {
                await speechTask;
            }
            else
            {
                _ = speechTask;
            }
        }
    }

    private bool IsMessageFeedbackPlaying =>
        _isTemporaryMoodActive &&
        _currentMood == PetMood.MessageFeedback &&
        _temporaryMoodOwner == PetVisualOwner.Temporary;

    private void BeginMessageFeedbackOpportunity(
        long speechSequence,
        TimeSpan visibleDuration,
        bool requested,
        double probability)
    {
        // Every newly displayed line invalidates an older not-yet-started hand
        // gesture. A gesture whose first frame is already visible is a normal
        // one-shot and must finish unless a click or real drag forces it away.
        if (!IsMessageFeedbackPlaying)
        {
            var previous = _messageFeedbackCts;
            _messageFeedbackCts = null;
            CancelWithoutThrow(previous);
        }

        if (!requested || IsMessageFeedbackPlaying || _positiveReactionPending)
        {
            return;
        }

        var now = Environment.TickCount64;
        var forcedInteractionActive =
            _pointerGesture.IsPressed ||
            PetVisualTransitionPolicy.IsForcedInteraction(_visualOwner) ||
            IsClickFlickFallPendingOrActive ||
            IsCuteAngryPendingOrActive;
        if (!PetMessageFeedbackPolicy.CanOpenOpportunity(
                speechWasShown: true,
                seatedRoutineActive: _isSeatedRoutineActive,
                forcedInteractionActive,
                temporaryAnimationActive: _isTemporaryMoodActive,
                nowMilliseconds: now,
                availableAtMilliseconds:
                    _messageFeedbackAvailableAtMilliseconds) ||
            !PetMessageFeedbackPolicy.ShouldSelect(
                probability,
                Random.Shared.NextDouble()))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _messageFeedbackCts = cts;
        _ = PlayMessageFeedbackWhenAvailableAsync(
            cts,
            speechSequence,
            now,
            visibleDuration);
    }

    private async Task PlayMessageFeedbackWhenAvailableAsync(
        CancellationTokenSource cts,
        long speechSequence,
        long shownAtMilliseconds,
        TimeSpan visibleDuration)
    {
        var started = 0;
        try
        {
            var spriteAtlas = _spriteAtlas;
            if (spriteAtlas is null)
            {
                return;
            }

            // Decode and validate off the dispatcher. The same cached snapshot
            // is reused by PlayTemporaryMoodAsync after the availability gate.
            var prepared = await Task.Run(
                () => spriteAtlas.PrepareSequence(PetMood.MessageFeedback),
                cts.Token);
            var duration = PetAnimations.CalculatePreviewDuration(
                prepared.Frames.Count,
                prepared.FrameDuration);
            var latestStart =
                PetMessageFeedbackPolicy.CalculateLatestStartDeadline(
                    shownAtMilliseconds,
                    visibleDuration,
                    duration);
            if (Environment.TickCount64 > latestStart)
            {
                return;
            }

            using var playbackCts = new CancellationTokenSource();
            using var pendingCancellation = cts.Token.Register(() =>
            {
                if (Volatile.Read(ref started) == 0)
                {
                    playbackCts.Cancel();
                }
            });
            var activated = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bool CanActivate() =>
                ReferenceEquals(_messageFeedbackCts, cts) &&
                !cts.IsCancellationRequested &&
                !_isClosing &&
                speechSequence == _characterBubbleSequence &&
                PetMessageFeedbackPolicy.IsSpeechBubbleActive(
                    _activeBubbleSemanticKey,
                    _bubbleHideCts is { IsCancellationRequested: false },
                    speechSequence) &&
                Environment.TickCount64 <= latestStart &&
                !_isSeatedRoutineActive &&
                !_pointerGesture.IsPressed &&
                !_isTemporaryMoodActive &&
                !_positiveReactionPending &&
                !PetVisualTransitionPolicy.IsForcedInteraction(_visualOwner) &&
                !IsClickFlickFallPendingOrActive &&
                !IsCuteAngryPendingOrActive;
            void MarkActivated()
            {
                Interlocked.Exchange(ref started, 1);
                _messageFeedbackAvailableAtMilliseconds = checked(
                    Environment.TickCount64 +
                    (long)PetMessageFeedbackPolicy.Cooldown.TotalMilliseconds);
                activated.TrySetResult(true);
            }

            var playback = PlayTemporaryMoodAsync(
                PetMood.MessageFeedback,
                duration,
                playbackCts.Token,
                completeAtAnimationBoundary: true,
                onActivated: MarkActivated,
                canActivate: CanActivate,
                abandonIfSeated: true);
            var remaining = Math.Max(
                0,
                latestStart - Environment.TickCount64);
            var deadline = Task.Delay(TimeSpan.FromMilliseconds(remaining));
            var first = await Task.WhenAny(playback, activated.Task, deadline);
            if (ReferenceEquals(first, deadline) &&
                Volatile.Read(ref started) == 0)
            {
                playbackCts.Cancel();
            }

            await playback;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Message feedback animation failure", ex);
        }
        finally
        {
            if (ReferenceEquals(_messageFeedbackCts, cts))
            {
                _messageFeedbackCts = null;
            }

            cts.Dispose();
        }
    }

    private void DeliverInstantActionReaction(PetDialogueIntent intent)
    {
        var line = PetActionDialogueContext.ChooseInstantReaction(intent);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _ = PresentCharacterLineAsync(
            line,
            isImportant: false,
            visibleDuration: TimeSpan.FromSeconds(2.5),
            speechPerformance:
                PetGrabDialogueContext.SpeechPerformance(intent) ??
                PetActionDialogueContext.SpeechPerformance(intent));
    }

    private void StartGrabReleaseLinePreparation(PetDialogueIntent intent)
    {
        CancelGrabReleaseLinePreparation();
        if (!_settings.DynamicDialogueEnabled)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _dialogueLifetimeCts.Token);
        _grabReleaseLinePreparationCts = cts;
        _preparedGrabReleaseIntent = intent;
        _preparedGrabReleaseLineTask = PrepareGrabReleaseLineAsync(intent, cts.Token);
    }

    private async Task<string?> PrepareGrabReleaseLineAsync(
        PetDialogueIntent intent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _dialogue.CreateLineAsync(
                intent,
                userText: null,
                memories: null,
                dynamicDialogueEnabled: true,
                shareMemoryWithDialogue: false,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Grab-release dialogue preparation failed: {ex.GetType().Name}");
            return null;
        }
    }

    private void DeliverPreparedGrabReleaseReaction(PetDialogueIntent intent)
    {
        var preparedLine = _preparedGrabReleaseIntent == intent &&
                           _preparedGrabReleaseLineTask is { IsCompletedSuccessfully: true }
            ? _preparedGrabReleaseLineTask.Result
            : null;
        CancelGrabReleaseLinePreparation();

        if (IsInteractionVoiceBlocked(Environment.TickCount64))
        {
            return;
        }

        // The release reaction supersedes the earlier pain cry. Stopping it
        // here lets the angry line begin with the angry animation instead of
        // waiting for the previous cloud audio to finish.
        _speech.Stop();
        if (string.IsNullOrWhiteSpace(preparedLine))
        {
            DeliverInstantActionReaction(intent);
            return;
        }

        _ = PresentCharacterLineAsync(
            preparedLine,
            isImportant: false,
            visibleDuration: TimeSpan.FromSeconds(3),
            speechPerformance: PetGrabDialogueContext.SpeechPerformance(intent));
    }

    private void CancelGrabReleaseLinePreparation()
    {
        var cts = _grabReleaseLinePreparationCts;
        var task = _preparedGrabReleaseLineTask;
        _grabReleaseLinePreparationCts = null;
        _preparedGrabReleaseLineTask = null;
        _preparedGrabReleaseIntent = null;
        CancelWithoutThrow(cts);
        if (cts is null)
        {
            return;
        }

        if (task is null || task.IsCompleted)
        {
            cts.Dispose();
            return;
        }

        _ = task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryDeliverInteractionLine(PetDialogueIntent intent)
    {
        if (intent == PetDialogueIntent.PetClicked && CountActiveUserTasks() >= 3)
        {
            intent = PetDialogueIntent.PetClickedWhileBusy;
        }

        var now = Environment.TickCount64;
        var isGrabInteraction = PetGrabDialogueContext.IsGrabIntent(intent);
        CancellationTokenSource cts;
        lock (_voicePriorityGate)
        {
            if (_isClosing ||
                now < (isGrabInteraction
                    ? _grabVoiceAvailableAtMilliseconds
                    : _interactionVoiceAvailableAtMilliseconds) ||
                IsInteractionVoiceBlocked(now))
            {
                return false;
            }

            if (isGrabInteraction)
            {
                _grabVoiceAvailableAtMilliseconds = now + GrabVoiceCooldownMilliseconds;
            }

            _interactionVoiceAvailableAtMilliseconds = now + InteractionVoiceCooldownMilliseconds;
            CancelWithoutThrow(_interactionDialogueCts);
            cts = CancellationTokenSource.CreateLinkedTokenSource(_dialogueLifetimeCts.Token);
            _interactionDialogueCts = cts;
        }

        _ = DeliverInteractionLineAsync(intent, cts);
        return true;
    }

    private async Task DeliverInteractionLineAsync(
        PetDialogueIntent intent,
        CancellationTokenSource requestCancellation)
    {
        try
        {
            var line = await _dialogue.CreateLineAsync(
                intent,
                userText: null,
                memories: null,
                dynamicDialogueEnabled: _settings.DynamicDialogueEnabled,
                shareMemoryWithDialogue: false,
                requestCancellation.Token);

            if (requestCancellation.IsCancellationRequested ||
                _isClosing ||
                IsInteractionVoiceBlocked(Environment.TickCount64))
            {
                return;
            }

            await PresentCharacterLineAsync(
                line,
                isImportant: false,
                visibleDuration: TimeSpan.FromSeconds(4),
                isStillCurrent: () => !requestCancellation.IsCancellationRequested,
                speechPerformance:
                    PetGrabDialogueContext.SpeechPerformance(intent) ??
                    PetActionDialogueContext.SpeechPerformance(intent));
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Interaction dialogue failed: {ex.GetType().Name}");
        }
        finally
        {
            lock (_voicePriorityGate)
            {
                if (ReferenceEquals(_interactionDialogueCts, requestCancellation))
                {
                    _interactionDialogueCts = null;
                }
            }

            requestCancellation.Dispose();
        }
    }

    private void BeginImportantVoice()
    {
        CancelGrabReleaseLinePreparation();
        lock (_voicePriorityGate)
        {
            CancelWithoutThrow(_interactionDialogueCts);
            ExtendImportantVoiceProtection(ImportantVoiceProtectionMilliseconds);
            _speech.Stop();
        }
    }

    private void ExtendImportantVoiceProtection(long milliseconds)
    {
        var target = Environment.TickCount64 + milliseconds;
        while (true)
        {
            var current = Interlocked.Read(ref _importantVoiceProtectionUntilMilliseconds);
            if (current >= target ||
                Interlocked.CompareExchange(
                    ref _importantVoiceProtectionUntilMilliseconds,
                    target,
                    current) == current)
            {
                return;
            }
        }
    }

    private bool IsInteractionVoiceBlocked(long now) =>
        Volatile.Read(ref _importantSpeechActiveCount) > 0 ||
        now < Interlocked.Read(ref _importantVoiceProtectionUntilMilliseconds);

    private void CancelAutomaticDialogue()
    {
        lock (_dialogueRequestGate)
        {
            CancelWithoutThrow(_automaticDialogueCts);
            _automaticDialogueCts = null;
        }
    }

    private void CancelInteractionDialogue()
    {
        CancelGrabReleaseLinePreparation();
        lock (_voicePriorityGate)
        {
            CancelWithoutThrow(_interactionDialogueCts);
            _interactionDialogueCts = null;
        }
    }

    private static void CancelWithoutThrow(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cancellation callback failed: {ex.GetType().Name}");
        }
    }

    private async Task SpeakSafelyAsync(
        string text,
        bool isImportant = false,
        CancellationToken cancellationToken = default,
        string? speechPerformance = null,
        SpeechService.PreparedSpeech? preparedSpeech = null)
    {
        if (isImportant)
        {
            Interlocked.Increment(ref _importantSpeechActiveCount);
        }

        try
        {
            Task speechTask;
            if (Dispatcher.CheckAccess())
            {
                speechTask = _settings.TtsEnabled
                    ? preparedSpeech is not null
                        ? _speech.SpeakPreparedAsync(preparedSpeech, cancellationToken)
                        : _speech.SpeakAsync(text, speechPerformance, cancellationToken)
                    : Task.CompletedTask;
            }
            else
            {
                speechTask = await Dispatcher.InvokeAsync(() =>
                    _settings.TtsEnabled
                        ? preparedSpeech is not null
                            ? _speech.SpeakPreparedAsync(preparedSpeech, cancellationToken)
                            : _speech.SpeakAsync(text, speechPerformance, cancellationToken)
                        : Task.CompletedTask);
            }

            await speechTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TTS failed: {ex}");
        }
        finally
        {
            if (isImportant)
            {
                Interlocked.Decrement(ref _importantSpeechActiveCount);
                ExtendImportantVoiceProtection(1200);
            }
        }
    }

    private async void ReconnectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Exception? localError = null;
        try
        {
            await _codex.ReconnectAsync();
            var localThreads = await _codex.ListThreadsAsync();
            foreach (var thread in localThreads.Where(thread =>
                         !CompanionThreadIdentity.IsCompanionSource(thread.ThreadSource) &&
                         !CompanionThreadIdentity.IsCompanionWorkspace(thread.Cwd)))
            {
                _desktopMonitor.AddTaskIdCandidate(thread.Id);
            }
        }
        catch (Exception ex)
        {
            localError = ex;
            App.WriteErrorLog("Independent Codex app-server reconnect failure", ex);
        }

        try
        {
            await _dialogueClient.ReconnectAsync();
            await _dialogue.WarmUpAsync(_dialogueLifetimeCts.Token);
        }
        catch (Exception ex)
        {
            // Luna dialogue is optional. Keep the Codex panel connected and let the
            // character fall back to its local lines when subscription access is absent.
            App.WriteErrorLog("Codex Luna dialogue reconnect failure", ex);
        }

        try
        {
            await _desktopMonitor.RefreshAsync();
            RenderDesktopSnapshot(_desktopMonitor.Snapshot);
        }
        catch (Exception ex)
        {
            App.WriteErrorLog("Codex desktop task monitor reconnect failure", ex);
            if (localError is null)
            {
                localError = ex;
            }
        }

        if (localError is not null && !_desktopMonitor.Snapshot.IsConnected)
        {
            SetStatus("连接失败", Brushes.IndianRed);
            EnsurePanel();
            _panel?.ShowStartupError(localError.GetBaseException().Message);
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownReady)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        CancelWorkloadForGrab();
        _animationPreparationLifetime.Cancel();
        if (_chatOrb is not null)
        {
            _chatOrb.ChatRequested -= ChatOrb_ChatRequested;
            _chatOrb.Close();
            _chatOrb = null;
        }

        CancelVoiceConversation();
        CancelAutomaticDialogue();
        CancelInteractionDialogue();
        CancelWithoutThrow(_dialogueLifetimeCts);
        _movementTimer.Stop();
        _animationTimer.Stop();
        _dragHoldTimer.Stop();
        _pointerGesture.Cancel();
        CancelClickFlickFallForGrab();
        CancelCuteAngryForGrab();
        CancelWithoutThrow(_dragReleaseCts);
        CancelSeatedRoutine();
        StopAutonomousMovement(restorePersistentMood: false);
        CancelBubbleHide();
        CancelTemporaryMoodAndRestoreScale();
        CancelWithoutThrow(_messageFeedbackCts);
        CancelWithoutThrow(_singleClickCts);
        try
        {
            // Persist the visible resting viewport, not the larger transparent
            // animation host. RestoreWindowPosition expects the same coordinate.
            _settings.WindowLeft = RestingViewportLeft;
            _settings.WindowTop = RestingViewportTop;
            try
            {
                await _settingsStore.SaveAsync(_settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings shutdown save failed: {ex}");
            }

            _progress.ProgressChanged -= Codex_ProgressChanged;
            _desktopMonitor.SnapshotChanged -= DesktopMonitor_SnapshotChanged;
            _dialogueClient.CompanionThreadObserved -= DialogueClient_CompanionThreadObserved;
            _codex.ConnectionStateChanged -= Codex_ConnectionStateChanged;
            _codex.ApprovalRequested -= Codex_ApprovalRequested;
            _codex.TurnCompleted -= Codex_TurnCompleted;

            if (_panel is not null)
            {
                _panel.IsVisibleChanged -= Panel_IsVisibleChanged;
                _panel.AllowClose();
                _panel.Close();
            }

            _settingsWindow?.Close();
            _settingsWindow = null;

            if (_progressBubble is not null)
            {
                _progressBubble.HideBubble();
                _progressBubble.Close();
                _progressBubble = null;
            }

            try
            {
                _progress.Dispose();
                await _desktopMonitor.DisposeAsync();
                await _desktopBridge.DisposeAsync();
                await _codex.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Codex shutdown failed: {ex}");
            }

            await _dialogueClient.DisposeAsync();
            await _microphone.DisposeAsync();
            await _transcription.DisposeAsync();
            _dialogueLifetimeCts.Dispose();
            _speech.Dispose();
        }
        finally
        {
            _animationPreparationLifetime.Dispose();
            _shutdownReady = true;
            _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Send);
        }
    }

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
