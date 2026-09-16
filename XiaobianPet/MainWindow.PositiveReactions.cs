using System.Windows;
using System.Windows.Input;
using XiaobianPet.Models;

namespace XiaobianPet;

public partial class MainWindow
{
    private readonly PetCelebrationPolicy _celebrationPolicy = new();
    private bool _positiveReactionPending;
    private readonly PetHeadStrokeGesture _headStrokeGesture = new();
    private long _nextHeadStrokeSampleAt;

    private void ObserveHeadStroke(MouseEventArgs e)
    {
        var now = Environment.TickCount64;
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed ||
            e.MiddleButton == MouseButtonState.Pressed || _pointerGesture.IsPressed ||
            _isAutonomousMoving || !CanStartPositiveReaction(automatic: false) ||
            ContextMenu?.IsOpen == true)
        {
            _headStrokeGesture.Reset();
            return;
        }
        if (now < _nextHeadStrokeSampleAt) return;
        _nextHeadStrokeSampleAt = now + 60;
        var point = e.GetPosition(this);
        var overHead = TryClassifyVisibleGrabRegion(point, out var region) && region == PetGrabRegion.Head;
        if (_headStrokeGesture.Observe(now, point.X / ViewportWidth, point.Y / ViewportHeight,
                overHead, buttonPressed: false))
            _ = PlayPositiveReactionAsync(PetMood.HeadPatHappy, automatic: false);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e) => _headStrokeGesture.Reset();

    private bool CanStartPositiveReaction(bool automatic) =>
        !_isClosing && _spriteAtlas is not null &&
        !IsWorkloadRoutineActive &&
        !_positiveReactionPending && !_recycleBinFeedbackPending && !_pointerGesture.IsPressed &&
        !_isTemporaryMoodActive && _temporaryMoodCts is null &&
        !IsClickInteractionReserved && !IsCuteAngryPendingOrActive &&
        !PetVisualTransitionPolicy.IsForcedInteraction(_visualOwner) &&
        (!automatic || !_isSeatedRoutineActive && !_isAutonomousMoving);

    private void HeadPatMenuItem_Click(object sender, RoutedEventArgs e) =>
        _ = PlayPositiveReactionAsync(PetMood.HeadPatHappy, automatic: false);

    private void CelebrateMenuItem_Click(object sender, RoutedEventArgs e) =>
        _ = PlayPositiveReactionAsync(PetMood.TaskCelebrate, automatic: false);

    private void RamNibbleMenuItem_Click(object sender, RoutedEventArgs e) =>
        _ = PlayPositiveReactionAsync(PetMood.StandingRamNibble, automatic: false);

    private void TryCelebrateTaskCompletion(string eventKey)
    {
        if (TryQueueWorkloadCompletion(eventKey)) return;
        if (_celebrationPolicy.ObserveCompletion(
                eventKey, Environment.TickCount64, Random.Shared.NextDouble(),
                CanStartPositiveReaction(automatic: true)))
        {
            _ = PlayPositiveReactionAsync(PetMood.TaskCelebrate, automatic: true);
        }
    }

    private async Task PlayPositiveReactionAsync(PetMood mood, bool automatic)
    {
        if (!CanStartPositiveReaction(automatic))
        {
            return;
        }

        _positiveReactionPending = true;
        var requestedAt = Environment.TickCount64;
        try
        {
            var atlas = _spriteAtlas!;
            // Prepare every frame before entering the boundary queue. No file IO
            // or decoding occurs in the first-frame presentation callback.
            var prepared = await Task.Run(() => atlas.PrepareSequence(mood));
            if (_isClosing || IsWorkloadRoutineActive || _pointerGesture.IsPressed ||
                IsClickInteractionReserved || IsCuteAngryPendingOrActive ||
                _isTemporaryMoodActive || _temporaryMoodCts is not null)
            {
                return;
            }

            bool CanActivate() =>
                !automatic || Environment.TickCount64 - requestedAt <= 7000 &&
                !_isSeatedRoutineActive && !_isAutonomousMoving;

            await PlayTemporaryMoodAsync(
                mood,
                PetAnimations.CalculatePreviewDuration(prepared.Frames.Count, prepared.FrameDuration),
                CancellationToken.None,
                completeAtAnimationBoundary: true,
                canActivate: CanActivate,
                abandonIfSeated: automatic,
                onActivated: () =>
                {
                    if (mood == PetMood.TaskCelebrate)
                    {
                        _celebrationPolicy.MarkActivated(Environment.TickCount64);
                    }
                });
        }
        catch (Exception ex)
        {
            App.WriteErrorLog($"Positive reaction ({mood})", ex);
        }
        finally
        {
            _positiveReactionPending = false;
        }
    }
}
