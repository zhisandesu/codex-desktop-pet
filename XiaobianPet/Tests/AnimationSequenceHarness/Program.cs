using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XiaobianPet.Models;
using XiaobianPet.Services;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void WritePng(string path, int width, int height, Color color)
{
    var pixels = new byte[checked(width * height * 4)];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = color.B;
        pixels[index + 1] = color.G;
        pixels[index + 2] = color.R;
        pixels[index + 3] = color.A;
    }

    var bitmap = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        pixels,
        width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static Color ReadFirstPixel(BitmapSource frame)
{
    var pixel = new byte[4];
    frame.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
    return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
}

static bool IsExactHorizontalMirror(BitmapSource source, BitmapSource mirrored)
{
    if (source.PixelWidth != mirrored.PixelWidth ||
        source.PixelHeight != mirrored.PixelHeight ||
        source.Format.BitsPerPixel != mirrored.Format.BitsPerPixel ||
        source.Format.BitsPerPixel != 32)
    {
        return false;
    }

    var stride = checked(source.PixelWidth * 4);
    var sourcePixels = new byte[checked(stride * source.PixelHeight)];
    var mirroredPixels = new byte[sourcePixels.Length];
    source.CopyPixels(sourcePixels, stride, 0);
    mirrored.CopyPixels(mirroredPixels, stride, 0);

    for (var y = 0; y < source.PixelHeight; y++)
    {
        for (var x = 0; x < source.PixelWidth; x++)
        {
            var sourceOffset = (y * stride) + ((source.PixelWidth - 1 - x) * 4);
            var mirroredOffset = (y * stride) + (x * 4);
            for (var channel = 0; channel < 4; channel++)
            {
                if (sourcePixels[sourceOffset + channel] !=
                    mirroredPixels[mirroredOffset + channel])
                {
                    return false;
                }
            }
        }
    }

    return true;
}

PetGrabHitTestTests.Run();

var projectRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var productionAnimationsRoot = Path.Combine(projectRoot, "Assets", "animations");
Expect(!File.Exists(Path.Combine(projectRoot, "Assets", "spritesheet.png")),
    "legacy mixed image-generated spritesheet must not be present");

var expectedDirectories = new Dictionary<PetMood, string>
{
    [PetMood.Idle] = "idle-video",
    [PetMood.IdleVariant] = "idle-blink-video",
    [PetMood.IdleHeadTilt] = "idle-head-tilt-video",
    [PetMood.IdleHeadTiltAlt] = "idle-head-tilt-alt-video",
    [PetMood.IdleChinShake] = "idle-chin-shake-video",
    [PetMood.MessageFeedback] = "message-feedback-video",
    [PetMood.ClickFlickFall] = "click-flick-fall",
    [PetMood.CuteAngry] = "cute-angry-video",
    [PetMood.HeadPatHappy] = "head-pat-happy-video",
    [PetMood.TaskCelebrate] = "task-celebrate-video",
    [PetMood.StandingRamNibble] = "standing-ram-nibble-video",
    [PetMood.SelfPlayPeekaboo] = "self-play-peekaboo-video",
    [PetMood.SelfPlayAirplane] = "self-play-airplane-video",
    [PetMood.SelfPlayTail] = "self-play-tail-video",
    [PetMood.RecycleBinPeek] = "recycle-bin-peek-video",
    [PetMood.DragLanding] = "drag-soft-landing-video",
    [PetMood.SeatedDoubleCheekCute] = "seated-double-cheek-cute-video",
    [PetMood.Dragged] = "dragged",
    [PetMood.DraggedWingPout] = "dragged-wing-pout",
    [PetMood.DraggedHeadAngry] = "dragged-head-angry",
    [PetMood.Walking] = "walking-right-video",
    [PetMood.WalkingLeft] = "walking-left-video",
    [PetMood.ClimbingUp] = "climbing-up",
    [PetMood.JumpingDown] = "jumping-down",
    [PetMood.PantingAfterEffort] = "panting-after-effort",
    [PetMood.RunFallRight] = "run-fall-right",
    [PetMood.RunFallLeft] = "run-fall-left",
    [PetMood.SeatedSitDown] = "seated-sit-down-video",
    [PetMood.SeatedIdle] = "seated-idle-video",
    [PetMood.SeatedHeadTilt] = "seated-head-tilt-video",
    [PetMood.SeatedBlink] = "seated-blink-video",
    [PetMood.SeatedCoverHead] = "seated-cover-head-video",
    [PetMood.SeatedChinShake] = "seated-chin-shake-video",
    [PetMood.SeatedStandUp] = "seated-stand-up-video"
};
var optionalWorkloadDirectories = new Dictionary<PetMood, string>
{
    [PetMood.WorkloadConfident] = "workload-confident-video",
    [PetMood.WorkloadPanting] = "workload-panting-video",
    [PetMood.WorkloadWipeSweat] = "workload-wipe-sweat-video",
    [PetMood.WorkloadComputerEnter] = "workload-computer-enter-video",
    [PetMood.WorkloadCryTyping] = "workload-cry-typing-video",
    [PetMood.WorkloadWipeTears] = "workload-wipe-tears-video",
    [PetMood.WorkloadDeskSlump] = "workload-desk-slump-video",
    [PetMood.WorkloadDeskBonk] = "workload-desk-bonk-video",
    [PetMood.WorkloadComputerExit] = "workload-computer-exit-video",
    [PetMood.WorkloadCollapse] = "workload-collapse-video",
    [PetMood.WorkloadStandUp] = "workload-stand-up-video"
};
Expect(PetAnimations.WorkloadMoods.ToHashSet().SetEquals(optionalWorkloadDirectories.Keys),
    "the gated workload pack must include all entry, loop, interlude, exit and recovery clips");
