using XiaobianPet.Models;

const double OrbWidth = 60;
const double OrbHeight = 60;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectNear(double actual, double expected, string message)
{
    if (Math.Abs(actual - expected) > 0.000001)
    {
        throw new InvalidOperationException(
            $"{message}. Expected {expected}, received {actual}.");
    }
}

var primaryScreen = new ChatOrbPlacementBounds(0, 0, 1920, 1080);

var rightPreferred = ChatOrbPlacementPolicy.Calculate(
    new ChatOrbPlacementBounds(100, 200, 242, 276),
    OrbWidth,
    OrbHeight,
    primaryScreen);
ExpectNear(
    rightPreferred.Left,
    100 + 242 - ChatOrbPlacementPolicy.PetOverlap,
    "the orb must prefer the pet's right edge when it fits");
ExpectNear(
    rightPreferred.Top,
    200 + (276 * ChatOrbPlacementPolicy.VerticalAnchorRatio) - (OrbHeight / 2),
    "the orb must use the pet's vertical interaction anchor");

var flippedLeft = ChatOrbPlacementPolicy.Calculate(
    new ChatOrbPlacementBounds(1710, 200, 242, 276),
    OrbWidth,
    OrbHeight,
    primaryScreen);
ExpectNear(
    flippedLeft.Left,
    1710 - OrbWidth + ChatOrbPlacementPolicy.PetOverlap,
    "the orb must flip to the pet's left edge near the right screen boundary");

var clampedAtTop = ChatOrbPlacementPolicy.Calculate(
    new ChatOrbPlacementBounds(500, -200, 242, 276),
    OrbWidth,
    OrbHeight,
    primaryScreen);
ExpectNear(
    clampedAtTop.Top,
    ChatOrbPlacementPolicy.ScreenMargin,
    "the orb must clamp to the top screen margin");

var clampedAtBottom = ChatOrbPlacementPolicy.Calculate(
    new ChatOrbPlacementBounds(500, 1010, 242, 276),
    OrbWidth,
    OrbHeight,
    primaryScreen);
ExpectNear(
    clampedAtBottom.Top,
    1080 - OrbHeight - ChatOrbPlacementPolicy.ScreenMargin,
    "the orb must clamp to the bottom screen margin");

var negativeCoordinateDesktop = new ChatOrbPlacementBounds(-1920, -180, 3840, 1260);
var negativeMonitor = ChatOrbPlacementPolicy.Calculate(
    new ChatOrbPlacementBounds(-1810, -160, 242, 276),
    OrbWidth,
    OrbHeight,
    negativeCoordinateDesktop);
ExpectNear(
    negativeMonitor.Left,
    -1810 + 242 - ChatOrbPlacementPolicy.PetOverlap,
    "negative multi-monitor coordinates must preserve the right-side placement");
ExpectNear(
    negativeMonitor.Top,
    -160 + (276 * ChatOrbPlacementPolicy.VerticalAnchorRatio) - (OrbHeight / 2),
    "negative multi-monitor coordinates must preserve a negative vertical position");

var narrowDesktop = new ChatOrbPlacementBounds(-30, 40, 32, 28);
var narrowPlacement = ChatOrbPlacementPolicy.Calculate(
    new ChatOrbPlacementBounds(-25, 42, 20, 20),
    OrbWidth,
    OrbHeight,
    narrowDesktop);
Expect(double.IsFinite(narrowPlacement.Left) && double.IsFinite(narrowPlacement.Top),
    "an ultra-narrow desktop must always produce finite coordinates");
ExpectNear(
    narrowPlacement.Left,
    narrowDesktop.Left + ((narrowDesktop.Width - OrbWidth) / 2),
    "an orb wider than the desktop must be centered to maximize its visible width");
ExpectNear(
    narrowPlacement.Top,
    narrowDesktop.Top + ((narrowDesktop.Height - OrbHeight) / 2),
    "an orb taller than the desktop must be centered to maximize its visible height");

Console.WriteLine("CHAT_ORB_PLACEMENT_HARNESS_OK");
