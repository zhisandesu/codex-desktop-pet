namespace XiaobianPet.Models;

internal static class PetRunDialoguePolicy
{
    public const double RunningEffortProbability = 0.60;

    public static bool ShouldSpeakRunningEffort(double randomSample)
    {
        if (double.IsNaN(randomSample) || randomSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }

        return randomSample < RunningEffortProbability;
    }
}