Expect(PetAnimations.ExternalFrameDirectories.Count == expectedDirectories.Count + optionalWorkloadDirectories.Count &&
       expectedDirectories.Concat(optionalWorkloadDirectories).All(pair =>
           PetAnimations.ExternalFrameDirectories.TryGetValue(pair.Key, out var actual) &&
           actual == pair.Value),
    "video animation directory mappings changed");
var expectedWarmUpOrder = new[]
{
    PetMood.Dragged,
    PetMood.DraggedWingPout,
    PetMood.DraggedHeadAngry,
    PetMood.ClickFlickFall,
    PetMood.CuteAngry,
    PetMood.HeadPatHappy,
    PetMood.TaskCelebrate,
    PetMood.StandingRamNibble,
    PetMood.SelfPlayPeekaboo,
    PetMood.SelfPlayAirplane,
    PetMood.SelfPlayTail,
    PetMood.RecycleBinPeek,
    PetMood.Idle,
    PetMood.IdleVariant,
    PetMood.IdleHeadTilt,
    PetMood.IdleHeadTiltAlt,
    PetMood.IdleChinShake,
    PetMood.Walking,
    PetMood.WalkingLeft,
    PetMood.ClimbingUp,
    PetMood.JumpingDown,
    PetMood.PantingAfterEffort,
    PetMood.RunFallRight,
    PetMood.RunFallLeft,
    PetMood.SeatedSitDown,
    PetMood.SeatedIdle,
    PetMood.SeatedHeadTilt,
    PetMood.SeatedBlink,
    PetMood.SeatedCoverHead,
    PetMood.SeatedChinShake,
    PetMood.SeatedDoubleCheekCute,
    PetMood.SeatedStandUp,
    PetMood.MessageFeedback
};
Expect(PetAnimations.AnimationWarmUpMoods.SequenceEqual(expectedWarmUpOrder) &&
       PetAnimations.AnimationWarmUpMoods.Distinct().Count() ==
       PetAnimations.AnimationWarmUpMoods.Count &&
       PetAnimations.AnimationWarmUpMoods.ToHashSet().SetEquals(expectedDirectories.Keys.Where(mood => mood != PetMood.DragLanding)),
    "background warm-up must cover every active video, excluding retired landing");
Expect(PetAnimations.AnimationPreviewOptions.Count == expectedDirectories.Count - 1 &&
       PetAnimations.AnimationPreviewOptions.Select(option => option.Mood).Distinct().Count() ==
       PetAnimations.AnimationPreviewOptions.Count &&
       expectedDirectories.Keys.Where(mood => mood != PetMood.DragLanding).All(mood =>
           PetAnimations.AnimationPreviewOptions.Any(option => option.Mood == mood)),
    "animation preview catalog must expose each active video once, without retired landing");
Expect(PetAnimations.AnimationPreviewOptions.All(option =>
           !string.IsNullOrWhiteSpace(option.DisplayName) &&
           option.HelpText.Contains(option.Mood.ToString(), StringComparison.Ordinal)) &&
       Enum.GetValues<PetAnimationPreviewCategory>().All(category =>
           PetAnimations.AnimationPreviewOptions.Any(option => option.Category == category)),
    "animation preview entries must have developer-readable labels and complete grouping");
Expect(PetAnimations.AnimationPreviewOptions.Single(option =>
           option.Mood == PetMood.ClickFlickFall).HelpText.Contains(
           "纯素材预览，不代表真实独占交互",
           StringComparison.Ordinal),
    "click settings preview must not claim to reproduce the real exclusive interaction");
Expect(PetAnimations.AnimationPreviewOptions
           .Where(option => option.Mood is PetMood.Walking or PetMood.WalkingLeft)
           .All(option => option.HelpText.Contains("四轮完整", StringComparison.Ordinal)) &&
       PetAnimations.GetAnimationPreviewCycleCount(PetMood.Walking) == 4 &&
       PetAnimations.GetAnimationPreviewCycleCount(PetMood.WalkingLeft) == 4,
    "walking settings previews must advertise and play four complete cycles");

var expectedFallbacks = new Dictionary<PetMood, PetMood>
{
    [PetMood.Waving] = PetMood.Idle,
    [PetMood.Jumping] = PetMood.Idle,
    [PetMood.Failed] = PetMood.Idle,
    [PetMood.Waiting] = PetMood.Idle,
    [PetMood.Working] = PetMood.Idle,
    [PetMood.Review] = PetMood.Idle
};
Expect(PetAnimations.VisualFallbackMoods.Count == expectedFallbacks.Count &&
       expectedFallbacks.All(pair =>
           PetAnimations.ResolveVisualMood(pair.Key) == pair.Value),
    "legacy image-generated moods must resolve to the video idle sequence");

Expect(PetAnimations.DragMoods.Count == 3 &&
       PetAnimations.DragMoods.Distinct().Count() == 3 &&
       PetAnimations.DragMoods.Contains(PetMood.Dragged) &&
       PetAnimations.DragMoods.Contains(PetMood.DraggedWingPout) &&
       PetAnimations.DragMoods.Contains(PetMood.DraggedHeadAngry),
    "drag mood set must contain each semantic drag animation exactly once");
Expect(PetAnimations.DragMoodForGrabRegion(PetGrabRegion.Head) == PetMood.DraggedHeadAngry &&
       PetAnimations.DragMoodForGrabRegion(PetGrabRegion.Wing) == PetMood.DraggedWingPout &&
       PetAnimations.DragMoodForGrabRegion(PetGrabRegion.Body) == PetMood.Dragged,
    "semantic grab regions must map to their matching drag animations");
