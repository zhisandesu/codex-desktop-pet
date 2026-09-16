using XiaobianPet.Models;

const long PressedAt = 1_000;
const double MinimumHorizontalDistance = 4;
const double MinimumVerticalDistance = 4;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static PetPointerGestureRecognizer PressedRecognizer(
    long pressedAt = PressedAt,
    long holdMilliseconds = 300)
{
    var recognizer = new PetPointerGestureRecognizer(holdMilliseconds);
    recognizer.Press(pressedAt);
    Expect(recognizer.IsPressed, "press must enter the pressed state");
    Expect(!recognizer.IsDragging, "press alone must not start a drag");
    return recognizer;
}

var idle = new PetPointerGestureRecognizer();
Expect(
    idle.Observe(
        PressedAt + 300,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "movement without a press must be ignored");
Expect(
    idle.Release() == PetPointerGestureCompletion.Ignored,
    "release without a press must be ignored");

var beforeHold = PressedRecognizer();
Expect(
    beforeHold.Observe(
        PressedAt + 299,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "movement at 299 ms must not start a drag");
Expect(beforeHold.IsPressed, "movement before the hold threshold must keep the press active");
Expect(!beforeHold.IsDragging, "movement before the hold threshold must remain pending");
Expect(
    beforeHold.Release() == PetPointerGestureCompletion.Click,
    "an early release with ordinary pointer jitter must remain a click");
Expect(!beforeHold.IsPressed && !beforeHold.IsDragging,
    "a jitter-tolerant click must reset the recognizer");

var horizontalBoundary = PressedRecognizer();
Expect(
    horizontalBoundary.Observe(
        PressedAt + 300,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.DragStarted,
    "the horizontal drag distance must start a drag at 300 ms");
Expect(horizontalBoundary.IsPressed && horizontalBoundary.IsDragging,
    "a started drag must remain pressed and report dragging");
Expect(
    horizontalBoundary.Observe(
        PressedAt + 301,
        MinimumHorizontalDistance + 1,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "an active drag must emit DragStarted only once");
Expect(
    horizontalBoundary.Release() == PetPointerGestureCompletion.Dragged,
    "releasing an active drag must complete it as dragged");
Expect(!horizontalBoundary.IsPressed && !horizontalBoundary.IsDragging,
    "a dragged release must reset the recognizer");

var verticalBoundary = PressedRecognizer();
Expect(
    verticalBoundary.Observe(
        PressedAt + 300,
        0,
        -MinimumVerticalDistance,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.DragStarted,
    "the vertical drag distance must start a drag at 300 ms");
Expect(
    verticalBoundary.Release() == PetPointerGestureCompletion.Dragged,
    "a vertical drag must complete as dragged");

var belowDistance = PressedRecognizer();
Expect(
    belowDistance.Observe(
        PressedAt + 300,
        MinimumHorizontalDistance - 0.01,
        -(MinimumVerticalDistance - 0.01),
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "movement below both drag distances must remain pending");
Expect(!belowDistance.IsDragging, "sub-threshold movement must not start a drag");
Expect(
    belowDistance.Release() == PetPointerGestureCompletion.Click,
    "sub-threshold movement must complete as a click");

var jitterClick = PressedRecognizer();
Expect(
    jitterClick.Observe(
        PressedAt + 120,
        MinimumHorizontalDistance - 0.5,
        -(MinimumVerticalDistance - 0.5),
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "small pointer jitter must not start a drag");
Expect(
    jitterClick.Release() == PetPointerGestureCompletion.Click,
    "a short press with small jitter must complete as a click");

var nearClickTolerance = PressedRecognizer();
Expect(
    nearClickTolerance.Observe(
        PressedAt + 120,
        PetPointerGestureRecognizer.DefaultClickCancellationDistance - 0.01,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "movement below the independent click tolerance must not start an early drag");
Expect(
    nearClickTolerance.Release() == PetPointerGestureCompletion.Click,
    "movement below the independent click tolerance must remain a click");

var fastMovementCanceled = PressedRecognizer();
Expect(
    fastMovementCanceled.Observe(
        PressedAt + 120,
        PetPointerGestureRecognizer.DefaultClickCancellationDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "large movement before the hold threshold must not start a drag");
Expect(
    fastMovementCanceled.Release() == PetPointerGestureCompletion.Canceled,
    "a quick movement at the independent click tolerance must cancel the click");

var heldWithoutMovement = PressedRecognizer();
Expect(
    heldWithoutMovement.Observe(
        PressedAt + 900,
        0,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "holding without movement must not start a drag");
Expect(
    heldWithoutMovement.Release() == PetPointerGestureCompletion.Click,
    "holding and releasing at the press point must complete as a click");

var movedThenHeld = PressedRecognizer();
Expect(
    movedThenHeld.Observe(
        PressedAt + 100,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "early movement must wait for the hold threshold");
Expect(
    movedThenHeld.Observe(
        PressedAt + 300,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.DragStarted,
    "continued holding after early movement must start a drag at 300 ms");
Expect(
    movedThenHeld.Release() == PetPointerGestureCompletion.Dragged,
    "a drag armed by early movement must complete as dragged");

var movedOutAndBack = PressedRecognizer();
Expect(
    movedOutAndBack.Observe(
        PressedAt + 100,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "early movement must remain pending before the hold threshold");
Expect(
    movedOutAndBack.Observe(
        PressedAt + 300,
        0,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "returning inside the drag distance must not start a drag when the hold completes");
Expect(
    movedOutAndBack.Release() == PetPointerGestureCompletion.Click,
    "crossing only the drag threshold and returning must remain a click");

var movedPastClickToleranceAndBack = PressedRecognizer();
Expect(
    movedPastClickToleranceAndBack.Observe(
        PressedAt + 100,
        PetPointerGestureRecognizer.DefaultClickCancellationDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "large early movement must wait for the hold threshold");
Expect(
    movedPastClickToleranceAndBack.Observe(
        PressedAt + 299,
        0,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "returning after large early movement must not start a drag");
Expect(
    movedPastClickToleranceAndBack.Release() == PetPointerGestureCompletion.Canceled,
    "crossing the click tolerance must remain canceled even after returning");

var customHold = PressedRecognizer(holdMilliseconds: 450);
Expect(
    customHold.Observe(
        PressedAt + 449,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.None,
    "a custom hold threshold must reject movement one millisecond early");
Expect(
    customHold.Observe(
        PressedAt + 450,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.DragStarted,
    "a custom hold threshold must accept movement at its exact boundary");

var canceled = PressedRecognizer();
canceled.Cancel();
Expect(!canceled.IsPressed && !canceled.IsDragging,
    "cancel must reset a pending press");
Expect(
    canceled.Release() == PetPointerGestureCompletion.Ignored,
    "release after cancel must be ignored");

var canceledDrag = PressedRecognizer();
Expect(
    canceledDrag.Observe(
        PressedAt + 300,
        MinimumHorizontalDistance,
        0,
        MinimumHorizontalDistance,
        MinimumVerticalDistance) == PetPointerGestureUpdate.DragStarted,
    "the cancellation test must first start a drag");
canceledDrag.Cancel();
Expect(!canceledDrag.IsPressed && !canceledDrag.IsDragging,
    "cancel must reset an active drag");
Expect(
    canceledDrag.Release() == PetPointerGestureCompletion.Ignored,
    "release after canceling a drag must be ignored");

Console.WriteLine("POINTER_GESTURE_HARNESS_OK");
