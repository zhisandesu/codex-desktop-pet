using XiaobianPet.Models;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void ExpectExternalOneShot(
    PetMood mood,
    string expectedDirectory,
    int expectedFrameCount,
    TimeSpan expectedFrameDuration)
{
    Expect(
        PetAnimations.ExternalFrameDirectories.TryGetValue(mood, out var directory) &&
        directory == expectedDirectory,
        $"{mood} must map to '{expectedDirectory}'");

    var definition = PetAnimations.Definitions[mood];
    Expect(
        definition.FrameCount == expectedFrameCount,
        $"{mood} must contain {expectedFrameCount} frames");
    Expect(
        definition.FrameDuration == expectedFrameDuration,
        $"{mood} must use its declared production frame duration");
    Expect(!definition.Loop, $"{mood} must be a one-shot animation");
}

ExpectExternalOneShot(
    PetMood.ClickFlickFall,
    "click-flick-fall",
    expectedFrameCount: 193,
    expectedFrameDuration: PetAnimations.ClickFlickFallFrameDuration);
ExpectExternalOneShot(
    PetMood.CuteAngry,
    "cute-angry-video",
    expectedFrameCount: 121,
    expectedFrameDuration: PetAnimations.ExternalAnimationFrameDuration);
ExpectExternalOneShot(
    PetMood.IdleChinShake,
    "idle-chin-shake-video",
    expectedFrameCount: 121,
    expectedFrameDuration: PetAnimations.ExternalAnimationFrameDuration);
ExpectExternalOneShot(
    PetMood.SeatedChinShake,
    "seated-chin-shake-video",
    expectedFrameCount: 121,
    expectedFrameDuration: PetAnimations.ExternalAnimationFrameDuration);

using (var preparationLifetime = new PetAnimationPreparationLifetime())
{
    var preparationToken = preparationLifetime.Token;
    Expect(
        !preparationToken.IsCancellationRequested,
        "window-owned animation preparation must begin with a live token");
    preparationLifetime.Cancel();
    Expect(
        preparationToken.IsCancellationRequested,
        "window shutdown must cancel every grab/multitask background preparation");
}
var disposedPreparationLifetime = new PetAnimationPreparationLifetime();
var disposedPreparationToken = disposedPreparationLifetime.Token;
disposedPreparationLifetime.Dispose();
Expect(
    disposedPreparationToken.IsCancellationRequested,
    "disposing the preparation lifetime must cancel its token before releasing it");

Expect(
    Math.Abs(PetAnimations.ClickFlickFallContentScale - ((213d / 165d) * 0.98)) < 0.000001 &&
    Math.Abs(165 * PetAnimations.ClickFlickFallContentScale / 213 - 0.98) < 1e-12,
    "ClickFlickFall must align its final complete head to idle then uniformly shrink 2%");
Expect(
    Math.Abs(PetAnimations.MaximumPresentationScale - 1.35d) < 0.000001,
    "the fixed host must reserve the largest calibrated drag presentation");
var clickTargetWidth = PetAnimations.CalculateContentScaledWindowDimension(
    242,
    PetAnimations.ClickFlickFallContentScale);
var clickTargetHeight = PetAnimations.CalculateContentScaledWindowDimension(
    276,
    PetAnimations.ClickFlickFallContentScale);
Expect(
    Math.Abs(clickTargetWidth - 302.4407272727273) < 0.000001 &&
    Math.Abs(clickTargetHeight - 345.4538181818182) < 0.000001 &&
    Math.Abs(
        ((clickTargetWidth - PetAnimations.PetImageTotalMargin) /
         (242 - PetAnimations.PetImageTotalMargin)) -
        ((213d / 165d) * 0.98)) < 0.000001,
    "ClickFlickFall must compensate for the fixed image margin when matching head size");

Expect(
    Math.Abs(PetAnimations.CuteAngryWindowScale - 1d) < 0.000001,
    "CuteAngry must not enlarge the already idle-matched complete silhouette");
var cuteAngryTargetWidth = PetAnimations.CalculateContentScaledWindowDimension(
    242,
    PetAnimations.CuteAngryWindowScale);
var cuteAngryTargetHeight = PetAnimations.CalculateContentScaledWindowDimension(
    276,
    PetAnimations.CuteAngryWindowScale);
Expect(
    Math.Abs(cuteAngryTargetWidth - 242d) < 0.000001 &&
    Math.Abs(cuteAngryTargetHeight - 276d) < 0.000001 &&
    Math.Abs(
        ((cuteAngryTargetWidth - PetAnimations.PetImageTotalMargin) /
         (242 - PetAnimations.PetImageTotalMargin)) -
        1d) < 0.000001,
    "CuteAngry must keep the normal resting content and window dimensions");

var clickDefinition = PetAnimations.Definitions[PetMood.ClickFlickFall];
var clickDuration = PetAnimations.CalculatePreviewDuration(
    clickDefinition.FrameCount,
    clickDefinition.FrameDuration);
var clickWatchdog = PetAnimations.CalculateAnimationWatchdog(
    clickDefinition.FrameCount,
    clickDefinition.FrameDuration);
Expect(
    clickDuration <= PetAnimations.MaximumSingleAnimationDuration &&
    clickDuration > TimeSpan.FromSeconds(5.9),
    "the complete 193-frame click reaction must fit just below the six-second cap");
Expect(
    clickWatchdog == clickDuration + TimeSpan.FromSeconds(2),
    "the generic animation watchdog must be the actual duration plus two seconds");

foreach (var (mood, directory) in PetAnimations.ExternalFrameDirectories)
{
    var definition = PetAnimations.Definitions[mood];
    var duration = PetAnimations.CalculatePreviewDuration(
        definition.FrameCount,
        PetAnimations.GetExternalFrameDuration(mood));
    Expect(
        duration <= PetAnimations.MaximumSingleAnimationDuration,
        $"external animation {mood} ({directory}) exceeds the six-second runtime cap");
}

Expect(
    PetAnimations.GetExternalFrameDuration(PetMood.RunFallRight) ==
    PetAnimations.RunFallFrameDuration &&
    PetAnimations.GetExternalFrameDuration(PetMood.RunFallLeft) ==
    PetAnimations.RunFallFrameDuration &&
    PetAnimations.CalculatePreviewDuration(
        193,
        PetAnimations.RunFallFrameDuration) <=
    PetAnimations.MaximumSingleAnimationDuration &&
    PetAnimations.MaximumSingleAnimationDuration -
    PetAnimations.CalculatePreviewDuration(
        193,
        PetAnimations.RunFallFrameDuration) <
    PetAnimations.RunFallFrameDuration,
    "both complete 193-frame running falls must preserve every decoded frame within six seconds");

Expect(
    PetAnimations.IdleGestureMoods.Contains(PetMood.IdleChinShake),
    "the idle gesture pool must include IdleChinShake");
Expect(
    PetAnimations.SeatedGestureMoods.Contains(PetMood.SeatedChinShake),
    "the seated gesture pool must include the independent seated chin shake");

