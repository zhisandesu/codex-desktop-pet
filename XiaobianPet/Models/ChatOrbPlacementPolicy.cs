namespace XiaobianPet.Models;

/// <summary>
/// The screen-space bounds used to place the chat orb. Values are expressed in
/// the same device-independent coordinate system as the owning window.
/// </summary>
public readonly record struct ChatOrbPlacementBounds(
    double Left,
    double Top,
    double Width,
    double Height);

/// <summary>
/// The calculated top-left position of the chat orb.
/// </summary>
public readonly record struct ChatOrbPlacement(double Left, double Top);

/// <summary>
/// Calculates a stable chat-orb position without depending on WPF types.
/// </summary>
public static class ChatOrbPlacementPolicy
{
    public const double ScreenMargin = 6;
    public const double PetOverlap = 12;
    public const double VerticalAnchorRatio = 0.58;

    public static ChatOrbPlacement Calculate(
        ChatOrbPlacementBounds pet,
        double orbWidth,
        double orbHeight,
        ChatOrbPlacementBounds virtualScreen)
    {
        ValidateBounds(pet, nameof(pet));
        ValidateBounds(virtualScreen, nameof(virtualScreen));
        ValidatePositiveFinite(orbWidth, nameof(orbWidth));
        ValidatePositiveFinite(orbHeight, nameof(orbHeight));

        var screenRight = virtualScreen.Left + virtualScreen.Width;
        var rightCandidate = pet.Left + pet.Width - PetOverlap;
        var leftCandidate = pet.Left - orbWidth + PetOverlap;
        var canUseRight = rightCandidate + orbWidth <= screenRight - ScreenMargin;
        var preferredLeft = canUseRight ? rightCandidate : leftCandidate;

        var preferredTop =
            pet.Top + (pet.Height * VerticalAnchorRatio) - (orbHeight / 2);

        return new ChatOrbPlacement(
            FitAxis(
                preferredLeft,
                orbWidth,
                virtualScreen.Left,
                virtualScreen.Width),
            FitAxis(
                preferredTop,
                orbHeight,
                virtualScreen.Top,
                virtualScreen.Height));
    }

    private static double FitAxis(
        double preferred,
        double windowSize,
        double screenStart,
        double screenSize)
    {
        var availableWithMargins = screenSize - (ScreenMargin * 2);
        if (windowSize <= availableWithMargins)
        {
            var minimum = screenStart + ScreenMargin;
            var maximum = screenStart + screenSize - windowSize - ScreenMargin;
            return Math.Clamp(preferred, minimum, maximum);
        }

        // When the virtual desktop is narrower than the orb, no coordinate can
        // keep it fully visible. Centering maximizes the visible, clickable area
        // and avoids an inverted clamp interval.
        return screenStart + ((screenSize - windowSize) / 2);
    }

    private static void ValidateBounds(ChatOrbPlacementBounds bounds, string name)
    {
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top))
        {
            throw new ArgumentOutOfRangeException(name, "Bounds coordinates must be finite.");
        }

        ValidatePositiveFinite(bounds.Width, $"{name}.{nameof(bounds.Width)}");
        ValidatePositiveFinite(bounds.Height, $"{name}.{nameof(bounds.Height)}");
    }

    private static void ValidatePositiveFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Value must be finite and positive.");
        }
    }
}
