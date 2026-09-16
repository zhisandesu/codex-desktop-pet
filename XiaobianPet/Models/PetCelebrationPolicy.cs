namespace XiaobianPet.Models;

/// <summary>Bounded, session-local deduplication of real successful turn events.</summary>
public sealed class PetCelebrationPolicy
{
    public const double Probability = 0.60;
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _availableAt = long.MinValue;

    public bool ObserveCompletion(string eventKey, long now, double sample, bool available)
    {
        if (string.IsNullOrWhiteSpace(eventKey) || !_seen.Add(eventKey))
        {
            return false;
        }

        _order.Enqueue(eventKey);
        while (_order.Count > 128)
        {
            _seen.Remove(_order.Dequeue());
        }

        // Missed opportunities are not replayed later, even after the cooldown.
        return available && now >= _availableAt &&
            double.IsFinite(sample) && sample >= 0 && sample < Probability;
    }

    public void MarkActivated(long now) =>
        _availableAt = checked(now + (long)Cooldown.TotalMilliseconds);
}