var ambientIdleMoods = new[]
{
    PetMood.Idle,
    PetMood.Working,
    PetMood.Review,
    PetMood.Waiting,
    PetMood.Failed
};
Expect(
    ambientIdleMoods.All(mood =>
        PetSeatedRoutinePolicy.IsAmbientIdleMood(mood) &&
        PetAnimations.ResolveVisualMood(mood) == PetMood.Idle),
    "all status moods using the idle video must share the ambient-idle policy");
Expect(
    ambientIdleMoods.All(persistentMood =>
        ambientIdleMoods.All(currentMood =>
            PetSeatedRoutinePolicy.CanRunSeatedRoutine(
                persistentMood,
                currentMood))),
    "all ambient idle fallback transitions must allow a seated routine to continue");
Expect(
    !PetSeatedRoutinePolicy.IsAmbientIdleMood(PetMood.ClickFlickFall) &&
    !PetSeatedRoutinePolicy.CanRunSeatedRoutine(PetMood.Failed, PetMood.ClickFlickFall),
    "failure status must not bypass exclusive interaction playback");

Expect(
    PetSeatedRoutinePolicy.CalculateDelay(
        PetSeatedRoutineScheduleKind.Initial,
        0) == TimeSpan.FromSeconds(8) &&
    PetSeatedRoutinePolicy.CalculateDelay(
        PetSeatedRoutineScheduleKind.Initial,
        1) == TimeSpan.FromSeconds(15),
    "the first seated visit must be scheduled within 8-15 seconds");
Expect(
    PetSeatedRoutinePolicy.CalculateDelay(
        PetSeatedRoutineScheduleKind.Recurring,
        0) == TimeSpan.FromSeconds(55) &&
    PetSeatedRoutinePolicy.CalculateDelay(
        PetSeatedRoutineScheduleKind.Recurring,
        1) == TimeSpan.FromSeconds(85),
    "later seated visits must retain the 55-85 second cadence");
Expect(
    PetSeatedRoutinePolicy.CalculateVisitDuration(0) == TimeSpan.FromSeconds(60) &&
    PetSeatedRoutinePolicy.CalculateVisitDuration(1) == TimeSpan.FromSeconds(180) &&
    PetSeatedRoutinePolicy.CalculateGestureOpportunityDelay(0) == TimeSpan.FromSeconds(8) &&
    PetSeatedRoutinePolicy.CalculateGestureOpportunityDelay(1) == TimeSpan.FromSeconds(24),
    "a seated visit must loop SeatedIdle for 60-180 seconds with dynamic opportunities every 8-24 seconds");
Expect(
    PetSeatedRoutinePolicy.ShouldPlayGestureOpportunity(0) &&
    PetSeatedRoutinePolicy.ShouldPlayGestureOpportunity(0.6499) &&
    !PetSeatedRoutinePolicy.ShouldPlayGestureOpportunity(0.65) &&
    !PetSeatedRoutinePolicy.ShouldPlayGestureOpportunity(1),
    "each dynamic opportunity must play a seated gesture 65% of the time and otherwise keep SeatedIdle looping");
Expect(
    PetAnimations.Definitions[PetMood.SeatedIdle].Loop &&
    !PetSeatedRoutinePolicy.ShouldReleaseIdleLoopAtBoundary(
        exitRequested: false,
        nowMilliseconds: 59_999,
        deadline: 60_000) &&
    PetSeatedRoutinePolicy.ShouldReleaseIdleLoopAtBoundary(
        exitRequested: false,
        nowMilliseconds: 60_000,
        deadline: 60_000) &&
    PetSeatedRoutinePolicy.ShouldReleaseIdleLoopAtBoundary(
        exitRequested: true,
        nowMilliseconds: 1,
        deadline: 60_000) &&
    !PetSeatedRoutinePolicy.ShouldReleaseIdleLoopAtBoundary(
        exitRequested: false,
        nowMilliseconds: 60_000,
        deadline: PetSeatedRoutinePolicy.UnscheduledDeadline),
    "SeatedIdle must remain a complete loop and release only at a real boundary after exit or visit deadline");
Expect(
    PetSeatedRoutinePolicy.SelectGesture(0) == PetMood.SeatedBlink &&
    PetSeatedRoutinePolicy.SelectGesture(0.32) == PetMood.SeatedHeadTilt &&
    PetSeatedRoutinePolicy.SelectGesture(0.56) == PetMood.SeatedChinShake &&
    PetSeatedRoutinePolicy.SelectGesture(0.72) == PetMood.SeatedCoverHead &&
    PetSeatedRoutinePolicy.SelectGesture(0.80) == PetMood.SeatedDoubleCheekCute &&
    PetSeatedRoutinePolicy.SelectGesture(0, PetMood.SeatedBlink) != PetMood.SeatedBlink,
    "seated gestures must follow their weighted pool without an adjacent repeat");
foreach (var previousGesture in PetAnimations.SeatedGestureMoods)
{
    foreach (var randomSample in new[] { 0d, 0.20, 0.40, 0.60, 0.80, 1d })
    {
        Expect(
            PetSeatedRoutinePolicy.SelectGesture(randomSample, previousGesture) !=
            previousGesture,
            $"dynamic seated gesture opportunities must not repeat {previousGesture} adjacently");
    }
}

Expect(
    PetMessageFeedbackPolicy.ProbabilityForIntent(PetDialogueIntent.TaskStarted) == 0.40 &&
    PetMessageFeedbackPolicy.ProbabilityForIntent(PetDialogueIntent.TaskCompleted) == 0.50 &&
    PetMessageFeedbackPolicy.ProbabilityForIntent(PetDialogueIntent.TaskFailed) == 0.30 &&
    PetMessageFeedbackPolicy.ProbabilityForIntent(PetDialogueIntent.DirectChat) == 0.20 &&
    PetMessageFeedbackPolicy.ProbabilityForIntent(PetDialogueIntent.PetClicked) == 0,
    "the pointing gesture probability must follow speech context and exclude click/grab chatter");
Expect(
    PetRunDialoguePolicy.RunningEffortProbability == 0.60 &&
    PetRunDialoguePolicy.ShouldSpeakRunningEffort(0) &&
    PetRunDialoguePolicy.ShouldSpeakRunningEffort(0.599999) &&
    !PetRunDialoguePolicy.ShouldSpeakRunningEffort(0.60) &&
    !PetRunDialoguePolicy.ShouldSpeakRunningEffort(1),
    "run-fall effort speech should trigger only below its configured probability");
Expect(
    PetMessageFeedbackPolicy.ShouldSelect(0.4, 0.3999) &&
    !PetMessageFeedbackPolicy.ShouldSelect(0.4, 0.4) &&
    PetMessageFeedbackPolicy.CanOpenOpportunity(
        speechWasShown: true,
        seatedRoutineActive: false,
        forcedInteractionActive: false,
        temporaryAnimationActive: false,
        nowMilliseconds: 20_000,
        availableAtMilliseconds: 20_000) &&
    !PetMessageFeedbackPolicy.CanOpenOpportunity(
        speechWasShown: true,
        seatedRoutineActive: true,
        forcedInteractionActive: false,
        temporaryAnimationActive: false,
        nowMilliseconds: 20_000,
        availableAtMilliseconds: 0) &&
    PetMessageFeedbackPolicy.CalculateLatestStartDeadline(
        10_000,
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(4)) == 14_000 &&
    PetMessageFeedbackPolicy.IsSpeechBubbleActive(
        "pet-speech:42",
        hideScheduleActive: true,
        speechSequence: 42) &&
    !PetMessageFeedbackPolicy.IsSpeechBubbleActive(
        "task-progress:42",
        hideScheduleActive: true,
        speechSequence: 42) &&
    !PetMessageFeedbackPolicy.IsSpeechBubbleActive(
        "pet-speech:42",
        hideScheduleActive: false,
        speechSequence: 42),
    "message feedback must be randomly selected only inside a live, unblocked speech session");

