namespace XiaobianPet.Models;

public enum PetTaskContextReaction { Resumed, EditingCode }

public sealed record PetTaskContextFeedback(
    string TaskId, string TurnId, PetTaskContextReaction Reaction, long NotBefore, long ExpiresAt);

/// <summary>One reaction per turn/category; startup and reconnection only establish a baseline.</summary>
public sealed class PetTaskContextFeedbackPolicy
{
    private sealed class ObservedTask
    {
        public string? TurnId;
        public bool Active;
        public bool ResumeSeen;
        public bool CodeSeen;
    }

    private readonly Dictionary<string, ObservedTask> _tasks = new(StringComparer.Ordinal);
    private readonly List<PetTaskContextFeedback> _pending = [];
    private readonly HashSet<(string Task, string Turn, PetTaskContextReaction Reaction)> _seen = [];
    private readonly Queue<(string Task, string Turn, PetTaskContextReaction Reaction)> _seenOrder = [];
    private bool _connected;
    private long _nextSpeechAt;
    private long _nextCodeSpeechAt;
    public int PendingCount => _pending.Count;

    public void Observe(IReadOnlyList<CodexDesktopTask> tasks, bool connected, bool enabled, long now)
    {
        if (!connected || !enabled)
        {
            _connected = false;
            _tasks.Clear();
            _pending.Clear();
            return;
        }
        var baseline = !_connected;
        _connected = true;
        var ids = tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in _tasks.Keys.Where(id => !ids.Contains(id)).ToArray()) _tasks.Remove(missing);
        foreach (var task in tasks)
        {
            var exists = _tasks.TryGetValue(task.Id, out var previous);
            previous ??= new ObservedTask();
            var detail = task as CodexDesktopTaskDetail;
            var turn = detail?.RecentTurns.FirstOrDefault(turn => turn.Id == detail.CurrentTurnId);
            var turnId = turn?.Id;
            if (turnId is not null && turnId != previous.TurnId)
            {
                var lateBaselineDetail = exists && previous.Active && previous.TurnId is null;
                var suppress = baseline || lateBaselineDetail;
                previous.TurnId = turnId;
                previous.ResumeSeen = suppress;
                previous.CodeSeen = suppress && turn!.HasCodeEdit;
                if (suppress)
                {
                    MarkSeen(task.Id, turnId, PetTaskContextReaction.Resumed);
                    if (turn!.HasCodeEdit) MarkSeen(task.Id, turnId, PetTaskContextReaction.EditingCode);
                }
            }
            previous.Active = task.IsActive && !task.ActiveFlags.Any(flag =>
                flag.Contains("waiting", StringComparison.OrdinalIgnoreCase) ||
                flag.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                flag.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                flag.Contains("blocked", StringComparison.OrdinalIgnoreCase)) &&
                (turn is null || IsRunning(turn.Status));
            if (previous.Active && turn is not null)
            {
                if (turn.ResumeRequested && !previous.ResumeSeen)
                {
                    previous.ResumeSeen = true;
                    Queue(new(task.Id, turn.Id, PetTaskContextReaction.Resumed, now + 1000, now + 35000));
                }
                if (turn.HasCodeEdit && !previous.CodeSeen)
                {
                    previous.CodeSeen = true;
                    Queue(new(task.Id, turn.Id, PetTaskContextReaction.EditingCode, now + 20000, now + 100000));
                }
            }
            _tasks[task.Id] = previous;
        }
        _pending.RemoveAll(feedback => !IsCurrent(feedback, now));
    }

    private void Queue(PetTaskContextFeedback feedback)
    {
        if (!MarkSeen(feedback.TaskId, feedback.TurnId, feedback.Reaction)) return;
        // Keep only a small, recent queue, with acknowledgments before ambient complaints.
        _pending.Add(feedback);
        if (_pending.Count > 6) _pending.RemoveAt(0);
    }

    private bool MarkSeen(string task, string turn, PetTaskContextReaction reaction)
    {
        var key = (task, turn, reaction);
        if (!_seen.Add(key)) return false;
        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > 512) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    public bool IsCurrent(PetTaskContextFeedback feedback, long now) =>
        _connected && now <= feedback.ExpiresAt && _tasks.TryGetValue(feedback.TaskId, out var task) &&
        task.Active && task.TurnId == feedback.TurnId;

    public PetTaskContextFeedback? TakeNext(long now, bool canSpeak)
    {
        _pending.RemoveAll(feedback => !IsCurrent(feedback, now));
        if (!canSpeak || now < _nextSpeechAt) return null;
        var next = _pending.Where(feedback => now >= feedback.NotBefore &&
                (feedback.Reaction != PetTaskContextReaction.EditingCode || now >= _nextCodeSpeechAt))
            .OrderBy(feedback => feedback.Reaction).FirstOrDefault();
        if (next is null) return null;
        _pending.Remove(next);
        _nextSpeechAt = now + 25000;
        if (next.Reaction == PetTaskContextReaction.EditingCode) _nextCodeSpeechAt = now + 120000;
        return next;
    }

    private static bool IsRunning(string status) =>
        status.Equals("inProgress", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("active", StringComparison.OrdinalIgnoreCase);
}