Expect(PetAnimations.IdleGestureMoods.Count == 8 &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.IdleVariant) &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.IdleHeadTilt) &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.IdleHeadTiltAlt) &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.IdleChinShake) &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.StandingRamNibble) &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.SelfPlayPeekaboo) &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.SelfPlayAirplane) &&
       PetAnimations.IdleGestureMoods.Contains(PetMood.SelfPlayTail) &&
       !PetAnimations.IdleGestureMoods.Contains(PetMood.RecycleBinPeek) &&
       PetAnimations.IdleGestureMoods.All(PetAnimations.IsIdleGestureMood),
    "idle pool includes three approved games, but recycle must stay on its cooldown path");
Expect(PetAnimations.SeatedSequenceMoods.Count == 8 &&
       PetAnimations.SeatedSequenceMoods.Distinct().Count() == 8 &&
       PetAnimations.SeatedSequenceMoods.All(PetAnimations.IsSeatedMood) &&
       PetAnimations.SeatedSequenceMoods.Contains(PetMood.SeatedSitDown) &&
       PetAnimations.SeatedSequenceMoods.Contains(PetMood.SeatedIdle) &&
       PetAnimations.SeatedSequenceMoods.Contains(PetMood.SeatedChinShake) &&
       PetAnimations.SeatedSequenceMoods.Contains(PetMood.SeatedStandUp),
    "seated sequence must contain both transitions, seated idle, and all gestures exactly once");
Expect(PetAnimations.SeatedGestureMoods.Count == 5 &&
       PetAnimations.SeatedGestureMoods.Distinct().Count() == 5 &&
       PetAnimations.SeatedGestureMoods.All(PetAnimations.IsSeatedGestureMood) &&
       PetAnimations.SeatedGestureMoods.Contains(PetMood.SeatedChinShake) &&
       PetAnimations.SeatedGestureMoods.Contains(PetMood.SeatedDoubleCheekCute) &&
       !PetAnimations.IsSeatedGestureMood(PetMood.SeatedIdle) &&
       !PetAnimations.IsSeatedGestureMood(PetMood.SeatedSitDown) &&
       !PetAnimations.IsSeatedGestureMood(PetMood.SeatedStandUp),
    "seated gesture pool must contain only head tilt, blink, cover-head, and seated chin shake");
var seatedGestureOrders = Enumerable
    .Range(0, PetAnimations.SeatedGestureMoods.Count)
    .SelectMany(firstIndex => new[] { false, true }
        .Select(reverse => PetAnimations.CreateSeatedGestureOrder(firstIndex, reverse)))
    .ToArray();
Expect(seatedGestureOrders.Length == 10 &&
       seatedGestureOrders.All(order =>
           order.Count == 5 &&
           order.Distinct().Count() == 5 &&
           order.All(PetAnimations.IsSeatedGestureMood)) &&
       seatedGestureOrders.Select(order => string.Join(",", order)).Distinct().Count() == 10,
    "seated routine must be able to play all ten non-repeating cyclic gesture orders");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CreateSeatedGestureOrder(-1, reverse: false),
    "seated gesture order accepted a negative first index");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CreateSeatedGestureOrder(5, reverse: false),
    "seated gesture order accepted an out-of-range first index");
var seatedGestureRounds = PetAnimations.CreateSeatedGestureRounds(
    roundCount: 3,
    firstGestureIndex: 1,
    reverseFirstRound: false);
Expect(seatedGestureRounds.Count == 3 &&
       seatedGestureRounds.All(round =>
           round.Count == PetAnimations.SeatedGestureMoods.Count &&
           round.Distinct().Count() == PetAnimations.SeatedGestureMoods.Count &&
           round.All(PetAnimations.IsSeatedGestureMood)) &&
       seatedGestureRounds
           .Select(round => string.Join(",", round))
           .Distinct()
           .Count() == 3 &&
       Enumerable.Range(1, seatedGestureRounds.Count - 1)
           .All(index =>
               seatedGestureRounds[index - 1][^1] !=
               seatedGestureRounds[index][0]),
    "a seated visit must retain every full gesture in each of its two-to-three rounds");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CreateSeatedGestureRounds(0, 0, reverseFirstRound: false),
    "seated gesture rounds accepted an empty visit");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CreateSeatedGestureRounds(2, 5, reverseFirstRound: false),
    "seated gesture rounds accepted an invalid first gesture index");
var seatedAmbientMoods = new[]
{
    PetMood.Idle,
    PetMood.Working,
    PetMood.Review,
    PetMood.Waiting,
    PetMood.Failed
};
Expect(seatedAmbientMoods.All(PetSeatedRoutinePolicy.IsAmbientIdleMood) &&
       seatedAmbientMoods.All(persistentMood =>
           seatedAmbientMoods.All(currentMood =>
               PetSeatedRoutinePolicy.CanRunSeatedRoutine(
                   persistentMood,
                   currentMood))) &&
       !PetSeatedRoutinePolicy.IsAmbientIdleMood(PetMood.ClickFlickFall),
    "seated runtime eligibility must follow the explicit ambient idle fallback family");
Expect(PetSeatedRoutinePolicy.CalculateDelay(
           PetSeatedRoutineScheduleKind.Initial,
           0) == TimeSpan.FromSeconds(8) &&
       PetSeatedRoutinePolicy.CalculateDelay(
           PetSeatedRoutineScheduleKind.Initial,
           1) == TimeSpan.FromSeconds(15) &&
       PetSeatedRoutinePolicy.CalculateDelay(
           PetSeatedRoutineScheduleKind.Recurring,
           0) == TimeSpan.FromSeconds(55) &&
       PetSeatedRoutinePolicy.CalculateDelay(
           PetSeatedRoutineScheduleKind.Recurring,
           1) == TimeSpan.FromSeconds(85),
    "seated scheduling must use 8-15 seconds initially and 55-85 seconds thereafter");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetSeatedRoutinePolicy.CalculateDelay(
        PetSeatedRoutineScheduleKind.Initial,
        -0.01),
    "seated delay accepted a random sample below zero");
var retainedSeatedDeadline = PetSeatedRoutinePolicy.PreserveScheduledDeadline(
    10_000,
    80_000);