var existingSeatedDeadline = 12_000L;
var replacementSeatedDeadline = 90_000L;
Expect(
    PetSeatedRoutinePolicy.PreserveScheduledDeadline(
        existingSeatedDeadline,
        replacementSeatedDeadline) == existingSeatedDeadline &&
    PetSeatedRoutinePolicy.PreserveScheduledDeadline(
        PetSeatedRoutinePolicy.UnscheduledDeadline,
        replacementSeatedDeadline) == replacementSeatedDeadline,
    "temporary, click, and status transitions must preserve an existing seated deadline");
Expect(
    PetSeatedRoutinePolicy.IsDeadlineDue(12_001, existingSeatedDeadline) &&
    !PetSeatedRoutinePolicy.IsDeadlineDue(
        long.MaxValue,
        PetSeatedRoutinePolicy.UnscheduledDeadline),
    "an overdue seated deadline must remain due while the unscheduled sentinel never fires");

Expect(
    PetSeatedRoutinePolicy.SelectBoundaryAction(
        PetIdleBoundaryAction.IdleGesture,
        seatedDeadlineDue: true,
        canStartSeatedRoutine: true,
        idleGestureDue: true,
        autonomousActionDue: true,
        canStartOrdinaryIdleAction: true) == PetIdleBoundaryAction.SeatedRoutine,
    "an overdue seated visit must supersede lower-priority idle work at the next boundary");
Expect(
    PetSeatedRoutinePolicy.SelectBoundaryAction(
        PetIdleBoundaryAction.SeatedRoutine,
        seatedDeadlineDue: true,
        canStartSeatedRoutine: false,
        idleGestureDue: true,
        autonomousActionDue: true,
        canStartOrdinaryIdleAction: false) == PetIdleBoundaryAction.SeatedRoutine,
    "a queued seated visit must survive a temporarily unavailable boundary");
Expect(
    PetSeatedRoutinePolicy.SelectBoundaryAction(
        PetIdleBoundaryAction.IdleGesture,
        seatedDeadlineDue: false,
        canStartSeatedRoutine: false,
        idleGestureDue: true,
        autonomousActionDue: true,
        canStartOrdinaryIdleAction: true) == PetIdleBoundaryAction.AutonomousMovement,
    "an overdue walk, run, or follow must replace a quiet gesture that has not started yet");
Expect(
    PetSeatedRoutinePolicy.SelectBoundaryAction(
        PetIdleBoundaryAction.IdleGesture,
        seatedDeadlineDue: false,
        canStartSeatedRoutine: false,
        idleGestureDue: true,
        autonomousActionDue: false,
        canStartOrdinaryIdleAction: true) == PetIdleBoundaryAction.IdleGesture,
    "a queued quiet gesture must survive when no rarer movement action is due");

Expect(
    PetAnimations.AnimationWarmUpMoods.Concat(PetAnimations.WorkloadMoods).Distinct().Count() ==
    PetAnimations.ExternalFrameDirectories.Count - 1 &&
    PetAnimations.AnimationWarmUpMoods.Distinct().Count() ==
    PetAnimations.AnimationWarmUpMoods.Count &&
    PetAnimations.AnimationWarmUpMoods.Take(3).SequenceEqual(PetAnimations.DragMoods) &&
    PetAnimations.AnimationWarmUpMoods[3] == PetMood.ClickFlickFall &&
    !PetAnimations.AnimationWarmUpMoods.Contains(PetMood.DragLanding) &&
    !PetAnimations.AnimationPreviewOptions.Any(option => option.Mood == PetMood.DragLanding),
    "warm-up and preview must exclude retired landing; prioritize grabs and flicks");

Expect(
    PetAnimations.Definitions[PetMood.Dragged].FrameCount == 44 &&
    PetAnimations.Definitions[PetMood.DraggedWingPout].FrameCount == 53 &&
    PetAnimations.Definitions[PetMood.DraggedHeadAngry].FrameCount == 45 &&
    PetAnimations.Definitions[PetMood.ClimbingUp].FrameCount == 49,
    "runtime animation definitions must match the four complete production loops");

Expect(
    !PetAnimations.ShouldStopAutonomousAtLoopBoundary(
        PetMood.ClimbingUp,
        completedCycleCount: 1,
        stopRequested: false) &&
    PetAnimations.ShouldStopAutonomousAtLoopBoundary(
        PetMood.ClimbingUp,
        completedCycleCount: 2,
        stopRequested: false),
    "climbing must stop only at its second real timer wrap");
Expect(
    !PetAnimations.ShouldStopAutonomousAtLoopBoundary(
        PetMood.Walking,
        completedCycleCount: PetAnimations.WalkingPlaybackCycleCount - 1,
        stopRequested: true) &&
    PetAnimations.ShouldStopAutonomousAtLoopBoundary(
        PetMood.WalkingLeft,
        completedCycleCount: PetAnimations.WalkingPlaybackCycleCount,
        stopRequested: false),
    "ordinary walking must complete exactly four full cycles before stopping at a real wrap, even if its watchdog has requested an early stop");
Expect(
    !PetAnimations.ShouldStopAutonomousAtLoopBoundary(
        PetMood.Walking,
        completedCycleCount: PetAnimations.WalkingPlaybackCycleCount + 3,
        stopRequested: false,
        enforceWalkingCycleLimit: false) &&
    PetAnimations.ShouldStopAutonomousAtLoopBoundary(
        PetMood.WalkingLeft,
        completedCycleCount: 1,
        stopRequested: true,
        enforceWalkingCycleLimit: false),
    "cursor-follow walking must ignore the ordinary four-cycle limit but still stop at its arrival wrap");
Expect(
    PetAnimations.ResolveAutonomousEntryMood(PetMood.RunFallRight, -1) ==
        PetMood.RunFallRight &&
    PetAnimations.ResolveAutonomousEntryMood(PetMood.RunFallLeft, 1) ==
        PetMood.RunFallLeft &&
    PetAnimations.ResolveAutonomousEntryMood(PetMood.Walking, -1) ==
        PetMood.WalkingLeft &&
    PetAnimations.ResolveAutonomousEntryMood(PetMood.Walking, 1) ==
        PetMood.Walking,
    "run-fall must enter its selected one-shot directly while ordinary walking resolves its own direction");
Expect(
    PetAnimations.GetAnimationPreviewCycleCount(PetMood.Walking) ==
        PetAnimations.WalkingPlaybackCycleCount &&
    PetAnimations.GetAnimationPreviewCycleCount(PetMood.WalkingLeft) ==
        PetAnimations.WalkingPlaybackCycleCount &&
    PetAnimations.GetAnimationPreviewCycleCount(PetMood.RunFallRight) == 1,
    "settings preview must repeat only walking for four complete cycles");
