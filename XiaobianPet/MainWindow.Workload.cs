using System.IO;
using System.Text.Json;
using XiaobianPet.Models;
using XiaobianPet.Services;

namespace XiaobianPet;

public partial class MainWindow
{
    private readonly PetWorkloadRoutinePolicy _workloadPolicy = new();
    private IReadOnlyDictionary<PetMood, PreparedAnimationSequence>? _workloadSequences;
    private WorkloadPack? _workloadPack;
    private CancellationTokenSource? _workloadRoutineCts;
    private long _nextWorkloadObservationAt;
    private readonly Dictionary<string, long> _workloadObservedActiveTasks = new(StringComparer.Ordinal);
    private long _workloadTaskEpoch;
    private bool IsWorkloadRoutineActive => _workloadRoutineCts is not null ||
        _visualOwner == PetVisualOwner.WorkloadRoutine;
    private bool WorkloadReactionsReady => _workloadSequences is not null;

    private sealed class WorkloadPack
    {
        public WorkloadPack() { }
        public bool QaApproved { get; set; }
        public Dictionary<string, WorkloadClip> Clips { get; set; } = [];
    }

    private sealed class WorkloadClip
    {
        public WorkloadClip() { }
        public int FrameCount { get; set; }
        public double ContentScale { get; set; } = 1;
        // Fractions of the resting content rectangle, calibrated once per
        // family. Never derive scale/position from each frame's body bounds.
        public double CenterOffsetX { get; set; }
        public double CenterOffsetY { get; set; }
        public PetWorkloadGrabProfile? GrabRegions { get; set; }
    }

    private async Task PrepareWorkloadAnimationsAsync(string animationsRoot)
    {
        var path = Path.Combine(animationsRoot, "workload-pack.json");
        if (!File.Exists(path) || _spriteAtlas is not { } atlas) return;
        var token = _animationPreparationLifetime.Token;
        try
        {
            var prepared = await Task.Run(() =>
            {
                var pack = JsonSerializer.Deserialize<WorkloadPack>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (pack is not { QaApproved: true })
                    throw new InvalidDataException("任务量动画尚未通过整组验收。");
                var sequences = new Dictionary<PetMood, PreparedAnimationSequence>();
                foreach (var mood in PetAnimations.WorkloadMoods)
                {
                    token.ThrowIfCancellationRequested();
                    if (!pack.Clips.TryGetValue(mood.ToString(), out var clip) ||
                        !double.IsFinite(clip.ContentScale) || clip.ContentScale < 1 ||
                        clip.ContentScale > PetAnimations.MaximumPresentationScale ||
                        !double.IsFinite(clip.CenterOffsetX) || !double.IsFinite(clip.CenterOffsetY))
                        throw new InvalidDataException($"{mood} 缺少有效的固定头部尺寸校准。");
                    var sequence = atlas.PrepareSequence(mood);
                    if (sequence.Frames.Count != clip.FrameCount)
                        throw new InvalidDataException($"{mood} 帧数不完整，不能启用这一组动画。");
                    if ((mood is PetMood.WorkloadComputerEnter or PetMood.WorkloadCryTyping or
                        PetMood.WorkloadWipeTears or PetMood.WorkloadDeskSlump or
                        PetMood.WorkloadDeskBonk or PetMood.WorkloadComputerExit or
                        PetMood.WorkloadCollapse or PetMood.WorkloadStandUp) && clip.GrabRegions is null)
                        throw new InvalidDataException($"{mood} 缺少姿态对应的抓取区域。");
                    clip.GrabRegions?.Validate(sequence.Frames.Count);
                    sequences.Add(mood, sequence);
                }
                return (pack, sequences);
            }, token);
            if (_isClosing || token.IsCancellationRequested) return;
            _workloadPack = prepared.pack;
            _workloadSequences = prepared.sequences;
            WriteWorkloadLoadReceipt();
            UpdateWorkloadRoutine(Environment.TickCount64);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Optional pack failure leaves the already working pet untouched.
            App.WriteErrorLog("Workload animation family preparation failure", ex);
        }
    }

