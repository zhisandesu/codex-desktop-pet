using XiaobianPet.Models;
using XiaobianPet.Services;

namespace XiaobianPet;

public partial class MainWindow
{
    private bool _dragLandingWindowScaleApplied;
    private Task<PreparedAnimationSequence?> PrepareDragLandingAsync(CancellationToken token)
    {
        var atlas = _spriteAtlas;
        return Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return atlas is not null && atlas.UsesExternalFrames(PetMood.DragLanding)
                    ? atlas.PrepareSequence(PetMood.DragLanding) : null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                App.WriteErrorLog("Drag landing preparation failure", ex);
                return null; // Missing optional new asset cannot strand an old install.
            }
        });
    }

    private void CancelDragLandingForGrab()
    {
        // Called only after the new grab has passed its complete-frame gate.
        CancelWithoutThrow(_dragReleaseCts);
        CancelAnimationBoundary(PetVisualOwner.DragLanding);
    }

    private void ApplyDragLandingWindowScale()
    {
        SetPetViewportSize(
            PetAnimations.CalculateContentScaledWindowDimension(
                RestingWindowWidth, PetAnimations.DragLandingContentScale),
            PetAnimations.CalculateContentScaledWindowDimension(
                RestingWindowHeight, PetAnimations.DragLandingContentScale));
        _dragLandingWindowScaleApplied = true;
    }

    private void RestoreWindowSizeAfterDragLanding()
    {
        if (!_dragLandingWindowScaleApplied) return;
        SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
        _dragLandingWindowScaleApplied = false;
    }

    private async Task<bool> PlayPreparedDragLandingAsync(
        PreparedAnimationSequence prepared,
        PreparedAnimationSequence? preparedCuteAngry,
        double targetRestingLeft,
        double targetRestingTop,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (prepared.RequestedMood != PetMood.DragLanding)
            throw new ArgumentException("Expected a prepared landing sequence.", nameof(prepared));
        if (_isClosing || _pointerGesture.IsPressed || _visualOwner != PetVisualOwner.Grab)
            return false;

        // Prepare the return before changing any visible frame or geometry.
        var fallback = _spriteAtlas!.PrepareSequence(_persistentMood);
        if (!ApplyMoodCore(PetMood.DragLanding, PetVisualOwner.DragLanding,
                prepared, releaseGrab: true)) return false;
        SetHostPositionForRestingViewport(targetRestingLeft, targetRestingTop);
        ApplyDragLandingWindowScale();
        var generation = _playbackGeneration;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _animationBoundaryWaiter = new AnimationBoundaryWaiter(generation,
            PetMood.DragLanding, PetVisualOwner.DragLanding, StopAtBoundary: true, completion);
        var completed = false;
        try
        {
            await AwaitAnimationBoundaryAsync(completion,
                PetAnimations.CalculateAnimationWatchdog(prepared.Frames.Count, prepared.FrameDuration),
                PetMood.DragLanding, token);
            completed = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { App.WriteErrorLog("Drag landing playback failure", ex); }
        finally
        {
            // The replacement grab owns geometry immediately. A canceled old
            // landing must not shrink it or publish an idle frame afterwards.
            if (!_isClosing && _visualOwner == PetVisualOwner.DragLanding &&
                _playbackGeneration == generation)
            {
                CancelAnimationBoundary(PetVisualOwner.DragLanding);
                _visualOwner = PetVisualOwner.Persistent;
                var angryStarted = completed && preparedCuteAngry is not null &&
                    TryStartPreparedCuteAngryReaction(preparedCuteAngry,
                        allowClickFinalFrame: true, allowPointerPress: false,
                        applyWindowScale: () =>
                        {
                            SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
                            ApplyCuteAngryWindowScale();
                        });
                if (!angryStarted)
                {
                    var target = _persistentMood;
                    var returnSequence = fallback.RequestedMood == target
                        ? fallback : _spriteAtlas!.PrepareSequence(target);
                    ApplyMoodCore(target, PetVisualOwner.Persistent, returnSequence);
                    SetPetViewportSize(RestingWindowWidth, RestingWindowHeight);
                }
                _panel?.FollowPet();
                _progressBubble?.PositionNear(this);
            }
        }
        return true;
    }
}