var fourCycleWalkingDuration = PetAnimations.CalculatePreviewDuration(
    checked(
        PetAnimations.Definitions[PetMood.Walking].FrameCount *
        PetAnimations.WalkingPlaybackCycleCount),
    PetAnimations.Definitions[PetMood.Walking].FrameDuration);
Expect(
    fourCycleWalkingDuration == TimeSpan.FromMilliseconds(5824) &&
    fourCycleWalkingDuration < PetAnimations.MaximumSingleAnimationDuration,
    "four complete walking cycles must last 5.824 seconds and remain below the six-second action cap");

var candidates = PetAnimations.CreateIdleGestureCandidates(
    [
        PetMood.IdleChinShake,
        PetMood.IdleHeadTilt,
        PetMood.IdleChinShake,
        PetMood.Idle
    ],
    PetMood.IdleChinShake);
Expect(
    candidates.SequenceEqual([PetMood.IdleHeadTilt]),
    "idle candidates must be distinct idle gestures and strictly exclude the previous mood");

var noRepeatCandidate = PetAnimations.CreateIdleGestureCandidates(
    [PetMood.IdleChinShake],
    PetMood.IdleChinShake);
Expect(
    noRepeatCandidate.SequenceEqual([PetMood.IdleChinShake]),
    "a partial install with one valid idle gesture must keep that asset reachable");

Expect(
    Math.Abs(PetAnimations.IdleGestureWeights.Values.Sum() - 1) < 1e-12 &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0) ==
        PetMood.IdleVariant &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.2799) ==
        PetMood.IdleVariant &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.30) ==
        PetMood.IdleHeadTilt &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.45) ==
        PetMood.IdleHeadTiltAlt &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.55) ==
        PetMood.IdleChinShake &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.65) ==
        PetMood.StandingRamNibble &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.75) ==
        PetMood.SelfPlayPeekaboo &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.85) ==
        PetMood.SelfPlayAirplane &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.95) ==
        PetMood.SelfPlayTail &&
    PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 1) ==
        PetMood.SelfPlayTail,
    "standing gestures must use the weighted blink, head-tilt, chin-touch, RAM and self-play pool");
foreach (var previousGesture in PetAnimations.IdleGestureMoods)
{
    var availableGestures = PetAnimations.CreateIdleGestureCandidates(
        PetAnimations.IdleGestureMoods,
        previousGesture);
    foreach (var randomSample in new[] { 0d, 0.2, 0.5, 0.8, 1d })
    {
        Expect(
            PetAnimations.SelectWeightedIdleGesture(
                availableGestures,
                randomSample) != previousGesture,
            $"standing gesture selection repeated {previousGesture} adjacently");
    }
}
Expect(
    PetAnimations.CalculateIdleGestureDelay(0) == TimeSpan.FromSeconds(1) &&
    PetAnimations.CalculateIdleGestureDelay(0.5) == TimeSpan.FromMilliseconds(2000) &&
    PetAnimations.CalculateIdleGestureDelay(1) == TimeSpan.FromMilliseconds(3000) &&
    PetAnimations.CalculateIdleGestureDelay(1) < PetAnimations.CalculatePreviewDuration(
        PetAnimations.Definitions[PetMood.Idle].FrameCount,
        PetAnimations.Definitions[PetMood.Idle].FrameDuration),
    "every standing gesture deadline must precede the first full idle wrap");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.SelectWeightedIdleGesture(
        PetAnimations.IdleGestureMoods,
        -0.01),
    "weighted standing gesture selection accepted a random sample below zero");

var blockedDuringClick = new[]
{
    PetVisualOwner.Persistent,
    PetVisualOwner.IdleGesture,
    PetVisualOwner.Autonomous,
    PetVisualOwner.Temporary,
    PetVisualOwner.SeatedRoutine,
    PetVisualOwner.ClickInteraction,
    PetVisualOwner.CuteAngryReaction
};
foreach (var requestedOwner in blockedDuringClick)
{
    Expect(
        !PetVisualTransitionPolicy.CanReplace(
            PetVisualOwner.ClickFlickFall,
            requestedOwner),
        $"{requestedOwner} must not interrupt ClickFlickFall");
}

foreach (var requestedOwner in blockedDuringClick)
{
    Expect(
        !PetVisualTransitionPolicy.CanReplace(
            PetVisualOwner.Persistent,
            requestedOwner,
            clickFlickFallReserved: true),
        $"a confirmed, still-preparing click must reject {requestedOwner}");
}
Expect(
    PetVisualTransitionPolicy.CanReplace(
        PetVisualOwner.Persistent,
        PetVisualOwner.Grab,
        clickFlickFallReserved: true),
    "grab must interrupt a confirmed click even while its frames are preparing");
Expect(
    PetVisualTransitionPolicy.CanReplace(
        PetVisualOwner.SeatedRoutine,
        PetVisualOwner.SeatedRoutine,
        clickFlickFallReserved: true),
    "a seated frame may continue only until the prepared click atomically commits");

Expect(
    PetVisualTransitionPolicy.CanReplace(
        PetVisualOwner.ClickFlickFall,
        PetVisualOwner.ClickFlickFall),
    "ClickFlickFall may retain its own visual ownership");
Expect(
    PetVisualTransitionPolicy.CanReplace(
        PetVisualOwner.ClickFlickFall,
        PetVisualOwner.Grab),
    "grabbing must be allowed to interrupt ClickFlickFall");

foreach (var currentOwner in Enum.GetValues<PetVisualOwner>()
             .Where(owner => owner is not PetVisualOwner.ClickFlickFall and
                 not PetVisualOwner.CuteAngryReaction and not PetVisualOwner.Grab and
                 not PetVisualOwner.DragLanding and not PetVisualOwner.WorkloadRoutine))
{
    foreach (var requestedOwner in Enum.GetValues<PetVisualOwner>())
    {
        Expect(
            PetVisualTransitionPolicy.CanReplace(currentOwner, requestedOwner),
            $"ordinary owner {currentOwner} must be replaceable by {requestedOwner}");
    }
}

foreach (var requestedOwner in Enum.GetValues<PetVisualOwner>())
{
    Expect(
        PetVisualTransitionPolicy.CanReplace(PetVisualOwner.Grab, requestedOwner) ==
        (requestedOwner == PetVisualOwner.Grab),
        $"an active grab must reject {requestedOwner} until the grab lifecycle releases it");
}

foreach (var requestedOwner in Enum.GetValues<PetVisualOwner>())
{
    Expect(
        PetVisualTransitionPolicy.CanReplace(
            PetVisualOwner.CuteAngryReaction,
            requestedOwner) ==
        (requestedOwner is PetVisualOwner.CuteAngryReaction or PetVisualOwner.Grab),
        $"an active cute-angry reaction must reject {requestedOwner}; only a real grab may interrupt it");
}

