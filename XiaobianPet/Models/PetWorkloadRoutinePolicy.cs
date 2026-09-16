namespace XiaobianPet.Models;

public enum PetWorkloadAction
{
    None, Confident, Panting, Computer, WipeSweat, Rest
}

public enum PetComputerInterlude
{
    Typing, WipeTearsAndSniff, SlumpOnDesk, BonkDesk
}

/// <summary>Session-local reaction decisions, independent of WPF and media IO.</summary>
public sealed class PetWorkloadRoutinePolicy
{
    public static readonly TimeSpan GroundRestDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan InteractionGrace = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan CompletionExpiry = TimeSpan.FromSeconds(20);
    private readonly PetWorkloadObservationPolicy _counts = new();
    private readonly HashSet<string> _seenCompletions = new(StringComparer.Ordinal);
    private readonly Queue<string> _completionOrder = new();
    private PetWorkloadAction _pendingTierAction;
    private bool _overloadDebt;
    private bool _restPending;
    private bool _restInProgress;
    private long? _completionAt;
    private long _interactionsUntil = long.MinValue;
    private int _typingCycles;
    private int _nextInterludeAfter = 3;
    private PetComputerInterlude _lastInterlude;
    private bool _bonkAfterRecovery;

    public PetWorkloadTier Tier => _counts.Tier;
    public int ActiveCount => _counts.LastReliableActiveCount;
    public bool ShouldContinueComputer => Tier == PetWorkloadTier.Overloaded && !_restPending;

    public PetWorkloadObservation Observe(int activeCount, bool connected, long now)
    {
        var observation = _counts.Observe(activeCount, connected, now);
        if (!observation.HasReliableCount) return observation;

        if (Tier == PetWorkloadTier.Overloaded)
        {
            _overloadDebt = true;
            _pendingTierAction = PetWorkloadAction.None;
        }
        else if (observation.TierChanged)
        {
            if (_overloadDebt && !_restInProgress)
            {
                _restPending = true;
                _pendingTierAction = PetWorkloadAction.None;
                _completionAt = null;
            }
            else if (!_restInProgress)
            {
                _pendingTierAction = Tier switch
                {
                    PetWorkloadTier.Light => PetWorkloadAction.Confident,
                    PetWorkloadTier.Medium => PetWorkloadAction.Panting,
                    _ => PetWorkloadAction.None
                };
            }
        }
        return observation;
    }

    // Only invoke for explicit successful completion signals. A count decrease,
    // generic idle, stopped task, missing task, or connection failure is not one.
    public bool ObserveSuccessfulCompletion(string eventKey, long now)
    {
        if (string.IsNullOrWhiteSpace(eventKey) || !_seenCompletions.Add(eventKey)) return false;
        _completionOrder.Enqueue(eventKey);
        while (_completionOrder.Count > 128) _seenCompletions.Remove(_completionOrder.Dequeue());
        if (!_overloadDebt && !_restInProgress && Tier != PetWorkloadTier.Overloaded)
            _completionAt = now;
        return true;
    }

    // Called only after complete asset preparation, on a safe standing boundary.
    public PetWorkloadAction TakeNextStandingAction(long now)
    {
        if (now < _interactionsUntil || _restInProgress) return PetWorkloadAction.None;
        if (_restPending)
        {
            _restPending = false;
            _restInProgress = true;
            return PetWorkloadAction.Rest;
        }
        if (Tier == PetWorkloadTier.Overloaded) return PetWorkloadAction.Computer;
        if (_completionAt is { } completedAt)
        {
            _completionAt = null;
            if (now - completedAt <= CompletionExpiry.TotalMilliseconds)
            {
                _pendingTierAction = PetWorkloadAction.None;
                return PetWorkloadAction.WipeSweat;
            }
        }
        var next = _pendingTierAction;
        _pendingTierAction = PetWorkloadAction.None;
        return next;
    }

    public bool HasPendingStandingAction(long now) =>
        now >= _interactionsUntil && !_restInProgress &&
        (_restPending || Tier == PetWorkloadTier.Overloaded ||
         _completionAt is not null || _pendingTierAction != PetWorkloadAction.None);

    public PetComputerInterlude BeginComputerVisit()
    {
        _typingCycles = 0;
        _nextInterludeAfter = 3;
        if (!_bonkAfterRecovery) return PetComputerInterlude.Typing;
        _bonkAfterRecovery = false;
        _lastInterlude = PetComputerInterlude.BonkDesk;
        return PetComputerInterlude.BonkDesk;
    }

    public PetComputerInterlude AfterCompleteTypingCycle(double selectionSample, double spacingSample)
    {
        ValidateSample(selectionSample);
        ValidateSample(spacingSample);
        _typingCycles++;
        if (_typingCycles < _nextInterludeAfter) return PetComputerInterlude.Typing;

        var choices = new[]
        {
            PetComputerInterlude.WipeTearsAndSniff,
            PetComputerInterlude.SlumpOnDesk,
            PetComputerInterlude.BonkDesk
        }.Where(value => value != _lastInterlude).ToArray();
        var selected = choices[(int)(selectionSample * choices.Length)];
        _lastInterlude = selected;
        _typingCycles = 0;
        _nextInterludeAfter = 2 + (int)(spacingSample * 3);
        return selected;
    }

    public void MarkGroundRestCompleted()
    {
        _restInProgress = false;
        _restPending = false;
        _pendingTierAction = PetWorkloadAction.None;
        _completionAt = null;
        _overloadDebt = Tier == PetWorkloadTier.Overloaded;
        _bonkAfterRecovery = _overloadDebt;
    }

    public void InterruptForGrab(long now)
    {
        _interactionsUntil = checked(now + (long)InteractionGrace.TotalMilliseconds);
        _restInProgress = false;
        _restPending = false;
        _pendingTierAction = PetWorkloadAction.None;
        _completionAt = null;
        _overloadDebt = Tier == PetWorkloadTier.Overloaded;
        _bonkAfterRecovery = false;
    }

    private static void ValidateSample(double sample)
    {
        if (!double.IsFinite(sample) || sample < 0 || sample >= 1)
            throw new ArgumentOutOfRangeException(nameof(sample));
    }
}
