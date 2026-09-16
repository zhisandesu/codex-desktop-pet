namespace XiaobianPet.Models;

public enum PetPointerGestureUpdate
{
    None,
    DragStarted
}

public enum PetPointerGestureCompletion
{
    Ignored,
    Click,
    Dragged,
    Canceled
}

public sealed class PetPointerGestureRecognizer
{
    public const long DefaultHoldMilliseconds = 300;
    public const double DefaultClickCancellationDistance = 12;

    private readonly long _holdMilliseconds;
    private long _pressedAtMilliseconds;
    private bool _movedBeyondClickTolerance;

    public PetPointerGestureRecognizer(long holdMilliseconds = DefaultHoldMilliseconds)
    {
        if (holdMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(holdMilliseconds));
        }

        _holdMilliseconds = holdMilliseconds;
    }

    public bool IsPressed { get; private set; }

    public bool IsDragging { get; private set; }

    public void Press(long timestampMilliseconds)
    {
        _pressedAtMilliseconds = timestampMilliseconds;
        _movedBeyondClickTolerance = false;
        IsPressed = true;
        IsDragging = false;
    }

    public PetPointerGestureUpdate Observe(
        long timestampMilliseconds,
        double horizontalTravel,
        double verticalTravel,
        double minimumHorizontalDragDistance,
        double minimumVerticalDragDistance)
    {
        if (!IsPressed || IsDragging)
        {
            return PetPointerGestureUpdate.None;
        }

        var isCurrentlyBeyondDragDistance =
            Math.Abs(horizontalTravel) >= minimumHorizontalDragDistance ||
            Math.Abs(verticalTravel) >= minimumVerticalDragDistance;

        // A drag keeps the tighter system threshold, but ordinary click jitter
        // gets a separate, more forgiving tolerance. Crossing the drag threshold
        // before the hold delay must not silently discard a normal click.
        var isCurrentlyBeyondClickTolerance =
            Math.Abs(horizontalTravel) >= DefaultClickCancellationDistance ||
            Math.Abs(verticalTravel) >= DefaultClickCancellationDistance;
        if (isCurrentlyBeyondClickTolerance)
        {
            _movedBeyondClickTolerance = true;
        }

        var heldMilliseconds = Math.Max(0, timestampMilliseconds - _pressedAtMilliseconds);
        if (!isCurrentlyBeyondDragDistance || heldMilliseconds < _holdMilliseconds)
        {
            return PetPointerGestureUpdate.None;
        }

        IsDragging = true;
        return PetPointerGestureUpdate.DragStarted;
    }

    public PetPointerGestureCompletion Release()
    {
        if (!IsPressed)
        {
            return PetPointerGestureCompletion.Ignored;
        }

        var completion = IsDragging
            ? PetPointerGestureCompletion.Dragged
            : _movedBeyondClickTolerance
                ? PetPointerGestureCompletion.Canceled
                : PetPointerGestureCompletion.Click;
        Reset();
        return completion;
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        _pressedAtMilliseconds = 0;
        _movedBeyondClickTolerance = false;
        IsPressed = false;
        IsDragging = false;
    }
}
