using System.Diagnostics;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed class CodexDesktopTaskMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecentlyCompletedDuration = TimeSpan.FromSeconds(10);

    private readonly CodexDesktopBridge _bridge;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _candidateGate = new();
    private readonly object _snapshotGate = new();
    private readonly HashSet<string> _taskIdCandidates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excludedTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _recentlyCompletedUntil =
        new(StringComparer.Ordinal);

    private CodexDesktopMonitorSnapshot _snapshot = CodexDesktopMonitorSnapshot.Empty;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private volatile bool _disposed;

    public CodexDesktopTaskMonitor(
        CodexDesktopBridge bridge,
        IEnumerable<string> taskIdCandidates,
        TimeSpan? pollInterval = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        ArgumentNullException.ThrowIfNull(taskIdCandidates);
        SetTaskIdCandidates(taskIdCandidates);
        _pollInterval = pollInterval ?? DefaultPollInterval;
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    public event EventHandler<CodexDesktopMonitorSnapshot>? SnapshotChanged;

    public void AddTaskIdCandidate(string? taskId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        lock (_candidateGate)
        {
            var normalized = taskId.Trim();
            if (!_excludedTaskIds.Contains(normalized))
            {
                _taskIdCandidates.Add(normalized);
            }
        }
    }

    public void AddExcludedTaskId(string? taskId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        lock (_candidateGate)
        {
            var normalized = taskId.Trim();
            _excludedTaskIds.Add(normalized);
            _taskIdCandidates.Remove(normalized);
        }
    }

    public bool IsTaskExcluded(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }

        lock (_candidateGate)
        {
            return _excludedTaskIds.Contains(taskId.Trim());
        }
    }

    public void SetTaskIdCandidates(IEnumerable<string> taskIds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(taskIds);
        lock (_candidateGate)
        {
            _taskIdCandidates.Clear();
            foreach (var taskId in taskIds)
            {
                if (!string.IsNullOrWhiteSpace(taskId) && !_excludedTaskIds.Contains(taskId.Trim()))
                {
                    _taskIdCandidates.Add(taskId.Trim());
                }
            }
        }
    }

    public CodexDesktopMonitorSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _snapshot;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_pollTask is not null)
            {
                return;
            }

            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfDisposed();

            _pollCts = new CancellationTokenSource();
            _pollTask = PollAsync(_pollCts.Token);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var now = DateTimeOffset.UtcNow;
            try
            {
                if (!_bridge.IsConnected)
                {
                    await _bridge.ConnectAsync(GetTaskIdCandidates(), cancellationToken).ConfigureAwait(false);
                }

                var listedTasks = await _bridge.ListTasksAsync(100, cancellationToken)
                    .ConfigureAwait(false);
                listedTasks = listedTasks
                    .Where(task => !IsExcluded(task))
                    .ToArray();
                AddTaskIdCandidates(listedTasks.Select(task => task.Id));
                var tasks = await EnrichActiveTasksAsync(listedTasks, cancellationToken).ConfigureAwait(false);
                tasks = tasks
                    .Where(task => !IsExcluded(task))
                    .ToArray();
                var snapshot = BuildSnapshot(tasks, now);
                Publish(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var previous = Snapshot;
                Publish(previous with
                {
                    IsConnected = false,
                    Error = UserFacingError(ex),
                    UpdatedAt = now
                });
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<CodexDesktopTask>> EnrichActiveTasksAsync(
        IReadOnlyList<CodexDesktopTask> listedTasks,
        CancellationToken cancellationToken)
    {
        var tasks = listedTasks?.ToArray() ?? [];
        var activeTasks = tasks
            .Where(IsActive)
            .OrderByDescending(task => task.UpdatedAt)
            .Take(8)
            .ToArray();

        if (activeTasks.Length == 0)
        {
            return tasks;
        }

        var detailReads = activeTasks.Select(task => ReadDetailSafelyAsync(task, cancellationToken));
        var details = await Task.WhenAll(detailReads).ConfigureAwait(false);
        var detailsById = details
            .Where(detail => detail is not null)
            .ToDictionary(detail => detail!.Id, detail => detail!, StringComparer.Ordinal);

        return tasks
            .Select(task => detailsById.TryGetValue(task.Id, out var detail) ? detail : task)
            .ToArray();
    }

    private async Task<CodexDesktopTask?> ReadDetailSafelyAsync(
        CodexDesktopTask task,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _bridge.ReadTaskAsync(
                    task.Id,
                    task.HostId,
                    3,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Codex task detail refresh failed for {task.Id}: {ex}");
            return null;
        }
    }

    private CodexDesktopMonitorSnapshot BuildSnapshot(
        IReadOnlyList<CodexDesktopTask> tasks,
        DateTimeOffset now)
    {
        tasks = tasks
            .Where(task => !IsExcluded(task))
            .ToArray();
        var previousTasks = Snapshot.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var currentIds = tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (IsActive(task))
            {
                _recentlyCompletedUntil.Remove(task.Id);
                continue;
            }

            if (IsIdle(task) &&
                previousTasks.TryGetValue(task.Id, out var previous) &&
                IsActive(previous))
            {
                _recentlyCompletedUntil[task.Id] = now + RecentlyCompletedDuration;
            }
        }

        foreach (var taskId in _recentlyCompletedUntil
                     .Where(pair => pair.Value <= now || !currentIds.Contains(pair.Key))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _recentlyCompletedUntil.Remove(taskId);
        }

        var activeCount = tasks.Count(IsActive);
        var waitingCount = tasks.Count(IsWaiting);
        var errorCount = tasks.Count(IsError);
        var readyCount = tasks.Count(task => IsRecentlyCompleted(task, now));
        var primaryTask = tasks
            .Where(task => Priority(task, now) > 0)
            .OrderByDescending(task => Priority(task, now))
            .ThenByDescending(task => task.UpdatedAt)
            .FirstOrDefault();

        return new CodexDesktopMonitorSnapshot(
            tasks.ToArray(),
            primaryTask,
            activeCount,
            waitingCount,
            readyCount,
            errorCount,
            true,
            null,
            now);
    }

    private int Priority(CodexDesktopTask task, DateTimeOffset now)
    {
        if (HasSignal(task, "waitingOnApproval"))
        {
            return 5;
        }

        if (HasSignal(task, "waitingOnUserInput"))
        {
            return 4;
        }

        if (IsError(task))
        {
            return 3;
        }

        if (_recentlyCompletedUntil.TryGetValue(task.Id, out var until) && until > now)
        {
            return 2;
        }

        return IsActive(task) ? 1 : 0;
    }

    private bool IsRecentlyCompleted(CodexDesktopTask task, DateTimeOffset now) =>
        _recentlyCompletedUntil.TryGetValue(task.Id, out var until) && until > now;

    private static bool IsWaiting(CodexDesktopTask task) =>
        HasSignal(task, "waitingOnApproval") || HasSignal(task, "waitingOnUserInput");

    private static bool IsError(CodexDesktopTask task) =>
        HasSignal(task, "systemError") ||
        HasSignal(task, "blocked") ||
        HasSignal(task, "failed") ||
        HasSignal(task, "error");

    private static bool IsActive(CodexDesktopTask task) =>
        HasSignal(task, "active") ||
        HasSignal(task, "running") ||
        HasSignal(task, "inProgress") ||
        HasSignal(task, "started");

    private static bool IsIdle(CodexDesktopTask task) =>
        Normalize(task.Status) == "idle";

    private static bool HasSignal(CodexDesktopTask task, string signal)
    {
        var expected = Normalize(signal);
        if (Normalize(task.Status) == expected || Normalize(task.CurrentTurnStatus) == expected)
        {
            return true;
        }

        return task.ActiveFlags.Any(flag => Normalize(flag) == expected);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Codex desktop monitor loop stopped unexpectedly: {ex}");
        }
    }

    private void Publish(CodexDesktopMonitorSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }

        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<CodexDesktopMonitorSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Codex desktop monitor subscriber failed: {ex}");
            }
        }
    }

    private static string UserFacingError(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(message) ? "无法连接 Codex 桌面应用" : message;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void AddTaskIdCandidates(IEnumerable<string> taskIds)
    {
        lock (_candidateGate)
        {
            foreach (var taskId in taskIds)
            {
                if (!string.IsNullOrWhiteSpace(taskId) && !_excludedTaskIds.Contains(taskId.Trim()))
                {
                    _taskIdCandidates.Add(taskId.Trim());
                }
            }
        }
    }

    private string[] GetTaskIdCandidates()
    {
        lock (_candidateGate)
        {
            return _taskIdCandidates.ToArray();
        }
    }

    private bool IsExcluded(CodexDesktopTask task)
    {
        if (CompanionThreadIdentity.IsCompanionWorkspace(task.Cwd))
        {
            return true;
        }

        lock (_candidateGate)
        {
            return _excludedTaskIds.Contains(task.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        CancellationTokenSource? pollCts;
        Task? pollTask;
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pollCts = _pollCts;
            pollTask = _pollTask;
            _pollCts = null;
            _pollTask = null;
            pollCts?.Cancel();
        }
        finally
        {
            _startGate.Release();
        }

        if (pollTask is not null)
        {
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        pollCts?.Dispose();
    }
}