Expect(retainedSeatedDeadline == 10_000 &&
       PetSeatedRoutinePolicy.IsDeadlineDue(10_001, retainedSeatedDeadline) &&
       PetSeatedRoutinePolicy.SelectBoundaryAction(
           PetIdleBoundaryAction.AutonomousMovement,
           seatedDeadlineDue: true,
           canStartSeatedRoutine: true,
           idleGestureDue: true,
           autonomousActionDue: true,
           canStartOrdinaryIdleAction: true) == PetIdleBoundaryAction.SeatedRoutine,
    "an overdue seated deadline must survive postponement and win the next idle boundary");
Expect(PetSeatedRoutinePolicy.SelectBoundaryAction(
           PetIdleBoundaryAction.IdleGesture,
           seatedDeadlineDue: false,
           canStartSeatedRoutine: false,
           idleGestureDue: true,
           autonomousActionDue: true,
           canStartOrdinaryIdleAction: true) == PetIdleBoundaryAction.AutonomousMovement,
    "an overdue movement must replace a not-yet-visible quiet gesture");
Expect(Math.Abs(PetAnimations.CalculateIdleGestureDelay(0).TotalMilliseconds - 1000) < 0.001 &&
       Math.Abs(PetAnimations.CalculateIdleGestureDelay(0.5).TotalMilliseconds - 2000) < 0.001 &&
       Math.Abs(PetAnimations.CalculateIdleGestureDelay(1).TotalMilliseconds - 3000) < 0.001,
    "base idle must choose the next complete gesture by its first full loop boundary");
Expect(PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0) ==
           PetMood.IdleVariant &&
       PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.30) ==
           PetMood.IdleHeadTilt &&
       PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.45) ==
           PetMood.IdleHeadTiltAlt &&
       PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.55) ==
           PetMood.IdleChinShake &&
       PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.65) ==
           PetMood.StandingRamNibble &&
       PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.75) == PetMood.SelfPlayPeekaboo &&
       PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.85) == PetMood.SelfPlayAirplane &&
       PetAnimations.SelectWeightedIdleGesture(PetAnimations.IdleGestureMoods, 0.95) == PetMood.SelfPlayTail,
    "standing gestures must use the documented weighted random pool");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CalculateIdleGestureDelay(-0.01),
    "idle gesture delay accepted a random sample below zero");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CalculateIdleGestureDelay(1.01),
    "idle gesture delay accepted a random sample above one");
Expect(PetAnimations.CalculatePreviewDuration(47, TimeSpan.FromMilliseconds(80)) ==
       TimeSpan.FromMilliseconds(3760),
    "animation preview duration must cover one complete actual frame sequence");
var seatedExpectedDuration = PetAnimations.CalculatePreviewDuration(
    97,
    PetAnimations.ExternalAnimationFrameDuration);
var seatedWatchdogDuration = PetAnimations.CalculateSeatedAnimationWatchdog(
    97,
    PetAnimations.ExternalAnimationFrameDuration);
Expect(seatedWatchdogDuration - seatedExpectedDuration ==
       PetAnimations.SeatedAnimationWatchdogGrace &&
       PetAnimations.SeatedAnimationWatchdogGrace >= TimeSpan.FromSeconds(2),
    "seated animation watchdog must allow one complete actual sequence plus recovery grace");
var seatedBlinkExpectedDuration = PetAnimations.CalculatePreviewDuration(
    97,
    PetAnimations.ExternalAnimationFrameDuration);
var seatedBlinkWatchdogDuration = PetAnimations.CalculateSeatedAnimationWatchdog(
    97,
    PetAnimations.ExternalAnimationFrameDuration);
Expect(seatedBlinkWatchdogDuration - seatedBlinkExpectedDuration ==
       PetAnimations.SeatedAnimationWatchdogGrace &&
       Math.Abs(seatedBlinkExpectedDuration.TotalSeconds - (97d / 24d)) < 1e-5,
    "seated blink preview and watchdog must use all 97 contiguous frames at native 24fps");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CalculatePreviewDuration(0, TimeSpan.FromMilliseconds(80)),
    "animation preview duration accepted an empty sequence");
ExpectThrows<ArgumentOutOfRangeException>(
    () => PetAnimations.CalculateSeatedAnimationWatchdog(
        0,
        PetAnimations.ExternalAnimationFrameDuration),
    "seated animation watchdog accepted an empty sequence");
Expect(!PetAnimations.DragMoods.Contains(PetMood.ClimbingUp) &&
       !PetAnimations.DragMoods.Contains(PetMood.JumpingDown) &&
       !PetAnimations.DragMoods.Contains(PetMood.PantingAfterEffort) &&
       !PetAnimations.DragMoods.Contains(PetMood.RunFallRight) &&
       !PetAnimations.DragMoods.Contains(PetMood.RunFallLeft),
    "autonomous and post-effort animations must not be selectable as drag moods");
Expect(PetAnimations.DragVisualProfiles.Count == PetAnimations.DragMoods.Count &&
       PetAnimations.DragMoods.All(mood =>
       {
           var profile = PetAnimations.DragVisualProfiles[mood];
           return profile.AnchorX is >= 0 and <= 1 &&
                  profile.AnchorY is >= 0 and <= 1 &&
                  profile.WindowScale > 1;
       }),
    "drag visual profiles must define an in-frame mouse anchor and enlarged scale");
Expect(Math.Abs(PetAnimations.DragVisualProfiles[PetMood.Dragged].WindowScale - 1.34) < 0.0001 &&
       Math.Abs(PetAnimations.DragVisualProfiles[PetMood.DraggedWingPout].WindowScale - 1.32) < 0.0001 &&
       Math.Abs(PetAnimations.DragVisualProfiles[PetMood.DraggedHeadAngry].WindowScale - 1.35) < 0.0001,
    "drag variants must keep their independently head-normalized display scales");
