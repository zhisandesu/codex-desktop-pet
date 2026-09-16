namespace XiaobianPet.Models;

/// <summary>Low-frequency, idle-only opportunities; time is monotonic.</summary>
public sealed class RecycleBinReactionPolicy
{
    public const long MinimumIntervalMilliseconds = 10 * 60 * 1000;
    private long? _nextAutomaticAt;

    public bool TryReserveAutomatic(long now, bool canPresent, double randomSample)
    {
        if (_nextAutomaticAt is null)
        {
            _nextAutomaticAt = now + MinimumIntervalMilliseconds;
            return false;
        }
        if (!canPresent || now < _nextAutomaticAt) return false;
        MarkPresented(now, randomSample);
        return true;
    }

    public void MarkPresented(long now, double randomSample)
    {
        if (!double.IsFinite(randomSample)) randomSample = .5;
        // No burst after a long busy period; manual checks postpone automatic
        // ones too. Add at most three minutes of jitter for a less regular rhythm.
        _nextAutomaticAt = now + MinimumIntervalMilliseconds +
            (long)(Math.Clamp(randomSample, 0, 1) * 3 * 60 * 1000);
    }
}
