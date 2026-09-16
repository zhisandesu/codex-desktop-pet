namespace XiaobianPet.Models;

public enum PetFollowScheduleKind
{
    Initial,
    Recurring,
    Retry
}

public enum PetFollowTargetKind
{
    Cursor,
    WorkAreaCenter
}

/// <summary>
/// A point in the caller's unified physical-pixel desktop coordinate space.
/// </summary>
public readonly record struct PetFollowPoint(double X, double Y);

/// <summary>
/// Bounds in the caller's unified physical-pixel desktop coordinate space.
/// </summary>
public readonly record struct PetFollowBounds(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public PetFollowPoint Center =>
        new(Left + (Width / 2), Top + (Height / 2));
}

public readonly record struct PetFollowPlan(
    PetFollowTargetKind TargetKind,
    PetFollowBounds WorkArea,
    PetFollowPoint TargetTopLeft,
    double Distance,
    int FacingDirection);

/// <summary>
/// Pure policy for infrequent, cursor-aware autonomous movement. Geometry is
/// expressed in one physical-pixel coordinate space; DIP constants are scaled
/// by <paramref name="dpiScale"/> at the API boundary.
/// </summary>
public static class PetFollowMovementPolicy
{
    public const long UnscheduledDeadline = long.MaxValue;

    public const double MinimumInitialDelayMilliseconds = 90_000;
    public const double MaximumInitialDelayMilliseconds = 150_000;
    public const double MinimumRecurringDelayMilliseconds = 180_000;
    public const double MaximumRecurringDelayMilliseconds = 300_000;
    public const double MinimumRetryDelayMilliseconds = 45_000;
    public const double MaximumRetryDelayMilliseconds = 90_000;

    public const double CursorTargetProbability = 0.70;
    public const double WorkAreaMarginDips = 16;
    public const double CursorGapDips = 24;
    public const double CursorNearPetExpansionDips = 32;
    public const double MinimumTravelDistanceDips = 180;
    public const double MinimumTravelWindowWidthRatio = 0.65;
    public const double HorizontalFacingDeadzoneDips = 18;
    public const double ArrivalToleranceDips = 8;
    public const double PreferredTravelSpeedDipsPerSecond = 150;

    public static readonly TimeSpan MaximumTravelDuration =
        TimeSpan.FromSeconds(12);
    public static readonly TimeSpan WatchdogGrace =
        TimeSpan.FromSeconds(2);