    private void WriteWorkloadLoadReceipt()
    {
        // A small local receipt verifies the actual deployed process, not just
        // the presence of files in a build directory. Hidden harnesses must not
        // overwrite the running pet's receipt or user settings.
        if (string.Equals(Environment.GetEnvironmentVariable(App.UiPreviewEnvironmentVariable),
            "1", StringComparison.Ordinal)) return;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "XiaobianPet");
            Directory.CreateDirectory(directory);
            var receipt = new
            {
                status = "ready",
                processId = Environment.ProcessId,
                loadedAt = DateTimeOffset.Now,
                interactionRevision = "task-context-hover-pat-20260913",
                taskContextFeedbackEnabled = _settings.TaskContextFeedbackEnabled,
                hoverHeadPatEnabled = true,
                softLandingEnabled = false,
                allClipsFullyDecoded = _workloadSequences!.Count == PetAnimations.WorkloadMoods.Count,
                clips = _workloadSequences.ToDictionary(pair => pair.Key.ToString(),
                    pair => pair.Value.Frames.Count),
                window = new { isLoaded = IsLoaded, isVisible = IsVisible,
                    state = WindowState.ToString(), left = Left, top = Top, width = Width, height = Height }
            };
            File.WriteAllText(Path.Combine(directory, "workload-runtime-status.json"),
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Diagnostic IO failure must not disable already decoded media. */ }
    }

    private bool? TryClassifyWorkloadGrabPoint(double frameX, double frameY, out PetGrabRegion region)
    {
        region = PetGrabRegion.Body;
        if (_currentSequence is not { } sequence || _workloadPack is not { } pack ||
            !pack.Clips.TryGetValue(sequence.VisualMood.ToString(), out var clip) ||
            clip.GrabRegions is not { } profile) return null;
        return profile.TryClassify(_frameIndex, frameX, frameY, out region);
    }

    private void UpdateWorkloadRoutine(long now)
    {
        if (!WorkloadReactionsReady || _isClosing) return;
        if (now >= _nextWorkloadObservationAt)
        {
            _nextWorkloadObservationAt = now + 250;
            _workloadPolicy.Observe(CountActiveUserTasks(), _desktopSnapshot.IsConnected, now);
        }
        if (IsWorkloadRoutineActive || !_workloadPolicy.HasPendingStandingAction(now) ||
            _pointerGesture.IsPressed || IsClickInteractionReserved ||
            IsCuteAngryPendingOrActive || _isTemporaryMoodActive ||
            _temporaryMoodCts is not null || _positiveReactionPending ||
            PetVisualTransitionPolicy.IsForcedInteraction(_visualOwner)) return;

        if (_isSeatedRoutineActive)
        {
            // Seated gestures finish, then the real StandUp clip plays. A chair
            // scene must never replace crossed legs directly.
            RequestGracefulSeatedExit();
            return;
        }
        if (_isAutonomousMoving || _visualOwner != PetVisualOwner.Persistent ||
            _currentSequence?.VisualMood != PetMood.Idle) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_animationPreparationLifetime.Token);
        _workloadRoutineCts = cts;
        _ = RunWorkloadRoutineAsync(cts);
    }

    private bool TryQueueWorkloadCompletion(string eventKey)
    {
        if (!WorkloadReactionsReady) return false;
        var now = Environment.TickCount64;
        _workloadPolicy.ObserveSuccessfulCompletion(eventKey, now);
        UpdateWorkloadRoutine(now);
        return true; // The workload reaction replaces automatic celebration.
    }

    private void ObserveWorkloadDesktopCompletions(CodexDesktopMonitorSnapshot snapshot)
    {
        if (!WorkloadReactionsReady || !snapshot.IsConnected) return;
        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in snapshot.Tasks)
        {
            if (CompanionThreadIdentity.IsCompanionWorkspace(task.Cwd) ||
                _desktopMonitor.IsTaskExcluded(task.Id) ||
                string.Equals(task.Id, _dialogueClient.ConversationThreadId, StringComparison.Ordinal) ||
                string.Equals(task.Id, _codex.CurrentThreadId, StringComparison.Ordinal)) continue;
            visibleIds.Add(task.Id);
            if (IsDesktopTaskActive(task))
            {
                if (!_workloadObservedActiveTasks.ContainsKey(task.Id))
                    _workloadObservedActiveTasks[task.Id] = ++_workloadTaskEpoch;
            }
            else if (HasDesktopSignal(task, "completed") &&
                     _workloadObservedActiveTasks.Remove(task.Id, out var epoch))
            {
                // All task completions are observed, not only whichever event
                // wins the one-visible-bubble priority. Removing the active
                // epoch deduplicates later summary/UpdatedAt refinements.
                var turnId = (task as CodexDesktopTaskDetail)?.CurrentTurnId;
                TryQueueWorkloadCompletion($"desktop:{task.Id}:{turnId ?? $"observed-run-{epoch}"}");
            }
            else if (HasDesktopSignal(task, "interrupted") || IsDesktopTaskError(task) ||
                     NormalizeDesktopSignal(task.Status) == "notloaded")
                _workloadObservedActiveTasks.Remove(task.Id);
        }
        foreach (var id in _workloadObservedActiveTasks.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
            _workloadObservedActiveTasks.Remove(id); // Missing is not success.
    }

    private void CancelWorkloadForGrab()
    {
        // The replacement grab is fully decoded before this is called. The old
        // routine's finally block must not touch that grab's image or geometry.
        _workloadPolicy.InterruptForGrab(Environment.TickCount64);
        var old = _workloadRoutineCts;
        _workloadRoutineCts = null;
        CancelWithoutThrow(old);
        CancelPendingVisualTransition(PetVisualOwner.WorkloadRoutine);
        CancelAnimationBoundary(PetVisualOwner.WorkloadRoutine);
    }

    private async Task RunWorkloadRoutineAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        var returnedToStanding = true;
        try
        {
            // Choose only in the boundary callback; the workload can change
            // while the current idle completes. All clips are already decoded.
            var action = PetWorkloadAction.None;
            var activated = await RequestNormalVisualTransition(PetMood.WorkloadConfident,
                PetVisualOwner.WorkloadRoutine, token, () =>
                {
                    if (token.IsCancellationRequested || _isClosing ||
                        !ReferenceEquals(_workloadRoutineCts, cts)) return false;
                    action = _workloadPolicy.TakeNextStandingAction(Environment.TickCount64);
                    return action != PetWorkloadAction.None;
                });
            if (!activated) return;
            while (action != PetWorkloadAction.None)
            {
                switch (action)
                {
                    case PetWorkloadAction.Confident:
                        await PlayWorkloadClipAsync(PetMood.WorkloadConfident, cts,
                            "哎呀，交给我吧！", "可爱自信、轻快地逞一点小强，不夸张喊叫。");
                        break;
                    case PetWorkloadAction.Panting:
                        await PlayWorkloadClipAsync(PetMood.WorkloadPanting, cts,
                            "呼呼，累死了……", "忙碌后的轻微喘气，小声可爱抱怨，仍在认真工作。");
                        break;
                    case PetWorkloadAction.WipeSweat:
                        await PlayWorkloadClipAsync(PetMood.WorkloadWipeSweat, cts,
                            "呼，做好啦！", "完成一件事情后的轻松与如释重负。");
                        break;
                    case PetWorkloadAction.Computer:
                        returnedToStanding = false;
                        await PlayWorkloadClipAsync(PetMood.WorkloadComputerEnter, cts);
                        var next = _workloadPolicy.BeginComputerVisit();
                        var firstTyping = true;
                        while (_workloadPolicy.ShouldContinueComputer)
                        {
                            await PlayWorkloadClipAsync(PetAnimations.ComputerMood(next), cts,
                                firstTyping && next == PetComputerInterlude.Typing
                                    ? "呜呜，主人，根本做不完啦！" : null,
                                "软软的可爱哭腔，带一点鼻音，边委屈边努力，不是痛苦尖叫。");
                            if (next == PetComputerInterlude.Typing) firstTyping = false;
                            next = next == PetComputerInterlude.Typing
                                ? _workloadPolicy.AfterCompleteTypingCycle(
                                    Random.Shared.NextDouble(), Random.Shared.NextDouble())
                                : PetComputerInterlude.Typing;
                        }
                        // Finish whichever interlude was on screen, leave the
                        // physical desk/chair, then use the floor-rest family.
                        await PlayWorkloadClipAsync(PetMood.WorkloadComputerExit, cts);
                        returnedToStanding = true;
                        break;
                    case PetWorkloadAction.Rest:
                        returnedToStanding = false;
                        await PlayWorkloadClipAsync(PetMood.WorkloadCollapse, cts);
                        // The held image is the actual decoded last video frame,
                        // not an old image-generated idle or reversed collapse.
                        await Task.Delay(PetWorkloadRoutinePolicy.GroundRestDuration, token);
                        await PlayWorkloadClipAsync(PetMood.WorkloadStandUp, cts);
                        returnedToStanding = true;
                        _workloadPolicy.MarkGroundRestCompleted();
                        break;
                }
                token.ThrowIfCancellationRequested();
                action = _workloadPolicy.TakeNextStandingAction(Environment.TickCount64);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.WriteErrorLog("Workload routine playback failure", ex);
        }
        finally
        {
            if (ReferenceEquals(_workloadRoutineCts, cts))
            {
                _workloadRoutineCts = null;
                if (!_isClosing && _visualOwner == PetVisualOwner.WorkloadRoutine)
                {
                    CancelAnimationBoundary(PetVisualOwner.WorkloadRoutine);
                    if (returnedToStanding)
                    {
                        _visualOwner = PetVisualOwner.Persistent;
                        var prepared = _spriteAtlas!.PrepareSequence(_persistentMood);
                        ApplyMoodCore(_persistentMood, PetVisualOwner.Persistent, prepared);
                        RestoreWindowSizeAfterAutonomousMovement();
                        ScheduleNextIdleGesture(Environment.TickCount64);
                    }
                    else
                    {
                        // An IO/playback failure cannot invent a standing frame
                        // from a prone/chair pose. Retain the last valid visual;
                        // a real grab can always recover control.
                        _workloadSequences = null;
                    }
                }
            }
            cts.Dispose();
        }
    }

    private async Task PlayWorkloadClipAsync(PetMood mood, CancellationTokenSource cts,
        string? line = null, string? performance = null)
    {
        var token = cts.Token;
        token.ThrowIfCancellationRequested();
        var prepared = _workloadSequences![mood];
        var clip = _workloadPack!.Clips[mood.ToString()];
        TaskCompletionSource<bool>? completion = null;
        var applied = await RequestNormalVisualTransition(mood, PetVisualOwner.WorkloadRoutine,
            token, () =>
            {
                if (_isClosing || token.IsCancellationRequested ||
                    !ReferenceEquals(_workloadRoutineCts, cts)) return false;
                if (!ApplyMoodAtAnimationBoundary(mood, PetVisualOwner.WorkloadRoutine, prepared))
                    return false;
                ApplyAutonomousContentScale(clip.ContentScale,
                    clip.CenterOffsetX * (RestingWindowWidth - PetAnimations.PetImageTotalMargin),
                    clip.CenterOffsetY * (RestingWindowHeight - PetAnimations.PetImageTotalMargin));
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _animationBoundaryWaiter = new AnimationBoundaryWaiter(_playbackGeneration,
                    mood, PetVisualOwner.WorkloadRoutine, StopAtBoundary: true, completion);
                if (line is not null)
                    _ = PresentCharacterLineAsync(line, isImportant: true,
                        isStillCurrent: () => ReferenceEquals(_workloadRoutineCts, cts) &&
                            _visualOwner == PetVisualOwner.WorkloadRoutine,
                        cancellationToken: token, playMessageFeedback: false,
                        speechPerformance: performance);
                return true;
            });
        if (!applied || completion is null)
            throw new OperationCanceledException("Workload animation lost its visual reservation.", token);
        await AwaitAnimationBoundaryAsync(completion,
            PetAnimations.CalculateAnimationWatchdog(prepared.Frames.Count, prepared.FrameDuration),
            mood, token);
    }
}
