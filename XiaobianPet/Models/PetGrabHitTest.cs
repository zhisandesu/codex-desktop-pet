namespace XiaobianPet.Models;

/// <summary>
/// The semantic part of the front-facing pet that the pointer grabbed.
/// </summary>
public enum PetGrabRegion
{
    Head,
    Wing,
    Body
}

/// <summary>
/// Classifies a pointer in the transparent 384 x 416 animation frame.
/// Coordinates are normalized to the whole frame, not the visible-alpha bounds.
/// </summary>
public static class PetGrabHitTest
{
    private const double AdaptiveHeadMaximumY = 0.49;
    private const double AdaptiveHeadMinimumX = 0.18;
    private const double AdaptiveHeadMaximumX = 0.82;
    private const double AdaptiveWingMaximumY = 0.78;
    private const double AdaptiveLeftWingMaximumX = 0.27;
    private const double AdaptiveRightWingMinimumX = 0.73;

    // These contours follow the calm front-facing video frame.  Head overlap is
    // intentional: hair, horns and the wing roots visually sit in front of the
    // wings, so the user gets the head reaction when those silhouettes overlap.
    private static readonly NormalizedPoint[] HeadContour =
    [
        new(0.48, 0.015),
        new(0.54, 0.04),
        new(0.62, 0.025),
        new(0.72, 0.075),
        new(0.75, 0.16),
        new(0.79, 0.27),
        new(0.78, 0.39),
        new(0.73, 0.48),
        new(0.62, 0.52),
        new(0.50, 0.50),
        new(0.38, 0.52),
        new(0.27, 0.48),
        new(0.22, 0.38),
        new(0.23, 0.24),
        new(0.26, 0.13),
        new(0.32, 0.06),
        new(0.42, 0.04)
    ];

    private static readonly NormalizedPoint[] LeftWingContour =
    [
        new(0.30, 0.46),
        new(0.42, 0.50),
        new(0.41, 0.61),
        new(0.36, 0.67),
        new(0.27, 0.73),
        new(0.18, 0.68),
        new(0.17, 0.60),
        new(0.22, 0.54)
    ];

    private static readonly NormalizedPoint[] RightWingContour =
    [
        new(0.58, 0.50),
        new(0.70, 0.46),
        new(0.80, 0.53),
        new(0.88, 0.62),
        new(0.90, 0.69),
        new(0.78, 0.71),
        new(0.67, 0.68),
        new(0.60, 0.61)
    ];

    public static PetGrabRegion Classify(double normalizedX, double normalizedY)
    {
        ValidateNormalizedCoordinate(normalizedX, nameof(normalizedX));
        ValidateNormalizedCoordinate(normalizedY, nameof(normalizedY));

        if (Contains(HeadContour, normalizedX, normalizedY))
        {
            return PetGrabRegion.Head;
        }

        if (Contains(LeftWingContour, normalizedX, normalizedY) ||
            Contains(RightWingContour, normalizedX, normalizedY))
        {
            return PetGrabRegion.Wing;
        }

        return PetGrabRegion.Body;
    }

    /// <summary>
    /// Classifies an opaque point relative to the alpha bounds of the frame that
    /// is actually visible.  This keeps head/wing/body hit regions attached to
    /// seated, tilted, running and climbing poses instead of applying the calm
    /// front-facing contours to every animation.
    /// </summary>
    public static PetGrabRegion ClassifyVisiblePoint(
        double normalizedX,
        double normalizedY,
        double visibleLeft,
        double visibleTop,
        double visibleRight,
        double visibleBottom)
    {
        ValidateNormalizedCoordinate(normalizedX, nameof(normalizedX));
        ValidateNormalizedCoordinate(normalizedY, nameof(normalizedY));
        ValidateNormalizedCoordinate(visibleLeft, nameof(visibleLeft));
        ValidateNormalizedCoordinate(visibleTop, nameof(visibleTop));
        ValidateNormalizedCoordinate(visibleRight, nameof(visibleRight));
        ValidateNormalizedCoordinate(visibleBottom, nameof(visibleBottom));

        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
        {
            throw new ArgumentException("The visible alpha bounds must have positive width and height.");
        }

        var relativeX = Math.Clamp(
            (normalizedX - visibleLeft) / (visibleRight - visibleLeft),
            0,
            1);
        var relativeY = Math.Clamp(
            (normalizedY - visibleTop) / (visibleBottom - visibleTop),
            0,
            1);

        if (relativeY <= AdaptiveHeadMaximumY &&
            relativeX >= AdaptiveHeadMinimumX &&
            relativeX <= AdaptiveHeadMaximumX)
        {
            return PetGrabRegion.Head;
        }

        if (relativeY <= AdaptiveWingMaximumY &&
            (relativeX <= AdaptiveLeftWingMaximumX ||
             relativeX >= AdaptiveRightWingMinimumX))
        {
            return PetGrabRegion.Wing;
        }

        return PetGrabRegion.Body;
    }

    private static void ValidateNormalizedCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The grab coordinate must be finite and between 0 and 1.");
        }
    }

    private static bool Contains(
        IReadOnlyList<NormalizedPoint> contour,
        double x,
        double y)
    {
        var inside = false;
        for (var currentIndex = 0;
             currentIndex < contour.Count;
             currentIndex++)
        {
            var previousIndex = currentIndex == 0
                ? contour.Count - 1
                : currentIndex - 1;
            var current = contour[currentIndex];
            var previous = contour[previousIndex];

            if (IsOnSegment(previous, current, x, y))
            {
                return true;
            }

            var crossesScanline = (current.Y > y) != (previous.Y > y);
            if (crossesScanline &&
                x < ((previous.X - current.X) * (y - current.Y) /
                     (previous.Y - current.Y)) + current.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool IsOnSegment(
        NormalizedPoint start,
        NormalizedPoint end,
        double x,
        double y)
    {
        const double epsilon = 1e-9;
        var cross = ((x - start.X) * (end.Y - start.Y)) -
                    ((y - start.Y) * (end.X - start.X));
        if (Math.Abs(cross) > epsilon)
        {
            return false;
        }

        return x >= Math.Min(start.X, end.X) - epsilon &&
               x <= Math.Max(start.X, end.X) + epsilon &&
               y >= Math.Min(start.Y, end.Y) - epsilon &&
               y <= Math.Max(start.Y, end.Y) + epsilon;
    }

    private readonly record struct NormalizedPoint(double X, double Y);
}
