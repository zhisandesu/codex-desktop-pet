namespace XiaobianPet.Models;

public enum PetAnimationFrameAdvance
{
    NextFrame,
    LoopBoundary,
    OneShotBoundary
}

public static class PetAnimationPlaybackPolicy
{
    public static bool ShouldStopAtRequestedBoundary(int remainingBoundaryCount)
    {
        if (remainingBoundaryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingBoundaryCount));
        }

        return remainingBoundaryCount == 1;
    }

    public static int ConsumeRequestedBoundary(int remainingBoundaryCount)
    {
        if (remainingBoundaryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingBoundaryCount));
        }

        return remainingBoundaryCount - 1;
    }

    public static PetAnimationFrameAdvance ClassifyAdvance(
        int currentFrameIndex,
        int frameCount,
        bool loop)
    {
        if (frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (currentFrameIndex < 0 || currentFrameIndex >= frameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(currentFrameIndex));
        }

        if (currentFrameIndex + 1 < frameCount)
        {
            return PetAnimationFrameAdvance.NextFrame;
        }

        return loop
            ? PetAnimationFrameAdvance.LoopBoundary
            : PetAnimationFrameAdvance.OneShotBoundary;
    }

    public static bool IsReadyForNormalTransition(
        int frameIndex,
        int frameCount,
        bool timerIsEnabled,
        bool hasVisibleFrame)
    {
        if (!hasVisibleFrame)
        {
            return true;
        }

        if (frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (frameIndex >= frameCount)
        {
            return true;
        }

        return !timerIsEnabled && frameIndex >= frameCount - 1;
    }
}
