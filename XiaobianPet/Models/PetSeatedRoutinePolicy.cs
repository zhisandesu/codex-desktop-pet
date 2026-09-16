namespace XiaobianPet.Models;

public enum PetIdleBoundaryAction
{
    None,
    IdleGesture,
    SeatedRoutine,
    AutonomousMovement
}

public enum PetSeatedRoutineScheduleKind
{
    Initial,
    Recurring
}

public static class PetSeatedRoutinePolicy
{
    public const long UnscheduledDeadline = long.MaxValue;
    public const double MinimumInitialDelayMilliseconds = 8000;
    public const double MaximumInitialDelayMilliseconds = 15000;
    public const double MinimumRecurringDelayMilliseconds = 55000;
    public const double MaximumRecurringDelayMilliseconds = 85000;
    public const double MinimumVisitDurationMilliseconds = 60_000;
    public const double MaximumVisitDurationMilliseconds = 180_000;
    public const double MinimumGestureOpportunityDelayMilliseconds = 8_000;
    public const double MaximumGestureOpportunityDelayMilliseconds = 24_000;
    public const double GestureOpportunityProbability = 0.65;

    public static bool IsAmbientIdleMood(PetMood mood) =>
        // Failed also uses the approved idle video. Keep the error status, but
        // do not turn a connection/task failure into a permanent animation lock.
        (mood is PetMood.Idle or PetMood.Working or PetMood.Review or PetMood.Waiting or PetMood.Failed) &&
        PetAnimations.ResolveVisualMood(mood) == PetMood.Idle;

    public static bool CanRunSeatedRoutine(
        PetMood persistentMood,
        PetMood currentMood) =>
        IsAmbientIdleMood(persistentMood) &&
        IsAmbientIdleMood(currentMood);

