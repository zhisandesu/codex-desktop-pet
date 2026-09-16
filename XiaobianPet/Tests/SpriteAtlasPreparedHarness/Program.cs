using System.IO;
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

static TException CaptureThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException(message);
}

static void WritePng(string path, Color color)
{
    var pixels = new byte[checked(PetAnimations.CellWidth * PetAnimations.CellHeight * 4)];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = color.B;
        pixels[index + 1] = color.G;
        pixels[index + 2] = color.R;
        pixels[index + 3] = color.A;
    }

    var bitmap = BitmapSource.Create(
        PetAnimations.CellWidth,
        PetAnimations.CellHeight,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        pixels,
        PetAnimations.CellWidth * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static Color ReadFirstPixel(BitmapSource source)
{
    var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
    var pixel = new byte[4];
    converted.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
    return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
}

var harnessRoot = Path.Combine(
    Path.GetTempPath(),
    "XiaobianPet-SpriteAtlasPreparedHarness",
    $"run-{Guid.NewGuid():N}");
Directory.CreateDirectory(harnessRoot);

try
{
    var exactLimitDuration = ExternalAnimationDurationPolicy.Validate(
        PetMood.Idle,
        actualFrameCount: 24,
        frameDuration: TimeSpan.FromMilliseconds(250));
    Expect(exactLimitDuration == PetAnimations.MaximumSingleAnimationDuration,
        "an external animation exactly at the six-second limit was rejected");

    var overLimitError = CaptureThrows<InvalidDataException>(
        () => ExternalAnimationDurationPolicy.Validate(
            PetMood.Idle,
            actualFrameCount: 25,
            frameDuration: TimeSpan.FromMilliseconds(250)),
        "an external animation over the six-second limit was accepted");
    Expect(overLimitError.Message.Contains(nameof(PetMood.Idle), StringComparison.Ordinal) &&
           overLimitError.Message.Contains("25 帧", StringComparison.Ordinal) &&
           overLimitError.Message.Contains("6.250 秒", StringComparison.Ordinal) &&
           overLimitError.Message.Contains("最长 6.000 秒", StringComparison.Ordinal),
        "the over-limit rejection did not clearly report mood, actual frames, duration, and limit");

    var animationsRoot = Path.Combine(harnessRoot, "animations");
    Directory.CreateDirectory(animationsRoot);

    var draggedDirectory = Path.Combine(
        animationsRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.Dragged]);
    Directory.CreateDirectory(draggedDirectory);
    WritePng(Path.Combine(draggedDirectory, "frame-10.png"), Colors.Blue);
    WritePng(Path.Combine(draggedDirectory, "frame-2.png"), Colors.Green);
    WritePng(Path.Combine(draggedDirectory, "frame-1.png"), Colors.Red);

    var atlas = new SpriteAtlas(animationsRoot);
    var prepared = atlas.PrepareSequence(PetMood.Dragged);

    Expect(prepared.RequestedMood == PetMood.Dragged &&
           prepared.VisualMood == PetMood.Dragged,
        "prepared sequence did not retain requested and visual moods");
    Expect(prepared.FrameDuration ==
           PetAnimations.GetExternalFrameDuration(PetMood.Dragged),
        "prepared sequence did not retain the resolved frame duration");
    Expect(prepared.Frames.Count == 3,
        "prepared sequence did not contain the complete directory");
    Expect(prepared.Frames.All(frame =>
            frame.IsFrozen &&
            frame.PixelWidth == PetAnimations.CellWidth &&
            frame.PixelHeight == PetAnimations.CellHeight),
        "prepared frames were not validated and frozen");
    Expect(prepared.Frames is IList<BitmapSource> { IsReadOnly: true },
        "prepared frame snapshot exposed a mutable collection");
    ExpectThrows<NotSupportedException>(
        () => ((IList<BitmapSource>)prepared.Frames).RemoveAt(0),
        "prepared frame snapshot allowed collection mutation");
    Expect(ReadFirstPixel(prepared.Frames[0]) == Colors.Red &&
           ReadFirstPixel(prepared.Frames[1]) == Colors.Green &&
           ReadFirstPixel(prepared.Frames[2]) == Colors.Blue,
        "prepared frames did not use natural filename ordering");
    Expect(ReferenceEquals(prepared, atlas.PrepareSequence(PetMood.Dragged)),
        "PrepareSequence did not return its cached snapshot");

    Directory.Delete(draggedDirectory, recursive: true);
    Expect(ReferenceEquals(prepared, atlas.PrepareSequence(PetMood.Dragged)) &&
           ReferenceEquals(prepared.Frames[2], atlas.GetFrame(PetMood.Dragged, 2)) &&
           ReadFirstPixel(atlas.GetFrame(PetMood.Dragged, 2)) == Colors.Blue,
        "a prepared sequence still depended on its deleted source directory");

    var idleDirectory = Path.Combine(
        animationsRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.Idle]);
    Directory.CreateDirectory(idleDirectory);
    WritePng(Path.Combine(idleDirectory, "00.png"), Colors.Orange);
    WritePng(Path.Combine(idleDirectory, "01.png"), Colors.Gold);

    var fallback = atlas.PrepareSequence(PetMood.Waving);
    var idle = atlas.PrepareSequence(PetMood.Idle);
    Expect(fallback.RequestedMood == PetMood.Waving &&
           fallback.VisualMood == PetMood.Idle &&
           fallback.FrameDuration == PetAnimations.GetExternalFrameDuration(PetMood.Idle),
        "prepared fallback sequence did not expose its request and resolved visual mood");
    Expect(ReferenceEquals(fallback.Frames, idle.Frames) &&
           !atlas.UsesExternalFrames(PetMood.Waving),
        "prepared fallback did not share the resolved cached frame snapshot");

    var brokenDirectory = Path.Combine(
        animationsRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.DraggedWingPout]);
    Directory.CreateDirectory(brokenDirectory);
    var firstPath = Path.Combine(brokenDirectory, "00.png");
    var middlePath = Path.Combine(brokenDirectory, "01.png");
    WritePng(firstPath, Colors.Red);
    File.WriteAllBytes(middlePath, [0x00, 0x01, 0x02, 0x03]);
    WritePng(Path.Combine(brokenDirectory, "02.png"), Colors.Blue);

    ExpectThrows<InvalidDataException>(
        () => atlas.PrepareSequence(PetMood.DraggedWingPout),
        "a corrupt middle frame did not reject the complete prepared sequence");
    Expect(!atlas.UsesExternalFrames(PetMood.DraggedWingPout),
        "a failed partial sequence was published as available");

    // A failed attempt must not cache its already-decoded prefix. Replacing both
    // the prefix and corrupt middle frame must yield one fresh, complete snapshot.
    WritePng(firstPath, Colors.Yellow);
    WritePng(middlePath, Colors.Green);
    var repaired = atlas.PrepareSequence(PetMood.DraggedWingPout);
    Expect(repaired.Frames.Count == 3 &&
           repaired.Frames.All(frame => frame.IsFrozen) &&
           ReadFirstPixel(repaired.Frames[0]) == Colors.Yellow &&
           ReadFirstPixel(repaired.Frames[1]) == Colors.Green &&
           ReadFirstPixel(repaired.Frames[2]) == Colors.Blue,
        "a failed load exposed or cached a partial prepared sequence");

    var concurrentRoot = Path.Combine(harnessRoot, "concurrent-animations");
    var slowClickDirectory = Path.Combine(
        concurrentRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.ClickFlickFall]);
    var fastGrabDirectory = Path.Combine(
        concurrentRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.DraggedHeadAngry]);
    Directory.CreateDirectory(slowClickDirectory);
    Directory.CreateDirectory(fastGrabDirectory);
    WritePng(Path.Combine(slowClickDirectory, "00.png"), Colors.Red);
    WritePng(Path.Combine(slowClickDirectory, "01.png"), Colors.Green);
    WritePng(Path.Combine(slowClickDirectory, "02.png"), Colors.Blue);
    WritePng(Path.Combine(fastGrabDirectory, "00.png"), Colors.Purple);

    using var clickDecodeEntered = new ManualResetEventSlim();
    using var releaseClickDecode = new ManualResetEventSlim();
    var concurrentAtlas = new SpriteAtlas(
        concurrentRoot,
        (mood, decodedFrameCount) =>
        {
            if (mood == PetMood.ClickFlickFall && decodedFrameCount == 1)
            {
                clickDecodeEntered.Set();
                releaseClickDecode.Wait(TimeSpan.FromSeconds(5));
            }
        });

    var clickLoad = Task.Run(() =>
        concurrentAtlas.PrepareSequence(PetMood.ClickFlickFall));
    Expect(clickDecodeEntered.Wait(TimeSpan.FromSeconds(5)),
        "click preparation never reached its first decoded frame");

    var grabLoad = Task.Run(() =>
        concurrentAtlas.PrepareSequence(PetMood.DraggedHeadAngry));
    var grabCompletedWhileClickWasPaused =
        grabLoad.Wait(TimeSpan.FromSeconds(2));
    releaseClickDecode.Set();
    Task.WaitAll([clickLoad, grabLoad], TimeSpan.FromSeconds(5));

    Expect(grabCompletedWhileClickWasPaused &&
           grabLoad.Result.Frames.Count == 1 &&
           ReadFirstPixel(grabLoad.Result.Frames[0]) == Colors.Purple,
        "preparing the click sequence blocked an unrelated grab sequence");

    var warmUpRoot = Path.Combine(harnessRoot, "warm-up-animations");
    var warmDraggedDirectory = Path.Combine(
        warmUpRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.Dragged]);
    var warmIdleDirectory = Path.Combine(
        warmUpRoot,
        PetAnimations.ExternalFrameDirectories[PetMood.Idle]);
    Directory.CreateDirectory(warmDraggedDirectory);
    Directory.CreateDirectory(warmIdleDirectory);
    WritePng(Path.Combine(warmDraggedDirectory, "00.png"), Colors.Coral);
    WritePng(Path.Combine(warmIdleDirectory, "00.png"), Colors.Aqua);

    using var warmDecodeEntered = new ManualResetEventSlim();
    using var releaseWarmDecode = new ManualResetEventSlim();
    var decodedWarmUpMoods = new List<PetMood>();
    var warmUpFailures = new List<(PetMood Mood, Exception Exception)>();
    var warmUpAtlas = new SpriteAtlas(
        warmUpRoot,
        (mood, decodedFrameCount) =>
        {
            if (decodedFrameCount != 1)
            {
                return;
            }

            decodedWarmUpMoods.Add(mood);
            if (mood == PetMood.Dragged)
            {
                warmDecodeEntered.Set();
                releaseWarmDecode.Wait(TimeSpan.FromSeconds(5));
            }
        });

    var warmUpTask = AnimationSequencePreloader.WarmUpAsync(
        warmUpAtlas,
        [
            PetMood.Dragged,
            PetMood.Dragged,
            PetMood.DraggedWingPout,
            PetMood.Idle
        ],
        (mood, exception) => warmUpFailures.Add((mood, exception)));
    Expect(warmDecodeEntered.Wait(TimeSpan.FromSeconds(5)),
        "background warm-up never began decoding its first priority mood");
    Expect(!warmUpTask.IsCompleted,
        "background warm-up blocked the caller instead of returning an in-flight task");
    releaseWarmDecode.Set();
    await warmUpTask.WaitAsync(TimeSpan.FromSeconds(5));

    Expect(decodedWarmUpMoods.SequenceEqual([PetMood.Dragged, PetMood.Idle]),
        "background warm-up did not preserve priority or de-duplicate moods");
    Expect(warmUpFailures.Count == 1 &&
           warmUpFailures[0].Mood == PetMood.DraggedWingPout &&
           warmUpFailures[0].Exception is InvalidDataException,
        "one missing warm-up sequence did not report failure and continue");
    var decodedWarmUpCount = decodedWarmUpMoods.Count;
    Expect(warmUpAtlas.PrepareSequence(PetMood.Dragged).Frames.Count == 1 &&
           warmUpAtlas.PrepareSequence(PetMood.Idle).Frames.Count == 1 &&
           decodedWarmUpMoods.Count == decodedWarmUpCount,
        "successfully warmed sequences were not atomically cached for later UI use");

    Console.WriteLine("SpriteAtlas prepared snapshot tests passed.");
}
finally
{
    if (Directory.Exists(harnessRoot))
    {
        Directory.Delete(harnessRoot, recursive: true);
    }
}