var ordinaryTransitionOwners = new[]
{
    PetVisualOwner.Persistent,
    PetVisualOwner.IdleGesture,
    PetVisualOwner.Autonomous,
    PetVisualOwner.Temporary,
    PetVisualOwner.SeatedRoutine,
    PetVisualOwner.CuteAngryBoundaryRequest
};
foreach (var requested in Enum.GetValues<PetVisualOwner>())
{
    Expect(PetVisualTransitionPolicy.CanReplace(PetVisualOwner.DragLanding, requested) ==
        (requested is PetVisualOwner.DragLanding or PetVisualOwner.Grab),
        $"only a true regrab may interrupt landing; got {requested}");
}
foreach (var requested in ordinaryTransitionOwners)
{
    Expect(PetVisualTransitionQueuePolicy.DecideRequest(PetVisualOwner.DragLanding,
        requested, false, true, false) == PetNormalTransitionRequestDecision.Queue,
        "ordinary requests must stay queued until landing releases ownership");
}
foreach (var owner in ordinaryTransitionOwners)
{
    Expect(
        PetVisualTransitionPolicy.GetTiming(owner) ==
        PetVisualTransitionTiming.AnimationBoundary,
        $"ordinary owner {owner} must wait for the current animation boundary");
    Expect(
        !PetVisualTransitionPolicy.IsForcedInteraction(owner),
        $"ordinary owner {owner} must never use immediate pre-emption");
}

var forcedInteractionOwners = new[]
{
    PetVisualOwner.DragLanding,
    PetVisualOwner.ClickInteraction,
    PetVisualOwner.ClickFlickFall,
    PetVisualOwner.CuteAngryReaction,
    PetVisualOwner.Grab
};
foreach (var owner in forcedInteractionOwners)
{
    Expect(
        PetVisualTransitionPolicy.GetTiming(owner) ==
        PetVisualTransitionTiming.Immediate,
        $"confirmed interaction owner {owner} must pre-empt immediately");
    Expect(
        PetVisualTransitionPolicy.IsForcedInteraction(owner),
        $"confirmed interaction owner {owner} must be marked as forced");
}

foreach (var owner in Enum.GetValues<PetVisualOwner>())
{
    var expectedCanInterruptSeated = owner is
        PetVisualOwner.ClickInteraction or
        PetVisualOwner.ClickFlickFall or
        PetVisualOwner.Grab;
    Expect(
        PetVisualTransitionPolicy.CanInterruptSeatedRoutine(owner) ==
        expectedCanInterruptSeated,
        $"only a confirmed click or true grab may interrupt a seated routine; got {owner}");
}

Expect(
    PetVisualTransitionPolicy.IsForcedInteraction(PetVisualOwner.CuteAngryReaction) &&
    !PetVisualTransitionPolicy.CanInterruptSeatedRoutine(PetVisualOwner.CuteAngryReaction),
    "cute-angry may atomically follow an interaction but must never tear down a seated routine");
Expect(
    !PetVisualTransitionPolicy.DoesTemporaryMoodBlockCuteAngryReaction(
        allowClickFinalFrame: true,
        isTemporaryMoodActive: false,
        hasLiveTemporaryRequest: true,
        liveTemporaryRequestIsQueued: true) &&
    PetVisualTransitionPolicy.DoesTemporaryMoodBlockCuteAngryReaction(
        allowClickFinalFrame: false,
        isTemporaryMoodActive: false,
        hasLiveTemporaryRequest: true,
        liveTemporaryRequestIsQueued: true) &&
    PetVisualTransitionPolicy.DoesTemporaryMoodBlockCuteAngryReaction(
        allowClickFinalFrame: true,
        isTemporaryMoodActive: true,
        hasLiveTemporaryRequest: true,
        liveTemporaryRequestIsQueued: true) &&
    !PetVisualTransitionPolicy.DoesTemporaryMoodBlockCuteAngryReaction(
        allowClickFinalFrame: true,
        isTemporaryMoodActive: false,
        hasLiveTemporaryRequest: false,
        liveTemporaryRequestIsQueued: false),
    "a queued ordinary temporary request must wait behind a confirmed click's forced cute-angry chain");
Expect(
    PetVisualTransitionPolicy.CanReceiveGrabRelease(PetVisualOwner.Persistent) &&
    PetVisualTransitionPolicy.CanReceiveGrabRelease(PetVisualOwner.DragLanding) &&
    PetVisualTransitionPolicy.CanReceiveGrabRelease(PetVisualOwner.CuteAngryReaction) &&
    !PetVisualTransitionPolicy.CanReceiveGrabRelease(PetVisualOwner.Temporary) &&
    !PetVisualTransitionPolicy.CanReceiveGrabRelease(PetVisualOwner.ClickFlickFall),
    "a real grab may release atomically only to persistent truth or the dedicated cute-angry reaction");

Expect(
    PetVisualTransitionQueuePolicy.DecideRequest(
        PetVisualOwner.Persistent,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: false,
        isAtAnimationBoundary: false,
        pointerIsPressed: false) == PetNormalTransitionRequestDecision.Queue,
    "an ordinary request inside a running cycle must queue");
Expect(
    PetVisualTransitionQueuePolicy.DecideRequest(
        PetVisualOwner.Persistent,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: false,
        isAtAnimationBoundary: true,
        pointerIsPressed: false) == PetNormalTransitionRequestDecision.ApplyNow,
    "an ordinary request may apply at a real unpressed boundary");
Expect(
    PetVisualTransitionQueuePolicy.DecideRequest(
        PetVisualOwner.Persistent,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: false,
        isAtAnimationBoundary: true,
        pointerIsPressed: true) == PetNormalTransitionRequestDecision.Queue,
    "a press must retain an ordinary boundary request until gesture resolution");
Expect(
    PetVisualTransitionQueuePolicy.DecideAmbientCuteAngryRequest(
        PetVisualOwner.Persistent,
        isAtAnimationBoundary: false,
        pointerIsPressed: false) == PetNormalTransitionRequestDecision.Queue &&
    PetVisualTransitionQueuePolicy.DecideAmbientCuteAngryRequest(
        PetVisualOwner.Persistent,
        isAtAnimationBoundary: true,
        pointerIsPressed: false) == PetNormalTransitionRequestDecision.ApplyNow &&
    PetVisualTransitionQueuePolicy.DecideAmbientCuteAngryRequest(
        PetVisualOwner.Persistent,
        isAtAnimationBoundary: true,
        pointerIsPressed: true) == PetNormalTransitionRequestDecision.Queue &&
    PetVisualTransitionQueuePolicy.DecideAmbientCuteAngryRequest(
        PetVisualOwner.Temporary,
        isAtAnimationBoundary: true,
        pointerIsPressed: false) == PetNormalTransitionRequestDecision.Reject,
    "ambient multitask anger must wait for a real unpressed idle boundary before the reaction owner starts");
foreach (var interactionOwner in new[]
         {
             PetVisualOwner.ClickFlickFall,
             PetVisualOwner.CuteAngryReaction,
             PetVisualOwner.Grab
         })
{
    Expect(
        PetVisualTransitionQueuePolicy.DecideRequest(
            interactionOwner,
            PetVisualOwner.Temporary,
            clickFlickFallReserved: interactionOwner == PetVisualOwner.ClickFlickFall,
            isAtAnimationBoundary: true,
            pointerIsPressed: false) == PetNormalTransitionRequestDecision.Queue,
        $"an ordinary request must wait behind {interactionOwner}, not disappear");
}
Expect(
    PetVisualTransitionQueuePolicy.DecidePendingAtBoundary(
        PetVisualOwner.CuteAngryReaction,
        PetVisualOwner.Persistent,
        clickFlickFallReserved: false,
        pointerIsPressed: false,
        cancellationRequested: false) == PetPendingTransitionDecision.Hold,
    "persistent truth must wait behind the complete cute-angry one-shot");

