using System.Windows;
using System.Windows.Media;
using XiaobianPet.Models;
using XiaobianPet.Services;

namespace XiaobianPet;

public partial class MainWindow
{
    private readonly PetTaskContextFeedbackPolicy _taskContextFeedback = new();
    private long _lastCharacterSpeechAt = -60000;
    private long _taskContextBubbleProtectedUntil;
    private bool _taskContextSpeechPending;
    private int _taskContextConversationCount;
    private int _resumeLineIndex;
    private int _codeLineIndex;

    // These lines describe a confirmed local event, not a command or a promise
    // to submit work. Keeping them local also avoids exposing task text to a model.
    private static readonly string[] ResumeContextLines =
    [
        "收到，这就继续帮你执行！",
        "接上啦，我们把之前的任务继续做完！",
        "嗯嗯，接着忙之前的事啦！"
    ];
    private static readonly string[] CodeContextLines =
    [
        "呜，这些屎山代码太难修了，让我一块块拆！",
        "这段代码绕得我头都晕啦，慢慢理顺它！",
        "又在和代码打架了，哼，我才不会认输！"
    ];

    private void ObserveTaskContextFeedback(CodexDesktopMonitorSnapshot snapshot)
    {
        var scopedTasks = snapshot.Tasks.Where(task =>
            !_desktopMonitor.IsTaskExcluded(task.Id) &&
            !CompanionThreadIdentity.IsCompanionWorkspace(task.Cwd) &&
            !string.Equals(task.Id, _dialogueClient.ConversationThreadId, StringComparison.Ordinal)).ToArray();
        _taskContextFeedback.Observe(scopedTasks, snapshot.IsConnected,
            _settings.TaskContextFeedbackEnabled, Environment.TickCount64);
    }

    private bool CanPresentTaskContextFeedback(long now) =>
        !_isClosing && _settings.TaskContextFeedbackEnabled && _desktopSnapshot.IsConnected &&
        !_pointerGesture.IsPressed && _dragReleaseCts is null &&
        !IsClickInteractionReserved && !IsCuteAngryPendingOrActive &&
        _visualOwner != PetVisualOwner.Grab &&
        Volatile.Read(ref _taskContextConversationCount) == 0 && _voiceConversationGate.CurrentCount > 0 &&
        _interactionDialogueCts is null && _automaticDialogueCts is null &&
        !IsInteractionVoiceBlocked(now) && now - _lastCharacterSpeechAt >= 10000;

    private IDisposable PauseTaskContextForConversation()
    {
        Interlocked.Increment(ref _taskContextConversationCount);
        return new TaskContextConversationScope(this);
    }

    private sealed class TaskContextConversationScope(MainWindow window) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref window._taskContextConversationCount);
    }

    private bool CanRenderTaskProgress(CodexTaskProgressState state) =>
        state != CodexTaskProgressState.Running ||
        Environment.TickCount64 >= _taskContextBubbleProtectedUntil ||
        _activeBubbleSemanticKey?.StartsWith("pet-speech:", StringComparison.Ordinal) != true;

    private void TryPresentTaskContextFeedback(long now)
    {
        if (_taskContextSpeechPending) return;
        var feedback = _taskContextFeedback.TakeNext(now, CanPresentTaskContextFeedback(now));
        if (feedback is not null) _ = PresentTaskContextFeedbackAsync(feedback);
    }

    private async Task PresentTaskContextFeedbackAsync(PetTaskContextFeedback feedback)
    {
        _taskContextSpeechPending = true;
        try
        {
            var line = feedback.Reaction == PetTaskContextReaction.Resumed
                ? ResumeContextLines[_resumeLineIndex++ % ResumeContextLines.Length]
                : CodeContextLines[_codeLineIndex++ % CodeContextLines.Length];
            await PresentCharacterLineAsync(line, isImportant: false,
                visibleDuration: TimeSpan.FromSeconds(7),
                isStillCurrent: () => _settings.TaskContextFeedbackEnabled && !_isClosing &&
                    Volatile.Read(ref _taskContextConversationCount) == 0 && _voiceConversationGate.CurrentCount > 0 &&
                    !_pointerGesture.IsPressed && !IsClickInteractionReserved && !IsCuteAngryPendingOrActive &&
                    _taskContextFeedback.IsCurrent(feedback, Environment.TickCount64),
                cancellationToken: _dialogueLifetimeCts.Token,
                playMessageFeedback: false, protectTaskContextBubble: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.WriteErrorLog("Task context feedback", ex); }
        finally { _taskContextSpeechPending = false; }
    }

    private async void TaskContextFeedbackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var previous = _settings.TaskContextFeedbackEnabled;
        _settings.TaskContextFeedbackEnabled = TaskContextFeedbackMenuItem.IsChecked;
        ObserveTaskContextFeedback(_desktopSnapshot);
        try { await _settingsStore.SaveAsync(_settings); }
        catch (Exception ex)
        {
            _settings.TaskContextFeedbackEnabled = previous;
            TaskContextFeedbackMenuItem.IsChecked = previous;
            ObserveTaskContextFeedback(_desktopSnapshot);
            SetStatus("任务反馈设置保存失败", Brushes.IndianRed);
            App.WriteErrorLog("Task context setting save", ex);
        }
    }
}
