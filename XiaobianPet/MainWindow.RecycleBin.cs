using System.Windows;
using XiaobianPet.Models;
using XiaobianPet.Services;

namespace XiaobianPet;

public partial class MainWindow
{
    private readonly RecycleBinStatisticsService _recycleBinStatistics = new();
    private readonly RecycleBinReactionPolicy _recycleBinReactionPolicy = new();
    private bool _recycleBinFeedbackPending;
    private long _recycleBinCompletedGeneration = -1;

    // The menu and low-frequency idle opportunity share the same complete clip.
    private void RecycleBinMenuItem_Click(object sender, RoutedEventArgs e) =>
        _ = PresentRecycleBinFeedbackAsync();

    private void TryPresentAutomaticRecycleBinFeedback(long now)
    {
        var eligible = !_recycleBinFeedbackPending && CanPresentRecycleBinFeedback() &&
            CanStartStandingAmbientAction() && IsRecycleBinIdleWindow() &&
            now - _lastCharacterSpeechAt >= 15000;
        if (_recycleBinReactionPolicy.TryReserveAutomatic(now, eligible, Random.Shared.NextDouble()))
            _ = PresentRecycleBinFeedbackAsync(automatic: true);
    }

    private bool IsRecycleBinIdleWindow() =>
        !IsWorkloadRoutineActive && _workloadPolicy.ActiveCount == 0 &&
        _desktopSnapshot.ActiveCount == 0 && !_isSeatedRoutineActive &&
        !_isAutonomousMoving && !_isTemporaryMoodActive && _temporaryMoodCts is null &&
        _visualOwner is PetVisualOwner.Persistent or PetVisualOwner.IdleGesture;

    private bool CanPresentRecycleBinFeedback() =>
        _spriteAtlas is not null && !_isClosing && !_pointerGesture.IsPressed &&
        !IsWorkloadRoutineActive && !_positiveReactionPending &&
        !_isTemporaryMoodActive && _temporaryMoodCts is null &&
        !IsClickInteractionReserved && !IsCuteAngryPendingOrActive &&
        _visualOwner != PetVisualOwner.Grab && _dragReleaseCts is null &&
        Volatile.Read(ref _taskContextConversationCount) == 0 &&
        _voiceConversationGate.CurrentCount > 0 &&
        _interactionDialogueCts is null && _automaticDialogueCts is null &&
        !IsInteractionVoiceBlocked(Environment.TickCount64);

    private bool CanContinueRecycleBinFeedback(bool automatic) =>
        !_isClosing && !_pointerGesture.IsPressed && !IsWorkloadRoutineActive &&
        !IsClickInteractionReserved && !IsCuteAngryPendingOrActive &&
        _visualOwner != PetVisualOwner.Grab && _dragReleaseCts is null &&
        Volatile.Read(ref _taskContextConversationCount) == 1 &&
        _voiceConversationGate.CurrentCount > 0 &&
        _interactionDialogueCts is null && _automaticDialogueCts is null &&
        (!automatic || _workloadPolicy.ActiveCount == 0 && _desktopSnapshot.ActiveCount == 0 &&
            !_isSeatedRoutineActive && !_isAutonomousMoving);

    private bool DidCompleteRecycleBinAnimation(long generation) =>
        generation >= 0 && _recycleBinCompletedGeneration == generation;

    private async Task PresentRecycleBinFeedbackAsync(bool automatic = false)
    {
        if (_recycleBinFeedbackPending || !CanPresentRecycleBinFeedback()) return;
        _recycleBinFeedbackPending = true;
        _recycleBinReactionPolicy.MarkPresented(Environment.TickCount64, Random.Shared.NextDouble());
        try
        {
            using var conversation = PauseTaskContextForConversation();
            var token = _dialogueLifetimeCts.Token;
            var snapshotTask = _recycleBinStatistics.QueryAsync(token);
            var atlas = _spriteAtlas!;
            var prepared = await Task.Run(() => atlas.PrepareSequence(PetMood.RecycleBinPeek), token);
            if (!CanContinueRecycleBinFeedback(automatic)) return;
            long generation = -1;
            await PlayTemporaryMoodAsync(PetMood.RecycleBinPeek,
                PetAnimations.CalculatePreviewDuration(prepared.Frames.Count, prepared.FrameDuration),
                token, completeAtAnimationBoundary: true,
                onActivated: () => generation = _playbackGeneration,
                canActivate: () => CanContinueRecycleBinFeedback(automatic),
                abandonIfSeated: automatic);
            // Cancellation also returns from PlayTemporaryMoodAsync. Only an
            // observed real last-frame boundary permits the result to be spoken.
            if (!DidCompleteRecycleBinAnimation(generation) || !CanContinueRecycleBinFeedback(automatic)) return;
            var snapshot = await snapshotTask;
            await PresentCharacterLineAsync(RecycleBinFeedback.CreateLine(snapshot),
                isImportant: false, visibleDuration: TimeSpan.FromSeconds(7),
                isStillCurrent: () => CanContinueRecycleBinFeedback(automatic),
                cancellationToken: token,
                playMessageFeedback: false, protectTaskContextBubble: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.WriteErrorLog("Recycle Bin aggregate feedback", ex); }
        finally { _recycleBinFeedbackPending = false; }
    }
}