Expect(
    PetCuteAngryTriggerPolicy.ShouldTrigger(
        PetCuteAngryTriggerSource.ClickCompleted,
        0.799999) &&
    !PetCuteAngryTriggerPolicy.ShouldTrigger(
        PetCuteAngryTriggerSource.ClickCompleted,
        0.80) &&
    PetCuteAngryTriggerPolicy.ShouldTrigger(
        PetCuteAngryTriggerSource.GrabReleased,
        0.699999) &&
    !PetCuteAngryTriggerPolicy.ShouldTrigger(
        PetCuteAngryTriggerSource.GrabReleased,
        0.70) &&
    PetCuteAngryTriggerPolicy.ShouldTrigger(
        PetCuteAngryTriggerSource.MultiTaskIncrease,
        0.149999) &&
    !PetCuteAngryTriggerPolicy.ShouldTrigger(
        PetCuteAngryTriggerSource.MultiTaskIncrease,
        0.15),
    "cute-angry trigger probabilities must remain click=80%, grab=70%, multitask=15%");
Expect(
    PetCuteAngryTriggerPolicy.IsForcedInteractionChain(
        PetCuteAngryTriggerSource.ClickCompleted) &&
    PetCuteAngryTriggerPolicy.IsForcedInteractionChain(
        PetCuteAngryTriggerSource.GrabReleased) &&
    !PetCuteAngryTriggerPolicy.IsForcedInteractionChain(
        PetCuteAngryTriggerSource.MultiTaskIncrease),
    "only click/grab reaction chains may bypass the ordinary animation boundary queue");
Expect(
    !PetCuteAngryTriggerPolicy.IsMultiTaskIncrease(-1, 3) &&
    !PetCuteAngryTriggerPolicy.IsMultiTaskIncrease(1, 1) &&
    PetCuteAngryTriggerPolicy.IsMultiTaskIncrease(1, 2) &&
    PetCuteAngryTriggerPolicy.IsMultiTaskIncrease(2, 3),
    "multitask anger must only sample on a known rise to more than one active task");
Expect(
    PetCuteAngryTriggerPolicy.CalculateMultiTaskCooldown(0) ==
        PetCuteAngryTriggerPolicy.MinimumMultiTaskCooldown &&
    PetCuteAngryTriggerPolicy.CalculateMultiTaskCooldown(1) ==
        PetCuteAngryTriggerPolicy.MaximumMultiTaskCooldown &&
    PetCuteAngryTriggerPolicy.MinimumMultiTaskCooldown == TimeSpan.FromSeconds(60) &&
    PetCuteAngryTriggerPolicy.MaximumMultiTaskCooldown == TimeSpan.FromSeconds(90),
    "multitask anger cooldown must remain within 60-90 seconds");
Expect(
    PetVisualTransitionQueuePolicy.DecideRequest(
        PetVisualOwner.Persistent,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: true,
        isAtAnimationBoundary: true,
        pointerIsPressed: false) == PetNormalTransitionRequestDecision.Queue,
    "an ordinary request must survive a confirmed click while frames prepare");

Expect(
    PetVisualTransitionQueuePolicy.DecidePendingAtBoundary(
        PetVisualOwner.Persistent,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: false,
        pointerIsPressed: false,
        cancellationRequested: true) == PetPendingTransitionDecision.DiscardCanceled,
    "a canceled pending request must be discarded exactly at drain time");
Expect(
    PetVisualTransitionQueuePolicy.DecidePendingAtBoundary(
        PetVisualOwner.Persistent,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: false,
        pointerIsPressed: true,
        cancellationRequested: false) == PetPendingTransitionDecision.Hold,
    "a pressed pointer must hold a pending request");
Expect(
    PetVisualTransitionQueuePolicy.DecidePendingAtBoundary(
        PetVisualOwner.ClickFlickFall,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: true,
        pointerIsPressed: false,
        cancellationRequested: false) == PetPendingTransitionDecision.Hold,
    "an active click must hold, not drop, the next ordinary request");
Expect(
    PetVisualTransitionQueuePolicy.DecidePendingAtBoundary(
        PetVisualOwner.Persistent,
        PetVisualOwner.Temporary,
        clickFlickFallReserved: false,
        pointerIsPressed: false,
        cancellationRequested: false) == PetPendingTransitionDecision.Apply,
    "a live pending request must apply once ownership and pointer gates clear");

Expect(
    PetAnimationPlaybackPolicy.ClassifyAdvance(
        currentFrameIndex: 0,
        frameCount: 3,
        loop: true) == PetAnimationFrameAdvance.NextFrame,
    "an interior frame must advance without opening a transition boundary");
Expect(
    PetAnimationPlaybackPolicy.ClassifyAdvance(
        currentFrameIndex: 2,
        frameCount: 3,
        loop: true) == PetAnimationFrameAdvance.LoopBoundary,
    "the tick after a loop's visible last frame must open the loop boundary");
Expect(
    PetAnimationPlaybackPolicy.ClassifyAdvance(
        currentFrameIndex: 2,
        frameCount: 3,
        loop: false) == PetAnimationFrameAdvance.OneShotBoundary,
    "the tick after a one-shot's visible last frame must open its terminal boundary");
Expect(
    !PetAnimationPlaybackPolicy.IsReadyForNormalTransition(
        frameIndex: 2,
        frameCount: 3,
        timerIsEnabled: true,
        hasVisibleFrame: true),
    "displaying the final frame is not yet permission to cut a running animation");
Expect(
    PetAnimationPlaybackPolicy.IsReadyForNormalTransition(
        frameIndex: 3,
        frameCount: 3,
        timerIsEnabled: true,
        hasVisibleFrame: true),
    "a loop wrap tick must admit the queued normal transition");
Expect(
    PetAnimationPlaybackPolicy.IsReadyForNormalTransition(
        frameIndex: 2,
        frameCount: 3,
        timerIsEnabled: false,
        hasVisibleFrame: true),
    "a stopped one-shot final frame must admit the queued normal transition");
Expect(
    PetAnimationPlaybackPolicy.ClassifyAdvance(
        currentFrameIndex: 0,
        frameCount: 1,
        loop: true) == PetAnimationFrameAdvance.LoopBoundary &&
    PetAnimationPlaybackPolicy.ClassifyAdvance(
        currentFrameIndex: 0,
        frameCount: 1,
        loop: false) == PetAnimationFrameAdvance.OneShotBoundary,
    "a one-frame sequence must still publish the correct real boundary");
