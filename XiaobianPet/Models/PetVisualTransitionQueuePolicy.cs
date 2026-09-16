namespace XiaobianPet.Models;

public enum PetNormalTransitionRequestDecision
{
    Reject,
    ApplyNow,
    Queue
}

public enum PetPendingTransitionDecision
{
    DiscardCanceled,
    Hold,
    Apply
}

public static class PetVisualTransitionQueuePolicy
{
    public static PetNormalTransitionRequestDecision DecideAmbientCuteAngryRequest(
        PetVisualOwner currentOwner,
        bool isAtAnimationBoundary,
        bool pointerIsPressed)
    {
        if (currentOwner != PetVisualOwner.Persistent)
        {
            return PetNormalTransitionRequestDecision.Reject;
        }

        return isAtAnimationBoundary && !pointerIsPressed
            ? PetNormalTransitionRequestDecision.ApplyNow
            : PetNormalTransitionRequestDecision.Queue;
    }

    public static PetNormalTransitionRequestDecision DecideRequest(
        PetVisualOwner currentOwner,
        PetVisualOwner requestedOwner,
        bool clickFlickFallReserved,
        bool isAtAnimationBoundary,
        bool pointerIsPressed)
    {
        if (PetVisualTransitionPolicy.GetTiming(requestedOwner) !=
            PetVisualTransitionTiming.AnimationBoundary)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedOwner));
        }

        var canReplaceCurrent = PetVisualTransitionPolicy.CanReplace(
            currentOwner,
            requestedOwner,
            clickFlickFallReserved);
        var canWaitForForcedInteractionRelease =
            currentOwner is PetVisualOwner.ClickFlickFall or
                PetVisualOwner.DragLanding or
                PetVisualOwner.CuteAngryReaction or
                PetVisualOwner.Grab ||
            clickFlickFallReserved;
        if (!canReplaceCurrent && !canWaitForForcedInteractionRelease)
        {
            return PetNormalTransitionRequestDecision.Reject;
        }

        return canReplaceCurrent && isAtAnimationBoundary && !pointerIsPressed
            ? PetNormalTransitionRequestDecision.ApplyNow
            : PetNormalTransitionRequestDecision.Queue;
    }

    public static PetPendingTransitionDecision DecidePendingAtBoundary(
        PetVisualOwner currentOwner,
        PetVisualOwner pendingOwner,
        bool clickFlickFallReserved,
        bool pointerIsPressed,
        bool cancellationRequested)
    {
        if (cancellationRequested)
        {
            return PetPendingTransitionDecision.DiscardCanceled;
        }

        if (pointerIsPressed ||
            !PetVisualTransitionPolicy.CanReplace(
                currentOwner,
                pendingOwner,
                clickFlickFallReserved))
        {
            return PetPendingTransitionDecision.Hold;
        }

        return PetPendingTransitionDecision.Apply;
    }
}
