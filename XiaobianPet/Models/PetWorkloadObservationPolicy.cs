namespace XiaobianPet.Models;

public enum PetWorkloadTier
{
    None,
    Light,
    Medium,
    Overloaded
}

public readonly record struct PetWorkloadObservation(
    PetWorkloadTier PreviousTier,
    PetWorkloadTier Tier,
    int ActiveCount,
    bool TierChanged,
    bool HasReliableCount);

/// <summary>
/// Classifies connected task-count observations. Repeated polling is not a new
/// reaction, and loss of connectivity is never interpreted as finishing work.
/// Animation scheduling and confirmed-completion events are separate concerns.
/// </summary>
public sealed class PetWorkloadObservationPolicy
{
    public static readonly TimeSpan TierStabilityDelay = TimeSpan.FromMilliseconds(1500);
    private PetWorkloadTier _tier;
    private PetWorkloadTier? _candidate;
    private long _candidateSince;
    private int _lastReliableActiveCount;
    private bool _hasReliableCount;
    private long? _lastObservationAt;

    public PetWorkloadTier Tier => _tier;
    public int LastReliableActiveCount => _lastReliableActiveCount;

    public static PetWorkloadTier Classify(int activeCount) => activeCount switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(activeCount)),
        0 => PetWorkloadTier.None,
        <= 3 => PetWorkloadTier.Light,
        <= 5 => PetWorkloadTier.Medium,
        _ => PetWorkloadTier.Overloaded
    };

    public static PetDialogueIntent StartIntent(int activeCount) => Classify(activeCount) switch
    {
        PetWorkloadTier.Overloaded => PetDialogueIntent.TaskOverloaded,
        PetWorkloadTier.Medium => PetDialogueIntent.TaskModeratelyBusy,
        _ => PetDialogueIntent.TaskStarted
    };

    public PetWorkloadObservation Observe(int activeCount, bool isConnected, long nowMilliseconds)
    {
        if (_lastObservationAt is { } previousAt && nowMilliseconds < previousAt)
            throw new ArgumentOutOfRangeException(nameof(nowMilliseconds), "Use a monotonic clock.");
        _lastObservationAt = nowMilliseconds;

        var previous = _tier;
        if (!isConnected)
        {
            // Discard an unconfirmed tier change. Keep the last reliable state
            // so reconnecting with the same workload cannot replay its greeting.
            _candidate = null;
            return new(previous, _tier, _lastReliableActiveCount, false, false);
        }

        var observed = Classify(activeCount);
        _lastReliableActiveCount = activeCount;
        if (!_hasReliableCount)
        {
            _hasReliableCount = true;
            _tier = observed;
            return new(previous, _tier, activeCount, _tier != previous, true);
        }

        if (observed == _tier)
        {
            _candidate = null;
        }
        else if (_candidate != observed)
        {
            _candidate = observed;
            _candidateSince = nowMilliseconds;
        }
        else if (nowMilliseconds - _candidateSince >= TierStabilityDelay.TotalMilliseconds)
        {
            _tier = observed;
            _candidate = null;
        }

        return new(previous, _tier, activeCount, _tier != previous, true);
    }
}