Expect(Math.Abs(PetAnimations.DragVisualProfiles[PetMood.DraggedHeadAngry].AnchorX - 0.495) < 0.0001 &&
       Math.Abs(PetAnimations.DragVisualProfiles[PetMood.DraggedHeadAngry].AnchorY - 0.341) < 0.0001,
    "head drag mouse anchor must stay on the forehead instead of between the horns");
Expect(PetAnimations.Definitions[PetMood.Dragged].FrameCount == 44 &&
       PetAnimations.Definitions[PetMood.DraggedWingPout].FrameCount == 53 &&
       PetAnimations.Definitions[PetMood.DraggedHeadAngry].FrameCount == 45 &&
       PetAnimations.DragMoods.All(mood => PetAnimations.Definitions[mood].Loop),
    "drag definitions must match the complete forward production loops");
Expect(Math.Abs(PetAnimations.ClimbingWindowScale - 1.18) < 0.0001,
    "climbing must retain the head-normalizing window scale that avoids sprite cropping");
Expect(Math.Abs(PetAnimations.JumpingContentScale - 1.28128641) < 0.00000001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           242,
           PetAnimations.JumpingContentScale) - 306.13330206) < 0.000001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           276,
           PetAnimations.JumpingContentScale) - 349.69703942) < 0.000001,
    "downward jump preview and autonomous playback must share the margin-aware content scale");
Expect(Math.Abs(PetAnimations.RunFallContentScale - 1.17) < 0.0001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           242,
           PetAnimations.RunFallContentScale) - 280.76) < 0.000001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           276,
           PetAnimations.RunFallContentScale) - 320.54) < 0.000001,
    "running fall preview and autonomous playback must share the margin-aware two-endpoint content scale");
Expect(PetAnimations.IsRunFallTravelFrame(0) &&
       PetAnimations.IsRunFallTravelFrame(38) &&
       !PetAnimations.IsRunFallTravelFrame(39) &&
       !PetAnimations.IsRunFallTravelFrame(192),
    "running fall desktop travel must stop when the opening run ends");
Expect(!PetAnimations.IsRunFallImpactFrame(38) &&
       PetAnimations.IsRunFallImpactFrame(39) &&
       !PetAnimations.IsRunFallImpactFrame(40),
    "running-fall pain voice must align with the first non-travel impact frame");
Expect(Math.Abs(PetAnimations.SeatedContentScale - 1.1712021321205954) < 1e-12 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           242,
           PetAnimations.SeatedContentScale) - 281.0340869234958) < 0.000001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           276,
           PetAnimations.SeatedContentScale) - 320.8549588155960) < 0.000001,
    "seated endpoints must match idle through a margin-aware family content scale");
Expect(
    Math.Abs(PetAnimations.ExternalAnimationFrameDuration.TotalSeconds - (1d / 24d)) < 1e-7,
    "AI-video motion frame duration changed");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.Idle) ==
       PetAnimations.Definitions[PetMood.Idle].FrameDuration,
    "video idle playback speed changed");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.IdleVariant) ==
       PetAnimations.Definitions[PetMood.IdleVariant].FrameDuration,
    "video blink playback speed changed");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.IdleHeadTilt) ==
       PetAnimations.ExternalAnimationFrameDuration &&
       !PetAnimations.Definitions[PetMood.IdleHeadTilt].Loop,
    "video head tilt must play once at the native AI-video cadence");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.IdleHeadTiltAlt) ==
       PetAnimations.ExternalAnimationFrameDuration &&
       !PetAnimations.Definitions[PetMood.IdleHeadTiltAlt].Loop,
    "alternate video head tilt must play once at the native AI-video cadence");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.IdleChinShake) ==
       PetAnimations.ExternalAnimationFrameDuration &&
       PetAnimations.Definitions[PetMood.IdleChinShake].FrameCount == 121 &&
       !PetAnimations.Definitions[PetMood.IdleChinShake].Loop,
    "chin-shake idle gesture must play all 121 frames once at 24fps");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.MessageFeedback) ==
       PetAnimations.ExternalAnimationFrameDuration &&
       !PetAnimations.Definitions[PetMood.MessageFeedback].Loop,
    "message feedback must play once at the native AI-video cadence");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.ClickFlickFall) ==
       PetAnimations.ClickFlickFallFrameDuration &&
       PetAnimations.Definitions[PetMood.ClickFlickFall].FrameCount == 193 &&
       !PetAnimations.Definitions[PetMood.ClickFlickFall].Loop &&
       PetAnimations.CalculatePreviewDuration(
           193,
           PetAnimations.GetExternalFrameDuration(PetMood.ClickFlickFall)) <=
       PetAnimations.MaximumSingleAnimationDuration &&
       Math.Abs(PetAnimations.ClickFlickFallContentScale - ((213d / 165d) * 0.98)) < 0.0001 &&
       Math.Abs(PetAnimations.MaximumPresentationScale - 1.35d) < 0.0001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           242,
            PetAnimations.ClickFlickFallContentScale) - 302.4407272727273) < 0.000001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           276,
            PetAnimations.ClickFlickFallContentScale) - 345.4538181818182) < 0.000001,
    "click reaction must play all 193 frames once within six seconds with head-matched scale");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.CuteAngry) ==
       PetAnimations.ExternalAnimationFrameDuration &&
       PetAnimations.Definitions[PetMood.CuteAngry].FrameCount == 121 &&
       !PetAnimations.Definitions[PetMood.CuteAngry].Loop &&
       PetAnimations.CalculatePreviewDuration(
           121,
           PetAnimations.GetExternalFrameDuration(PetMood.CuteAngry)) <=
       PetAnimations.MaximumSingleAnimationDuration &&
       Math.Abs(PetAnimations.CuteAngryWindowScale - 1d) < 0.0001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           242,
           PetAnimations.CuteAngryWindowScale) - 242d) < 0.000001 &&
       Math.Abs(PetAnimations.CalculateContentScaledWindowDimension(
           276,
           PetAnimations.CuteAngryWindowScale) - 276d) < 0.000001,
    "cute-angry must play all 121 frames forward once at 24fps within six seconds at the normal resting size");
