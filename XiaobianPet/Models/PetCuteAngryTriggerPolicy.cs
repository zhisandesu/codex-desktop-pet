namespace XiaobianPet.Models;

public enum PetCuteAngryTriggerSource
{
    ClickCompleted,
    GrabReleased,
    MultiTaskIncrease
}

public static class PetCuteAngryTriggerPolicy
{
    public const double ClickCompletedProbability = 0.80;
    public const double GrabReleasedProbability = 0.70;
    public const double MultiTaskIncreaseProbability = 0.15;

    public static readonly TimeSpan MinimumMultiTaskCooldown =
        TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumMultiTaskCooldown =
        TimeSpan.FromSeconds(90);

    public static bool ShouldTrigger(
        PetCuteAngryTriggerSource source,
        double randomSample)
    {
        ValidateUnitSample(randomSample);
        var probability = source switch
        {
            PetCuteAngryTriggerSource.ClickCompleted => ClickCompletedProbability,
            PetCuteAngryTriggerSource.GrabReleased => GrabReleasedProbability,
            PetCuteAngryTriggerSource.MultiTaskIncrease => MultiTaskIncreaseProbability,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };
        return randomSample < probability;
    }

    public static bool IsForcedInteractionChain(PetCuteAngryTriggerSource source) =>
        source switch
        {
            PetCuteAngryTriggerSource.ClickCompleted => true,
            PetCuteAngryTriggerSource.GrabReleased => true,
            PetCuteAngryTriggerSource.MultiTaskIncrease => false,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };

    public static bool IsMultiTaskIncrease(int previousActiveCount, int activeCount)
    {
        if (previousActiveCount < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(previousActiveCount));
        }

        if (activeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeCount));
        }

        return previousActiveCount >= 0 &&
            activeCount > 1 &&
            activeCount > previousActiveCount;
    }

    public static TimeSpan CalculateMultiTaskCooldown(double randomSample)
    {
        ValidateUnitSample(randomSample);
        var range = MaximumMultiTaskCooldown - MinimumMultiTaskCooldown;
        return MinimumMultiTaskCooldown + TimeSpan.FromTicks(
            (long)Math.Round(range.Ticks * randomSample));
    }

    private static void ValidateUnitSample(double randomSample)
    {
        if (!double.IsFinite(randomSample) || randomSample < 0 || randomSample > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }
    }
}