    public static TimeSpan CalculateDelay(
        PetFollowScheduleKind kind,
        double randomSample)
    {
        ValidateRandomSample(randomSample);
        var (minimum, maximum) = kind switch
        {
            PetFollowScheduleKind.Initial =>
                (MinimumInitialDelayMilliseconds, MaximumInitialDelayMilliseconds),
            PetFollowScheduleKind.Recurring =>
                (MinimumRecurringDelayMilliseconds, MaximumRecurringDelayMilliseconds),
            PetFollowScheduleKind.Retry =>
                (MinimumRetryDelayMilliseconds, MaximumRetryDelayMilliseconds),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        return TimeSpan.FromMilliseconds(
            minimum + (randomSample * (maximum - minimum)));
    }

    public static long CalculateDeadline(long nowMilliseconds, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        return checked(
            nowMilliseconds + (long)Math.Round(delay.TotalMilliseconds));
    }

    public static long PreserveScheduledDeadline(
        long currentDeadline,
        long candidateDeadline) =>
        currentDeadline == UnscheduledDeadline
            ? candidateDeadline
            : currentDeadline;

    public static bool IsDeadlineDue(long nowMilliseconds, long deadline) =>
        deadline != UnscheduledDeadline && nowMilliseconds >= deadline;

    public static PetFollowTargetKind SelectPreferredTarget(double randomSample)
    {
        ValidateRandomSample(randomSample);
        return randomSample < CursorTargetProbability
            ? PetFollowTargetKind.Cursor
            : PetFollowTargetKind.WorkAreaCenter;
    }

    public static bool TryCreatePlan(
        PetFollowTargetKind preferredTarget,
        PetFollowBounds currentPet,
        PetFollowPoint cursor,
        IReadOnlyList<PetFollowBounds> monitorWorkAreas,
        double dpiScale,
        int currentFacingDirection,
        out PetFollowPlan plan)
    {
        ValidateTargetKind(preferredTarget);
        ValidateBounds(currentPet, nameof(currentPet));
        ValidatePoint(cursor, nameof(cursor));
        ValidateDpiScale(dpiScale);
        ValidateFacingDirection(currentFacingDirection);
        ArgumentNullException.ThrowIfNull(monitorWorkAreas);

        foreach (var candidateWorkArea in monitorWorkAreas)
        {
            ValidateBounds(candidateWorkArea, nameof(monitorWorkAreas));
        }

        var requiredMargin = WorkAreaMarginDips * dpiScale;
        var usableWorkAreas = monitorWorkAreas
            .Where(workArea => CanFitPet(
                workArea,
                currentPet.Width,
                currentPet.Height,
                requiredMargin))
            .ToArray();
        if (usableWorkAreas.Length == 0)
        {
            plan = default;
            return false;
        }

        var workArea = SelectCursorWorkArea(cursor, usableWorkAreas);
        if (TryCreateCandidate(
                preferredTarget,
                currentPet,
                cursor,
                workArea,
                dpiScale,
                currentFacingDirection,
                out plan))
        {
            return true;
        }

        var alternateTarget = preferredTarget == PetFollowTargetKind.Cursor
            ? PetFollowTargetKind.WorkAreaCenter
            : PetFollowTargetKind.Cursor;
        return TryCreateCandidate(
            alternateTarget,
            currentPet,
            cursor,
            workArea,
            dpiScale,
            currentFacingDirection,
            out plan);
    }

    public static PetFollowPoint CalculateTargetTopLeft(
        PetFollowTargetKind targetKind,
        double petWidth,
        double petHeight,
        PetFollowPoint cursor,
        PetFollowBounds workArea,
        double dpiScale)
    {
        ValidateTargetKind(targetKind);
        ValidatePositiveFinite(petWidth, nameof(petWidth));
        ValidatePositiveFinite(petHeight, nameof(petHeight));
        ValidatePoint(cursor, nameof(cursor));
        ValidateBounds(workArea, nameof(workArea));
        ValidateDpiScale(dpiScale);

        var margin = WorkAreaMarginDips * dpiScale;
        if (!CanFitPet(workArea, petWidth, petHeight, margin))
        {
            throw new ArgumentException(
                "The work area cannot contain the pet and its safety margins.",
                nameof(workArea));
        }

        double preferredLeft;
        double preferredTop;
        if (targetKind == PetFollowTargetKind.WorkAreaCenter)
        {
            preferredLeft = workArea.Left + ((workArea.Width - petWidth) / 2);
            preferredTop = workArea.Top + ((workArea.Height - petHeight) / 2);
        }
        else
        {
            var gap = CursorGapDips * dpiScale;
            var center = workArea.Center;
            var towardCenterX = center.X - cursor.X;
            var towardCenterY = center.Y - cursor.Y;
            var normalizedHorizontalDistance =
                Math.Abs(towardCenterX) / workArea.Width;
            var normalizedVerticalDistance =
                Math.Abs(towardCenterY) / workArea.Height;

            if (normalizedHorizontalDistance >= normalizedVerticalDistance)
            {
                preferredLeft = towardCenterX >= 0
                    ? cursor.X + gap
                    : cursor.X - gap - petWidth;
                preferredTop = cursor.Y - (petHeight / 2);
            }
            else
            {
                preferredLeft = cursor.X - (petWidth / 2);
                preferredTop = towardCenterY >= 0
                    ? cursor.Y + gap
                    : cursor.Y - gap - petHeight;
            }
        }

        return new PetFollowPoint(
            Math.Clamp(
                preferredLeft,
                workArea.Left + margin,
                workArea.Right - petWidth - margin),
            Math.Clamp(
                preferredTop,
                workArea.Top + margin,
                workArea.Bottom - petHeight - margin));
    }

    public static PetFollowPoint AdvanceToward(
        PetFollowPoint current,
        PetFollowPoint target,
        double maximumStep)
    {
        ValidatePoint(current, nameof(current));
        ValidatePoint(target, nameof(target));
        ValidateNonNegativeFinite(maximumStep, nameof(maximumStep));

        var deltaX = target.X - current.X;
        var deltaY = target.Y - current.Y;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (distance == 0 || maximumStep >= distance)
        {
            return target;
        }

        if (maximumStep == 0)
        {
            return current;
        }

        var ratio = maximumStep / distance;
        return new PetFollowPoint(
            current.X + (deltaX * ratio),
            current.Y + (deltaY * ratio));
    }

    public static bool HasArrived(
        PetFollowPoint current,
        PetFollowPoint target,
        double dpiScale)
    {
        ValidatePoint(current, nameof(current));
        ValidatePoint(target, nameof(target));
        ValidateDpiScale(dpiScale);
        return CalculateDistance(current, target) <=
               ArrivalToleranceDips * dpiScale;
    }

    public static double CalculateLoopAlignedSpeed(
        double distance,
        TimeSpan walkingLoopDuration,
        double dpiScale)
    {
        ValidateNonNegativeFinite(distance, nameof(distance));
        ValidatePositiveDuration(walkingLoopDuration, nameof(walkingLoopDuration));
        ValidateDpiScale(dpiScale);
        if (distance == 0)
        {
            return 0;
        }

        var preferredSpeed = PreferredTravelSpeedDipsPerSecond * dpiScale;
        var idealCycleCount = Math.Max(
            1,
            checked((int)Math.Ceiling(
                distance /
                (preferredSpeed * walkingLoopDuration.TotalSeconds))));
        var maximumCycleCount = Math.Max(
            1,
            (int)Math.Floor(
                MaximumTravelDuration.TotalSeconds /
                walkingLoopDuration.TotalSeconds));
        var cycleCount = Math.Min(idealCycleCount, maximumCycleCount);
        var alignedDurationSeconds =
            cycleCount * walkingLoopDuration.TotalSeconds;
        return distance / alignedDurationSeconds;
    }

    public static TimeSpan CalculateWatchdog(
        double distance,
        double speed,
        TimeSpan walkingLoopDuration)
    {
        ValidateNonNegativeFinite(distance, nameof(distance));
        ValidatePositiveDuration(walkingLoopDuration, nameof(walkingLoopDuration));
        if (distance == 0)
        {
            return WatchdogGrace;
        }

        ValidatePositiveFinite(speed, nameof(speed));
        var rawCycleCount =
            (distance / speed) / walkingLoopDuration.TotalSeconds;
        var cycleCount = Math.Max(
            1,
            checked((int)Math.Ceiling(rawCycleCount - 1e-9)));
        return TimeSpan.FromTicks(checked(
            (walkingLoopDuration.Ticks * cycleCount) + WatchdogGrace.Ticks));
    }

    private static bool TryCreateCandidate(
        PetFollowTargetKind targetKind,
        PetFollowBounds currentPet,
        PetFollowPoint cursor,
        PetFollowBounds workArea,
        double dpiScale,
        int currentFacingDirection,
        out PetFollowPlan plan)
    {
        if (targetKind == PetFollowTargetKind.Cursor &&
            Contains(
                Expand(currentPet, CursorNearPetExpansionDips * dpiScale),
                cursor))
        {
            plan = default;
            return false;
        }

        var targetTopLeft = CalculateTargetTopLeft(
            targetKind,
            currentPet.Width,
            currentPet.Height,
            cursor,
            workArea,
            dpiScale);
        var currentTopLeft = new PetFollowPoint(currentPet.Left, currentPet.Top);
        var distance = CalculateDistance(currentTopLeft, targetTopLeft);
        var minimumDistance = Math.Max(
            MinimumTravelDistanceDips * dpiScale,
            currentPet.Width * MinimumTravelWindowWidthRatio);
        if (distance < minimumDistance)
        {
            plan = default;
            return false;
        }

        var horizontalDelta = targetTopLeft.X - currentPet.Left;
        var horizontalDeadzone = HorizontalFacingDeadzoneDips * dpiScale;
        var facingDirection = horizontalDelta < -horizontalDeadzone
            ? -1
            : horizontalDelta > horizontalDeadzone
                ? 1
                : currentFacingDirection;
        plan = new PetFollowPlan(
            targetKind,
            workArea,
            targetTopLeft,
            distance,
            facingDirection);
        return true;
    }

    private static PetFollowBounds SelectCursorWorkArea(
        PetFollowPoint cursor,
        IReadOnlyList<PetFollowBounds> workAreas)
    {
        foreach (var workArea in workAreas)
        {
            if (Contains(workArea, cursor))
            {
                return workArea;
            }
        }

        var best = workAreas[0];
        var bestDistanceSquared = DistanceSquaredToBounds(cursor, best);
        for (var index = 1; index < workAreas.Count; index++)
        {
            var candidate = workAreas[index];
            var candidateDistanceSquared =
                DistanceSquaredToBounds(cursor, candidate);
            if (candidateDistanceSquared < bestDistanceSquared)
            {
                best = candidate;
                bestDistanceSquared = candidateDistanceSquared;
            }
        }

        return best;
    }

    private static bool CanFitPet(
        PetFollowBounds workArea,
        double petWidth,
        double petHeight,
        double margin) =>
        petWidth + (margin * 2) <= workArea.Width &&
        petHeight + (margin * 2) <= workArea.Height;

    private static PetFollowBounds Expand(PetFollowBounds bounds, double amount) =>
        new(
            bounds.Left - amount,
            bounds.Top - amount,
            bounds.Width + (amount * 2),
            bounds.Height + (amount * 2));

    private static bool Contains(PetFollowBounds bounds, PetFollowPoint point) =>
        point.X >= bounds.Left &&
        point.X <= bounds.Right &&
        point.Y >= bounds.Top &&
        point.Y <= bounds.Bottom;

    private static double DistanceSquaredToBounds(
        PetFollowPoint point,
        PetFollowBounds bounds)
    {
        var nearestX = Math.Clamp(point.X, bounds.Left, bounds.Right);
        var nearestY = Math.Clamp(point.Y, bounds.Top, bounds.Bottom);
        var deltaX = point.X - nearestX;
        var deltaY = point.Y - nearestY;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private static double CalculateDistance(
        PetFollowPoint first,
        PetFollowPoint second)
    {
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static void ValidateTargetKind(PetFollowTargetKind targetKind)
    {
        if (!Enum.IsDefined(targetKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetKind),
                targetKind,
                null);
        }
    }

    private static void ValidateBounds(PetFollowBounds bounds, string name)
    {
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top))
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Bounds coordinates must be finite.");
        }

        ValidatePositiveFinite(bounds.Width, $"{name}.{nameof(bounds.Width)}");
        ValidatePositiveFinite(bounds.Height, $"{name}.{nameof(bounds.Height)}");
        if (!double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom))
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Bounds edges must be finite.");
        }
    }

    private static void ValidatePoint(PetFollowPoint point, string name)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Point coordinates must be finite.");
        }
    }

    private static void ValidateFacingDirection(int direction)
    {
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }
    }

    private static void ValidateDpiScale(double dpiScale) =>
        ValidatePositiveFinite(dpiScale, nameof(dpiScale));

    private static void ValidateRandomSample(double randomSample)
    {
        if (!double.IsFinite(randomSample) || randomSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }
    }

    private static void ValidatePositiveDuration(TimeSpan duration, string name)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void ValidatePositiveFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Value must be finite and positive.");
        }
    }

    private static void ValidateNonNegativeFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Value must be finite and non-negative.");
        }
    }
}