Expect(PetAnimations.GetExternalFrameDuration(PetMood.Walking) ==
       PetAnimations.Definitions[PetMood.Walking].FrameDuration &&
       PetAnimations.GetExternalFrameDuration(PetMood.WalkingLeft) ==
       PetAnimations.Definitions[PetMood.WalkingLeft].FrameDuration,
    "video walking playback speed changed");
var fullWalkingPlaybackDuration = PetAnimations.CalculatePreviewDuration(
    checked(
        PetAnimations.Definitions[PetMood.Walking].FrameCount *
        PetAnimations.WalkingPlaybackCycleCount),
    PetAnimations.GetExternalFrameDuration(PetMood.Walking));
Expect(PetAnimations.WalkingPlaybackCycleCount == 4 &&
       fullWalkingPlaybackDuration == TimeSpan.FromMilliseconds(5824) &&
       fullWalkingPlaybackDuration < PetAnimations.MaximumSingleAnimationDuration,
    "ordinary and settings walking must play four complete 8-frame cycles in 5.824 seconds");
Expect(PetAnimations.ResolveAutonomousEntryMood(PetMood.RunFallRight, -1) ==
           PetMood.RunFallRight &&
       PetAnimations.ResolveAutonomousEntryMood(PetMood.RunFallLeft, 1) ==
           PetMood.RunFallLeft,
    "run-fall selection must enter the selected one-shot directly without a walking prelude");
Expect(PetAnimations.Definitions[PetMood.Idle].FrameCount == 47 &&
       PetAnimations.Definitions[PetMood.Idle].Loop &&
       Math.Abs(PetAnimations.GetExternalFrameDuration(PetMood.Idle).TotalSeconds - (1d / 12d)) < 1e-7,
    "calm idle must be a 47-frame 12fps loop");
Expect(PetAnimations.Definitions[PetMood.PantingAfterEffort].FrameCount == 53 &&
       !PetAnimations.Definitions[PetMood.PantingAfterEffort].Loop &&
       Math.Abs(PetAnimations.GetExternalFrameDuration(PetMood.PantingAfterEffort).TotalSeconds - (1d / 24d)) < 1e-7,
    "post-effort panting must play 53 frames once at 24fps");
Expect(PetAnimations.Definitions[PetMood.RunFallRight].FrameCount == 193 &&
       !PetAnimations.Definitions[PetMood.RunFallRight].Loop &&
       PetAnimations.GetExternalFrameDuration(PetMood.RunFallRight) ==
       PetAnimations.RunFallFrameDuration &&
       PetAnimations.CalculatePreviewDuration(
           193,
           PetAnimations.GetExternalFrameDuration(PetMood.RunFallRight)) <=
       PetAnimations.MaximumSingleAnimationDuration &&
       PetAnimations.MaximumSingleAnimationDuration -
       PetAnimations.CalculatePreviewDuration(
           193,
           PetAnimations.GetExternalFrameDuration(PetMood.RunFallRight)) <
       PetAnimations.RunFallFrameDuration,
    "right-running fall must play all 193 decoded attachment frames once within six seconds");
Expect(PetAnimations.Definitions[PetMood.RunFallLeft].FrameCount == 193 &&
       !PetAnimations.Definitions[PetMood.RunFallLeft].Loop &&
       PetAnimations.GetExternalFrameDuration(PetMood.RunFallLeft) ==
       PetAnimations.RunFallFrameDuration &&
       PetAnimations.CalculatePreviewDuration(
           193,
           PetAnimations.GetExternalFrameDuration(PetMood.RunFallLeft)) <=
       PetAnimations.MaximumSingleAnimationDuration &&
       PetAnimations.MaximumSingleAnimationDuration -
       PetAnimations.CalculatePreviewDuration(
           193,
           PetAnimations.GetExternalFrameDuration(PetMood.RunFallLeft)) <
       PetAnimations.RunFallFrameDuration,
    "left-running fall must play all 193 mirrored frames once within six seconds");
Expect(PetAnimations.IsRunFallRightAutonomousCandidate(false, 80, true) &&
       PetAnimations.IsRunFallRightAutonomousCandidate(false, 120, true) &&
       !PetAnimations.IsRunFallRightAutonomousCandidate(false, 79.999, true) &&
       !PetAnimations.IsRunFallRightAutonomousCandidate(false, 120, false) &&
       !PetAnimations.IsRunFallRightAutonomousCandidate(true, 120, true),
    "right-running fall must be an ordinary deterministic candidate with only preview, asset, and right-space gates");
Expect(PetAnimations.IsRunFallLeftAutonomousCandidate(false, 80, true) &&
       PetAnimations.IsRunFallLeftAutonomousCandidate(false, 120, true) &&
       !PetAnimations.IsRunFallLeftAutonomousCandidate(false, 79.999, true) &&
       !PetAnimations.IsRunFallLeftAutonomousCandidate(false, 120, false) &&
       !PetAnimations.IsRunFallLeftAutonomousCandidate(true, 120, true),
    "left-running fall must be an ordinary deterministic candidate with only preview, asset, and left-space gates");