    public static TimeSpan CalculateDelay(
        PetSeatedRoutineScheduleKind kind,
        double randomSample)
    {
        if (!double.IsFinite(randomSample) || randomSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }

        var (minimum, maximum) = kind switch
        {
            PetSeatedRoutineScheduleKind.Initial =>
                (MinimumInitialDelayMilliseconds, MaximumInitialDelayMilliseconds),
            PetSeatedRoutineScheduleKind.Recurring =>
                (MinimumRecurringDelayMilliseconds, MaximumRecurringDelayMilliseconds),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return TimeSpan.FromMilliseconds(
            minimum + (randomSample * (maximum - minimum)));
    }

    public static PetMood SelectGesture(
        double randomSample,
        PetMood? previousGesture = null)
    {
        ValidateRandomSample(randomSample);
        if (previousGesture is not null &&
            !PetAnimations.IsSeatedGestureMood(previousGesture.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(previousGesture));
        }

        (PetMood Mood, double Weight)[] weightedGestures =
        [
            (PetMood.SeatedBlink, 0.32),
            (PetMood.SeatedHeadTilt, 0.24),
            (PetMood.SeatedChinShake, 0.16),
            (PetMood.SeatedCoverHead, 0.08),
            (PetMood.SeatedDoubleCheekCute, 0.20)
        ];
        var availableWeight = weightedGestures
            .Where(item => item.Mood != previousGesture)
            .Sum(item => item.Weight);
        var target = randomSample * availableWeight;
        var accumulated = 0d;
        foreach (var (mood, weight) in weightedGestures)
        {
            if (mood == previousGesture)
            {
                continue;
            }

            accumulated += weight;
            if (target < accumulated && accumulated - target > 1e-12)
            {
                return mood;
            }
        }

        return weightedGestures.Last(item => item.Mood != previousGesture).Mood;
    }

    public static TimeSpan CalculateVisitDuration(double randomSample) =>
        CalculateDuration(
            randomSample,
            MinimumVisitDurationMilliseconds,
            MaximumVisitDurationMilliseconds);

    public static TimeSpan CalculateGestureOpportunityDelay(double randomSample) =>
        CalculateDuration(
            randomSample,
            MinimumGestureOpportunityDelayMilliseconds,
            MaximumGestureOpportunityDelayMilliseconds);

    public static bool ShouldPlayGestureOpportunity(double randomSample)
    {
        ValidateRandomSample(randomSample);
        return randomSample < GestureOpportunityProbability;
    }

    public static bool ShouldReleaseIdleLoopAtBoundary(
        bool exitRequested,
        long nowMilliseconds,
        long deadline) =>
        exitRequested || IsDeadlineDue(nowMilliseconds, deadline);

    public static long CalculateDeadline(long nowMilliseconds, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        return checked(
            nowMilliseconds + (long)Math.Round(delay.TotalMilliseconds));
    }

    public static long PreserveScheduledDeadline(
        long currentDeadline,
        long candidateDeadline) =>
        currentDeadline == UnscheduledDeadline
            ? candidateDeadline
            : currentDeadline;

    public static bool IsDeadlineDue(long nowMilliseconds, long deadline) =>
        deadline != UnscheduledDeadline && nowMilliseconds >= deadline;

    public static PetIdleBoundaryAction SelectBoundaryAction(
        PetIdleBoundaryAction pendingAction,
        bool seatedDeadlineDue,
        bool canStartSeatedRoutine,
        bool idleGestureDue,
        bool autonomousActionDue,
        bool canStartOrdinaryIdleAction)
    {
        if (pendingAction == PetIdleBoundaryAction.SeatedRoutine ||
            seatedDeadlineDue && canStartSeatedRoutine)
        {
            return PetIdleBoundaryAction.SeatedRoutine;
        }

        if (pendingAction == PetIdleBoundaryAction.AutonomousMovement)
        {
            return pendingAction;
        }

        // A quiet gesture may have become pending early in the current Idle
        // loop. Once a rarer walk/run/follow deadline becomes due, replace the
        // not-yet-visible gesture so movement cannot be starved forever.
        if (autonomousActionDue && canStartOrdinaryIdleAction)
        {
            return PetIdleBoundaryAction.AutonomousMovement;
        }

        if (pendingAction != PetIdleBoundaryAction.None)
        {
            return pendingAction;
        }

        if (idleGestureDue && canStartOrdinaryIdleAction)
        {
            return PetIdleBoundaryAction.IdleGesture;
        }

        return PetIdleBoundaryAction.None;
    }

    private static TimeSpan CalculateDuration(
        double randomSample,
        double minimumMilliseconds,
        double maximumMilliseconds)
    {
        ValidateRandomSample(randomSample);
        return TimeSpan.FromMilliseconds(
            minimumMilliseconds +
            (randomSample * (maximumMilliseconds - minimumMilliseconds)));
    }

    private static void ValidateRandomSample(double randomSample)
    {
        if (!double.IsFinite(randomSample) || randomSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }
    }
}

public static class PetMessageFeedbackPolicy
{
    public const double TaskStartedProbability = 0.40;
    public const double TaskCompletedProbability = 0.50;
    public const double TaskProblemProbability = 0.30;
    public const double GeneralSpeechProbability = 0.20;
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    public static double ProbabilityForIntent(PetDialogueIntent intent) =>
        intent switch
        {
            PetDialogueIntent.TaskStarted or PetDialogueIntent.TaskOverloaded =>
                TaskStartedProbability,
            PetDialogueIntent.TaskCompleted or
                PetDialogueIntent.TaskCompletedStillBusy =>
                TaskCompletedProbability,
            PetDialogueIntent.ApprovalRequired or
                PetDialogueIntent.TaskInterrupted or
                PetDialogueIntent.TaskFailed =>
                TaskProblemProbability,
            PetDialogueIntent.PetClicked or
                PetDialogueIntent.PetClickedWhileBusy or
                PetDialogueIntent.HeadFlicked or
                PetDialogueIntent.RunningEffort or
                PetDialogueIntent.PetDoubleClicked or
                PetDialogueIntent.PetRightClicked or
                PetDialogueIntent.DragStarted or
                PetDialogueIntent.Dragging or
                PetDialogueIntent.DragDropped or
                PetDialogueIntent.WingGrabStarted or
                PetDialogueIntent.WingGrabHolding or
                PetDialogueIntent.WingGrabReleased or
                PetDialogueIntent.HeadGrabStarted or
                PetDialogueIntent.HeadGrabHolding or
                PetDialogueIntent.HeadGrabReleased or
                PetDialogueIntent.BodyGrabStarted or
                PetDialogueIntent.BodyGrabHolding or
                PetDialogueIntent.BodyGrabReleased => 0,
            _ => GeneralSpeechProbability
        };

    public static bool ShouldSelect(double probability, double randomSample)
    {
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability));
        }

        if (!double.IsFinite(randomSample) || randomSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }

        return randomSample < probability;
    }

    public static bool CanOpenOpportunity(
        bool speechWasShown,
        bool seatedRoutineActive,
        bool forcedInteractionActive,
        bool temporaryAnimationActive,
        long nowMilliseconds,
        long availableAtMilliseconds) =>
        speechWasShown &&
        !seatedRoutineActive &&
        !forcedInteractionActive &&
        !temporaryAnimationActive &&
        nowMilliseconds >= availableAtMilliseconds;

    public static bool IsSpeechBubbleActive(
        string? activeSemanticKey,
        bool hideScheduleActive,
        long speechSequence) =>
        hideScheduleActive &&
        string.Equals(
            activeSemanticKey,
            $"pet-speech:{speechSequence}",
            StringComparison.Ordinal);

    public static long CalculateLatestStartDeadline(
        long shownAtMilliseconds,
        TimeSpan visibleDuration,
        TimeSpan animationDuration)
    {
        if (visibleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleDuration));
        }

        if (animationDuration <= TimeSpan.Zero ||
            animationDuration > visibleDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(animationDuration));
        }

        return checked(
            shownAtMilliseconds +
            (long)Math.Floor(
                (visibleDuration - animationDuration).TotalMilliseconds));
    }
}