Expect(
    !PetAnimationPlaybackPolicy.ShouldStopAtRequestedBoundary(4) &&
    PetAnimationPlaybackPolicy.ConsumeRequestedBoundary(4) == 3 &&
    !PetAnimationPlaybackPolicy.ShouldStopAtRequestedBoundary(2) &&
    PetAnimationPlaybackPolicy.ConsumeRequestedBoundary(2) == 1 &&
    PetAnimationPlaybackPolicy.ShouldStopAtRequestedBoundary(1) &&
    PetAnimationPlaybackPolicy.ConsumeRequestedBoundary(1) == 0,
    "a four-cycle preview waiter must stop only after consuming its fourth real wrap");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimationPlaybackPolicy.ShouldStopAtRequestedBoundary(0),
    "boundary countdown accepted an empty request");
Expect(
    PetAnimationPlaybackPolicy.IsReadyForNormalTransition(
        frameIndex: 0,
        frameCount: 1,
        timerIsEnabled: true,
        hasVisibleFrame: false),
    "startup without a visible frame may transition immediately");

Expect(
    PetFollowMovementPolicy.CalculateDelay(
        PetFollowScheduleKind.Initial,
        0) == TimeSpan.FromSeconds(90) &&
    PetFollowMovementPolicy.CalculateDelay(
        PetFollowScheduleKind.Initial,
        1) == TimeSpan.FromSeconds(150) &&
    PetFollowMovementPolicy.CalculateDelay(
        PetFollowScheduleKind.Recurring,
        0) == TimeSpan.FromSeconds(180) &&
    PetFollowMovementPolicy.CalculateDelay(
        PetFollowScheduleKind.Recurring,
        1) == TimeSpan.FromSeconds(300) &&
    PetFollowMovementPolicy.CalculateDelay(
        PetFollowScheduleKind.Retry,
        0) == TimeSpan.FromSeconds(45) &&
    PetFollowMovementPolicy.CalculateDelay(
        PetFollowScheduleKind.Retry,
        1) == TimeSpan.FromSeconds(90),
    "follow opportunities must use the low-frequency initial, recurring, and retry ranges");
Expect(
    PetFollowMovementPolicy.SelectPreferredTarget(0) ==
        PetFollowTargetKind.Cursor &&
    PetFollowMovementPolicy.SelectPreferredTarget(0.6999) ==
        PetFollowTargetKind.Cursor &&
    PetFollowMovementPolicy.SelectPreferredTarget(0.70) ==
        PetFollowTargetKind.WorkAreaCenter &&
    PetFollowMovementPolicy.SelectPreferredTarget(1) ==
        PetFollowTargetKind.WorkAreaCenter,
    "follow target selection must use the exact 70/30 cursor/center threshold");

var followDeadline = PetFollowMovementPolicy.CalculateDeadline(
    10_000,
    TimeSpan.FromSeconds(90));
Expect(
    followDeadline == 100_000 &&
    !PetFollowMovementPolicy.IsDeadlineDue(99_999, followDeadline) &&
    PetFollowMovementPolicy.IsDeadlineDue(100_000, followDeadline) &&
    !PetFollowMovementPolicy.IsDeadlineDue(
        long.MaxValue,
        PetFollowMovementPolicy.UnscheduledDeadline) &&
    PetFollowMovementPolicy.PreserveScheduledDeadline(
        42_000,
        90_000) == 42_000 &&
    PetFollowMovementPolicy.PreserveScheduledDeadline(
        PetFollowMovementPolicy.UnscheduledDeadline,
        90_000) == 90_000,
    "follow deadlines must remain stable across temporary blockers and fire at their exact due time");

var primaryWorkArea = new PetFollowBounds(0, 0, 1920, 1040);
var restingPet = new PetFollowBounds(100, 100, 242, 276);
var primaryCursor = new PetFollowPoint(1600, 800);
Expect(
    PetFollowMovementPolicy.TryCreatePlan(
        PetFollowTargetKind.Cursor,
        restingPet,
        primaryCursor,
        [primaryWorkArea],
        dpiScale: 1,
        currentFacingDirection: 1,
        out var primaryCursorPlan) &&
    primaryCursorPlan.TargetKind == PetFollowTargetKind.Cursor &&
    primaryCursorPlan.WorkArea == primaryWorkArea &&
    primaryCursorPlan.FacingDirection == 1 &&
    Math.Abs(
        primaryCursor.X -
        (primaryCursorPlan.TargetTopLeft.X + restingPet.Width) -
        PetFollowMovementPolicy.CursorGapDips) < 0.000001 &&
    primaryCursorPlan.TargetTopLeft.X >=
        primaryWorkArea.Left + PetFollowMovementPolicy.WorkAreaMarginDips &&
    primaryCursorPlan.TargetTopLeft.Y >=
        primaryWorkArea.Top + PetFollowMovementPolicy.WorkAreaMarginDips &&
    primaryCursorPlan.TargetTopLeft.X + restingPet.Width <=
        primaryWorkArea.Right - PetFollowMovementPolicy.WorkAreaMarginDips &&
    primaryCursorPlan.TargetTopLeft.Y + restingPet.Height <=
        primaryWorkArea.Bottom - PetFollowMovementPolicy.WorkAreaMarginDips,
    "a cursor plan must stop toward the work-area center with a 24-DIP gap and safe margins");

var centerTarget = PetFollowMovementPolicy.CalculateTargetTopLeft(
    PetFollowTargetKind.WorkAreaCenter,
    restingPet.Width,
    restingPet.Height,
    new PetFollowPoint(20, 20),
    primaryWorkArea,
    dpiScale: 1);
Expect(
    Math.Abs(centerTarget.X - 839) < 0.000001 &&
    Math.Abs(centerTarget.Y - 382) < 0.000001,
    "the screen-center destination must use the selected monitor's usable work-area center");

var negativeWorkArea = new PetFollowBounds(-1920, -120, 1920, 1040);
Expect(
    PetFollowMovementPolicy.TryCreatePlan(
        PetFollowTargetKind.WorkAreaCenter,
        restingPet,
        new PetFollowPoint(-1800, 100),
        [primaryWorkArea, negativeWorkArea],
        dpiScale: 1,
        currentFacingDirection: 1,
        out var negativeMonitorPlan) &&
    negativeMonitorPlan.WorkArea == negativeWorkArea &&
    negativeMonitorPlan.TargetTopLeft.X < 0 &&
    negativeMonitorPlan.TargetTopLeft.Y >= negativeWorkArea.Top +
        PetFollowMovementPolicy.WorkAreaMarginDips,
    "negative-coordinate secondary monitors must keep their own work area and center target");

var taskbarCursorTarget = PetFollowMovementPolicy.CalculateTargetTopLeft(
    PetFollowTargetKind.Cursor,
    restingPet.Width,
    restingPet.Height,
    new PetFollowPoint(960, 1075),
    primaryWorkArea,
    dpiScale: 1);
Expect(
    Math.Abs(
        taskbarCursorTarget.Y + restingPet.Height -
        (primaryWorkArea.Bottom - PetFollowMovementPolicy.WorkAreaMarginDips)) <
    0.000001,
    "cursor targeting must clamp above the taskbar-facing work-area boundary");