var seatedVideosAt24Fps = PetAnimations.SeatedSequenceMoods.ToArray();
Expect(seatedVideosAt24Fps.Length == 8 &&
       seatedVideosAt24Fps.All(mood =>
           PetAnimations.GetExternalFrameDuration(mood) ==
           PetAnimations.ExternalAnimationFrameDuration) &&
       seatedVideosAt24Fps.Where(mood => mood is not PetMood.SeatedChinShake and not PetMood.SeatedDoubleCheekCute)
           .All(mood => PetAnimations.Definitions[mood].FrameCount == 97) &&
       PetAnimations.Definitions[PetMood.SeatedChinShake].FrameCount == 121 &&
       PetAnimations.Definitions[PetMood.SeatedDoubleCheekCute].FrameCount == 125,
    "all eight seated videos must use 24fps while preserving all source frames");
Expect(PetAnimations.Definitions[PetMood.SeatedBlink].FrameCount == 97 &&
       PetAnimations.GetExternalFrameDuration(PetMood.SeatedBlink) ==
       PetAnimations.ExternalAnimationFrameDuration &&
       !PetAnimations.Definitions[PetMood.SeatedBlink].Loop,
    "seated blink must play all 97 contiguous frames forward once at native 24fps");
Expect(PetAnimations.Definitions[PetMood.SeatedIdle].Loop &&
       PetAnimations.SeatedSequenceMoods
           .Where(mood => mood != PetMood.SeatedIdle)
           .All(mood => !PetAnimations.Definitions[mood].Loop),
    "only seated idle may loop; seated transitions and gestures must play forward once");
Expect(PetAnimations.Definitions[PetMood.ClimbingUp].FrameCount == 49 &&
       PetAnimations.Definitions[PetMood.ClimbingUp].Loop,
    "climbing animation must use its complete 49-frame loop while the window moves upward");
Expect(PetAnimations.ClimbingAutonomousCycleCount == 2,
    "autonomous climbing must play two complete forward cycles");
Expect(!PetAnimations.Definitions[PetMood.JumpingDown].Loop,
    "downward jump must play once without ping-pong or reverse playback");
Expect(PetAnimations.Definitions[PetMood.JumpingDown].FrameCount == 97 &&
       Math.Abs(PetAnimations.GetExternalFrameDuration(PetMood.JumpingDown).TotalSeconds - (1d / 24d)) < 1e-7,
    "downward jump must play all 97 source frames once at 24fps");
Expect(PetAnimations.JumpingDownTravelStartFrame == 38 &&
       PetAnimations.JumpingDownTravelEndFrameExclusive == 71 &&
       PetAnimations.JumpingDownTravelEndFrameExclusive -
       PetAnimations.JumpingDownTravelStartFrame == 33,
    "downward window travel must be limited to the 33 airborne/downward frames");
Expect(!PetAnimations.IsJumpingDownTravelFrame(37) &&
       PetAnimations.IsJumpingDownTravelFrame(38) &&
       PetAnimations.IsJumpingDownTravelFrame(70) &&
       !PetAnimations.IsJumpingDownTravelFrame(71),
    "downward travel frame gate must exclude anticipation and landing recovery");

var fullSpeed = PetAnimations.FitMovementSpeedToDistance(
    40,
    200,
    TimeSpan.FromSeconds(2));
Expect(Math.Abs(fullSpeed - 40) < 0.0001,
    "movement speed was reduced despite sufficient travel distance");
var fittedSpeed = PetAnimations.FitMovementSpeedToDistance(
    90,
    72,
    TimeSpan.FromSeconds(3));
Expect(Math.Abs(fittedSpeed - (70d / 3d)) < 0.0001,
    "movement speed did not fit the full animation inside the available distance");

var productionSequences = new SpriteAtlas(productionAnimationsRoot);
var expectedProductionCounts = new Dictionary<PetMood, int>
{
    [PetMood.Idle] = 47,
    [PetMood.IdleVariant] = 53,
    [PetMood.IdleHeadTilt] = 97,
    [PetMood.IdleHeadTiltAlt] = 97,
    [PetMood.IdleChinShake] = 121,
    [PetMood.MessageFeedback] = 97,
    [PetMood.ClickFlickFall] = 193,
    [PetMood.CuteAngry] = 121,
    [PetMood.HeadPatHappy] = 121,
    [PetMood.TaskCelebrate] = 121,
    [PetMood.StandingRamNibble] = 121,
    [PetMood.SelfPlayPeekaboo] = 141,
    [PetMood.SelfPlayAirplane] = 141,
    [PetMood.SelfPlayTail] = 141,
    [PetMood.RecycleBinPeek] = 141,
    [PetMood.DragLanding] = 97,
    [PetMood.SeatedDoubleCheekCute] = 125,
    [PetMood.Dragged] = 44,
    [PetMood.DraggedWingPout] = 53,
    [PetMood.DraggedHeadAngry] = 45,
    [PetMood.Walking] = 8,
    [PetMood.WalkingLeft] = 8,
    [PetMood.ClimbingUp] = 49,
    [PetMood.JumpingDown] = 97,
    [PetMood.PantingAfterEffort] = 53,
    [PetMood.RunFallRight] = 193,
    [PetMood.RunFallLeft] = 193,
    [PetMood.SeatedSitDown] = 97,
    [PetMood.SeatedIdle] = 97,
    [PetMood.SeatedHeadTilt] = 97,
    [PetMood.SeatedBlink] = 97,
    [PetMood.SeatedCoverHead] = 97,
    [PetMood.SeatedChinShake] = 121,
    [PetMood.SeatedStandUp] = 97
};
foreach (var (mood, expectedCount) in expectedProductionCounts)
{
    Expect(productionSequences.UsesExternalFrames(mood),
        $"production video sequence for {mood} was rejected");
    Expect(productionSequences.GetFrameCount(mood) == expectedCount,
        $"production video sequence for {mood} had the wrong frame count");
    Expect(PetAnimations.Definitions[mood].FrameCount == expectedCount,
        $"declared frame count for {mood} did not match the production directory");
    Expect(productionSequences.GetFrameDuration(mood) ==
           PetAnimations.GetExternalFrameDuration(mood),
        $"production video sequence for {mood} had the wrong frame duration");
    Expect(PetAnimations.CalculatePreviewDuration(
               expectedCount,
               productionSequences.GetFrameDuration(mood)) <=
           PetAnimations.MaximumSingleAnimationDuration,
        $"production video sequence for {mood} exceeds the six-second runtime cap");
    var lastFrame = productionSequences.GetFrame(mood, expectedCount - 1);
    Expect(lastFrame.PixelWidth == PetAnimations.CellWidth &&
           lastFrame.PixelHeight == PetAnimations.CellHeight,
        $"production video sequence for {mood} had a bad final frame size");
}

