namespace XiaobianPet.Models;

public enum PetVisualOwner
{
    Persistent,
    IdleGesture,
    Autonomous,
    Temporary,
    SeatedRoutine,
    ClickInteraction,
    ClickFlickFall,
    CuteAngryBoundaryRequest,
    CuteAngryReaction,
    Grab,
    DragLanding,
    WorkloadRoutine
}

public enum PetVisualTransitionTiming
{
    AnimationBoundary,
    Immediate
}

public static class PetVisualTransitionPolicy
{
    public static bool IsForcedInteraction(PetVisualOwner requestedOwner) =>
        requestedOwner is PetVisualOwner.ClickInteraction or
            PetVisualOwner.ClickFlickFall or
            PetVisualOwner.CuteAngryReaction or
            PetVisualOwner.DragLanding or
            PetVisualOwner.Grab;

    // A confirmed click and a true grab are both explicit interaction outcomes.
    // They may replace a seated pose immediately; ordinary transitions still
    // request StandUp and wait for its real final frame.
    public static bool CanInterruptSeatedRoutine(PetVisualOwner requestedOwner) =>
        requestedOwner is PetVisualOwner.ClickInteraction or
            PetVisualOwner.ClickFlickFall or
            PetVisualOwner.Grab;

    public static bool CanReceiveGrabRelease(PetVisualOwner requestedOwner) =>
        requestedOwner is PetVisualOwner.Persistent or
            PetVisualOwner.DragLanding or
            PetVisualOwner.CuteAngryReaction;

    public static bool DoesTemporaryMoodBlockCuteAngryReaction(
        bool allowClickFinalFrame,
        bool isTemporaryMoodActive,
        bool hasLiveTemporaryRequest,
        bool liveTemporaryRequestIsQueued)
    {
        if (isTemporaryMoodActive)
        {
            return true;
        }

        if (!hasLiveTemporaryRequest)
        {
            return false;
        }

        // A confirmed click already owns the screen. Its optional follow-up
        // reaction stays ahead of an ordinary request that the click preserved;
        // that request remains queued and drains after the reaction's final frame.
        return !allowClickFinalFrame || !liveTemporaryRequestIsQueued;
    }

    public static PetVisualTransitionTiming GetTiming(PetVisualOwner requestedOwner) =>
        IsForcedInteraction(requestedOwner)
            ? PetVisualTransitionTiming.Immediate
            : PetVisualTransitionTiming.AnimationBoundary;

    public static bool CanReplace(PetVisualOwner currentOwner, PetVisualOwner requestedOwner) =>
        currentOwner switch
        {
            PetVisualOwner.WorkloadRoutine =>
                requestedOwner is PetVisualOwner.WorkloadRoutine or PetVisualOwner.Grab,
            PetVisualOwner.DragLanding =>
                requestedOwner is PetVisualOwner.DragLanding or PetVisualOwner.Grab,
            PetVisualOwner.ClickFlickFall =>
                requestedOwner is PetVisualOwner.ClickFlickFall or PetVisualOwner.Grab,
            PetVisualOwner.CuteAngryReaction =>
                requestedOwner is PetVisualOwner.CuteAngryReaction or PetVisualOwner.Grab,
            PetVisualOwner.Grab => requestedOwner == PetVisualOwner.Grab,
            _ => true
        };

    public static bool CanReplace(
        PetVisualOwner currentOwner,
        PetVisualOwner requestedOwner,
        bool clickFlickFallReserved) =>
        (!clickFlickFallReserved ||
         requestedOwner is PetVisualOwner.ClickFlickFall or PetVisualOwner.Grab ||
         currentOwner == PetVisualOwner.SeatedRoutine &&
         requestedOwner == PetVisualOwner.SeatedRoutine) &&
        CanReplace(currentOwner, requestedOwner);
}