Expect(
    PetFollowMovementPolicy.TryCreatePlan(
        PetFollowTargetKind.Cursor,
        restingPet,
        new PetFollowPoint(150, 150),
        [primaryWorkArea],
        dpiScale: 1,
        currentFacingDirection: -1,
        out var nearCursorFallbackPlan) &&
    nearCursorFallbackPlan.TargetKind == PetFollowTargetKind.WorkAreaCenter,
    "a cursor inside the pet's 32-DIP expanded bounds must fall back to the work-area center");

var petAtCenter = new PetFollowBounds(
    centerTarget.X,
    centerTarget.Y,
    restingPet.Width,
    restingPet.Height);
Expect(
    !PetFollowMovementPolicy.TryCreatePlan(
        PetFollowTargetKind.Cursor,
        petAtCenter,
        new PetFollowPoint(
            petAtCenter.Left + (petAtCenter.Width / 2),
            petAtCenter.Top + (petAtCenter.Height / 2)),
        [primaryWorkArea],
        dpiScale: 1,
        currentFacingDirection: 1,
        out _),
    "a follow opportunity must be skipped when both cursor and center targets are too close");

var verticallySeparatedPet = new PetFollowBounds(
    centerTarget.X - 10,
    0,
    restingPet.Width,
    restingPet.Height);
Expect(
    PetFollowMovementPolicy.TryCreatePlan(
        PetFollowTargetKind.WorkAreaCenter,
        verticallySeparatedPet,
        new PetFollowPoint(1800, 900),
        [primaryWorkArea],
        dpiScale: 1,
        currentFacingDirection: -1,
        out var verticalPlan) &&
    verticalPlan.TargetKind == PetFollowTargetKind.WorkAreaCenter &&
    verticalPlan.FacingDirection == -1,
    "a target within the 18-DIP horizontal deadzone must preserve the current facing direction");

var wideWorkArea = new PetFollowBounds(0, 0, 3000, 2000);
var widePet = new PetFollowBounds(400, 850, 1000, 300);
Expect(
    !PetFollowMovementPolicy.TryCreatePlan(
        PetFollowTargetKind.WorkAreaCenter,
        widePet,
        new PetFollowPoint(500, 900),
        [wideWorkArea],
        dpiScale: 1,
        currentFacingDirection: 1,
        out _),
    "minimum travel must be the greater of 180 scaled DIP and 65% of the pet window width");

var twoDimensionalStep = PetFollowMovementPolicy.AdvanceToward(
    new PetFollowPoint(0, 0),
    new PetFollowPoint(3, 4),
    maximumStep: 2);
var clampedStep = PetFollowMovementPolicy.AdvanceToward(
    new PetFollowPoint(0, 0),
    new PetFollowPoint(3, 4),
    maximumStep: 8);
Expect(
    Math.Abs(twoDimensionalStep.X - 1.2) < 0.000001 &&
    Math.Abs(twoDimensionalStep.Y - 1.6) < 0.000001 &&
    clampedStep == new PetFollowPoint(3, 4) &&
    !PetFollowMovementPolicy.HasArrived(
        new PetFollowPoint(0, 0),
        new PetFollowPoint(9, 0),
        dpiScale: 1) &&
    PetFollowMovementPolicy.HasArrived(
        new PetFollowPoint(0, 0),
        new PetFollowPoint(8, 0),
        dpiScale: 1),
    "two-dimensional follow movement must preserve direction, avoid overshoot, and use the 8-DIP arrival tolerance");

var walkingLoopDuration = TimeSpan.FromMilliseconds(8 * 182);
const double followDistance = 1000;
var loopAlignedSpeed = PetFollowMovementPolicy.CalculateLoopAlignedSpeed(
    followDistance,
    walkingLoopDuration,
    dpiScale: 1);
var alignedTravelSeconds = followDistance / loopAlignedSpeed;
var alignedCycleCount = alignedTravelSeconds / walkingLoopDuration.TotalSeconds;
var followWatchdog = PetFollowMovementPolicy.CalculateWatchdog(
    followDistance,
    loopAlignedSpeed,
    walkingLoopDuration);
Expect(
    loopAlignedSpeed > 0 &&
    Math.Abs(alignedCycleCount - Math.Round(alignedCycleCount)) < 0.000001 &&
    alignedTravelSeconds <=
        PetFollowMovementPolicy.MaximumTravelDuration.TotalSeconds &&
    Math.Abs(
        followWatchdog.TotalSeconds - alignedTravelSeconds -
        PetFollowMovementPolicy.WatchdogGrace.TotalSeconds) < 0.000001,
    "follow speed and watchdog must align travel with complete walking loops");

ExpectThrows<ArgumentOutOfRangeException>(
    () => PetFollowMovementPolicy.CalculateDelay(
        PetFollowScheduleKind.Initial,
        double.NaN),
    "follow scheduling must reject non-finite random samples");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetFollowMovementPolicy.TryCreatePlan(
        PetFollowTargetKind.Cursor,
        new PetFollowBounds(0, 0, -1, 276),
        new PetFollowPoint(100, 100),
        [primaryWorkArea],
        dpiScale: 1,
        currentFacingDirection: 1,
        out _),
    "follow planning must reject negative pet dimensions");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetFollowMovementPolicy.AdvanceToward(
        new PetFollowPoint(double.PositiveInfinity, 0),
        new PetFollowPoint(0, 0),
        maximumStep: 1),
    "follow movement must reject non-finite geometry");

foreach (var (mood, directory) in new[]
         {
             (PetMood.HeadPatHappy, "head-pat-happy-video"),
             (PetMood.TaskCelebrate, "task-celebrate-video"),
             (PetMood.StandingRamNibble, "standing-ram-nibble-video")
         })
{
    ExpectExternalOneShot(mood, directory, 121, PetAnimations.ExternalAnimationFrameDuration);
    Expect(PetAnimations.CalculatePreviewDuration(121, PetAnimations.GetExternalFrameDuration(mood)) <=
           PetAnimations.MaximumSingleAnimationDuration, "positive reactions must remain <= 6 seconds");
    Expect(PetAnimations.AnimationWarmUpMoods.Contains(mood) &&
           PetAnimations.AnimationPreviewOptions.Any(option => option.Mood == mood),
        "positive reactions must be preloaded and exposed in previews");
}
var celebration = new PetCelebrationPolicy();
Expect(celebration.ObserveCompletion("a:1", 0, 0.59, true), "eligible successful turn can celebrate");
celebration.MarkActivated(0);
Expect(!celebration.ObserveCompletion("a:1", 40000, 0, true), "same turn never repeats");
Expect(!celebration.ObserveCompletion("a:2", 1000, 0, true), "cooldown blocks another completion");
Expect(!celebration.ObserveCompletion("a:2", 40000, 0, true), "blocked event cannot replay later");
Expect(!celebration.ObserveCompletion("a:3", 40000, 0, false), "busy animations suppress celebration");
Expect(!celebration.ObserveCompletion("a:3", 50000, 0, true), "busy missed opportunity remains dropped");
Expect(!celebration.ObserveCompletion("a:4", 40000, 0.60, true), "probability upper boundary is excluded");
Expect(celebration.ObserveCompletion("a:5", 40000, 0, true), "new success after cooldown can celebrate");
Expect(!celebration.ObserveCompletion("a:6", 40000, double.NaN, true), "invalid random sample rejected");
Console.WriteLine("Runtime animation policy harness passed.");