for (var frameIndex = 0; frameIndex < 193; frameIndex++)
{
    Expect(IsExactHorizontalMirror(
            productionSequences.GetFrame(PetMood.RunFallRight, frameIndex),
            productionSequences.GetFrame(PetMood.RunFallLeft, frameIndex)),
        $"left-running fall frame {frameIndex:00} was not the exact per-frame horizontal mirror");
}

foreach (var (mood, fallbackMood) in expectedFallbacks)
{
    Expect(!productionSequences.UsesExternalFrames(mood),
        $"{mood} unexpectedly has a dedicated sequence instead of the approved fallback");
    Expect(productionSequences.GetFrameCount(mood) ==
           productionSequences.GetFrameCount(fallbackMood),
        $"{mood} did not inherit the video idle frame count");
    Expect(ReferenceEquals(
            productionSequences.GetFrame(mood, 0),
            productionSequences.GetFrame(fallbackMood, 0)),
        $"{mood} did not render the exact video idle frame");
}

var harnessRoot = Path.Combine(
    projectRoot,
    "obj",
    "AnimationSequenceHarnessData",
    $"run-{Guid.NewGuid():N}");
Directory.CreateDirectory(harnessRoot);
try
{
    var missingRoot = Path.Combine(harnessRoot, "missing");
    var missing = new SpriteAtlas(missingRoot);
    Expect(!missing.UsesExternalFrames(PetMood.Dragged),
        "missing video sequence was reported as available");
    ExpectThrows<InvalidDataException>(
        () => missing.GetFrameCount(PetMood.Dragged),
        "missing required video sequence did not fail clearly");

    var animationsRoot = Path.Combine(harnessRoot, "animations");
    var idleDirectory = Path.Combine(
        animationsRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.Idle]);
    Directory.CreateDirectory(idleDirectory);
    WritePng(Path.Combine(idleDirectory, "00.png"), 384, 416, Colors.Orange);
    WritePng(Path.Combine(idleDirectory, "01.png"), 384, 416, Colors.Gold);
    WritePng(Path.Combine(idleDirectory, "02.png"), 384, 416, Colors.Yellow);

    var draggedDirectory = Path.Combine(
        animationsRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.Dragged]);
    Directory.CreateDirectory(draggedDirectory);
    WritePng(Path.Combine(draggedDirectory, "frame-10.png"), 384, 416, Colors.Blue);
    WritePng(Path.Combine(draggedDirectory, "frame-2.png"), 384, 416, Colors.Green);
    WritePng(Path.Combine(draggedDirectory, "frame-1.png"), 384, 416, Colors.Red);

    var invalidWingDirectory = Path.Combine(
        animationsRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.DraggedWingPout]);
    Directory.CreateDirectory(invalidWingDirectory);
    WritePng(Path.Combine(invalidWingDirectory, "00.png"), 383, 416, Colors.Purple);

    var sequences = new SpriteAtlas(animationsRoot);
    Expect(sequences.UsesExternalFrames(PetMood.Dragged),
        "valid video sequence was not selected");
    Expect(sequences.GetFrameCount(PetMood.Dragged) == 3,
        "video sequence frame count was wrong");
    Expect(ReadFirstPixel(sequences.GetFrame(PetMood.Dragged, 0)) == Colors.Red &&
           ReadFirstPixel(sequences.GetFrame(PetMood.Dragged, 1)) == Colors.Green &&
           ReadFirstPixel(sequences.GetFrame(PetMood.Dragged, 2)) == Colors.Blue,
        "video sequence natural filename ordering was wrong");

    Directory.Delete(draggedDirectory, recursive: true);
    Expect(ReadFirstPixel(sequences.GetFrame(PetMood.Dragged, 2)) == Colors.Blue,
        "video frames were not cached after first lazy load");

    Expect(!sequences.UsesExternalFrames(PetMood.DraggedWingPout),
        "invalid-size video sequence was reported as available");
    ExpectThrows<InvalidDataException>(
        () => sequences.GetFrameCount(PetMood.DraggedWingPout),
        "invalid-size required video sequence did not fail clearly");
    Expect(!sequences.UsesExternalFrames(PetMood.DraggedHeadAngry),
        "missing head-drag video sequence was reported as available");
    ExpectThrows<InvalidDataException>(
        () => sequences.GetFrameCount(PetMood.DraggedHeadAngry),
        "missing head-drag video sequence did not fail clearly");

    Expect(sequences.GetFrameCount(PetMood.Waving) == 3,
        "legacy visual fallback did not inherit the video idle sequence");
    Expect(ReferenceEquals(
            sequences.GetFrame(PetMood.Waving, 1),
            sequences.GetFrame(PetMood.Idle, 1)),
        "legacy visual fallback did not use the exact video idle frame");

    Console.WriteLine("ANIMATION_SEQUENCE_HARNESS_OK");
}
finally
{
    var expectedParent = Path.GetFullPath(Path.Combine(
        projectRoot,
        "obj",
        "AnimationSequenceHarnessData"));
    var resolved = Path.GetFullPath(harnessRoot);
    if (resolved.StartsWith(
            expectedParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(resolved).StartsWith("run-", StringComparison.Ordinal))
    {
        Directory.Delete(resolved, recursive: true);
    }
}
