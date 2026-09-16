using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed record PreparedAnimationSequence
{
    internal PreparedAnimationSequence(
        PetMood requestedMood,
        PetMood visualMood,
        IReadOnlyList<BitmapSource> frames,
        TimeSpan frameDuration)
    {
        RequestedMood = requestedMood;
        VisualMood = visualMood;
        Frames = frames;
        FrameDuration = frameDuration;
    }

    public PetMood RequestedMood { get; }

    public PetMood VisualMood { get; }

    public IReadOnlyList<BitmapSource> Frames { get; }

    public TimeSpan FrameDuration { get; }
}

internal static class ExternalAnimationDurationPolicy
{
    internal static TimeSpan Validate(
        PetMood mood,
        int actualFrameCount,
        TimeSpan frameDuration)
    {
        if (actualFrameCount <= 0)
        {
            throw new InvalidDataException(
                $"视频动画 {mood} 的实际帧数必须大于 0，当前为 {actualFrameCount}。 ");
        }

        if (frameDuration <= TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"视频动画 {mood} 的帧间隔必须大于 0，当前为 {frameDuration.TotalMilliseconds:F3} 毫秒。 ");
        }

        long totalTicks;
        try
        {
            totalTicks = checked(frameDuration.Ticks * (long)actualFrameCount);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                $"视频动画 {mood} 的实际总时长无法表示：" +
                $"{actualFrameCount} 帧 × {frameDuration.TotalMilliseconds:F3} 毫秒/帧。",
                ex);
        }

        var actualDuration = TimeSpan.FromTicks(totalTicks);
        if (actualDuration > PetAnimations.MaximumSingleAnimationDuration)
        {
            throw new InvalidDataException(
                $"视频动画 {mood} 的实际总时长为 {actualDuration.TotalSeconds:F3} 秒" +
                $"（{actualFrameCount} 帧 × {frameDuration.TotalMilliseconds:F3} 毫秒/帧），" +
                $"超过单个动画最长 {PetAnimations.MaximumSingleAnimationDuration.TotalSeconds:F3} 秒的限制。 ");
        }

        return actualDuration;
    }
}

public sealed class SpriteAtlas
{
    private readonly string _animationsRoot;
    private readonly Action<PetMood, int>? _frameDecodedObserver;
    private readonly ConcurrentDictionary<PetMood, PreparedAnimationSequence> _preparedSequences = [];
    private readonly ConcurrentDictionary<PetMood, ExternalAnimationSequence> _externalSequences = [];
    private readonly ConcurrentDictionary<PetMood, object> _externalSequenceLoadGates = [];
    private readonly ConcurrentDictionary<PetMood, string> _externalSequenceErrors = [];
    private static readonly Regex NaturalDigits = new(@"\d+", RegexOptions.Compiled);

    public SpriteAtlas(string animationsRoot)
        : this(animationsRoot, frameDecodedObserver: null)
    {
    }

    internal SpriteAtlas(
        string animationsRoot,
        Action<PetMood, int>? frameDecodedObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationsRoot);
        _animationsRoot = Path.GetFullPath(animationsRoot);
        _frameDecodedObserver = frameDecodedObserver;
    }

    public PreparedAnimationSequence PrepareSequence(PetMood mood)
    {
        if (_preparedSequences.TryGetValue(mood, out var cached))
        {
            return cached;
        }

        var visualMood = PetAnimations.ResolveVisualMood(mood);
        var sequence = GetRequiredExternalSequence(visualMood);
        var prepared = new PreparedAnimationSequence(
            mood,
            visualMood,
            sequence.Frames,
            PetAnimations.GetExternalFrameDuration(visualMood));
        return _preparedSequences.GetOrAdd(mood, prepared);
    }

    public BitmapSource GetFrame(PetMood mood, int frameIndex)
    {
        var sequence = PrepareSequence(mood);
        var externalIndex = Math.Clamp(frameIndex, 0, sequence.Frames.Count - 1);
        return sequence.Frames[externalIndex];
    }

    public int GetFrameCount(PetMood mood) => PrepareSequence(mood).Frames.Count;

    public TimeSpan GetFrameDuration(PetMood mood)
    {
        var visualMood = PetAnimations.ResolveVisualMood(mood);
        return PetAnimations.GetExternalFrameDuration(visualMood);
    }

    public bool UsesExternalFrames(PetMood mood) =>
        PetAnimations.ResolveVisualMood(mood) == mood &&
        TryGetExternalSequence(mood, out _);

    private ExternalAnimationSequence GetRequiredExternalSequence(PetMood mood)
    {
        if (TryGetExternalSequence(mood, out var sequence))
        {
            return sequence;
        }

        var detail = _externalSequenceErrors.TryGetValue(mood, out var error)
            ? error
            : "未配置视频动画目录。";
        throw new InvalidDataException($"无法加载柯朵的视频动画 {mood}：{detail}");
    }

    private bool TryGetExternalSequence(
        PetMood mood,
        out ExternalAnimationSequence sequence)
    {
        sequence = null!;
        if (!PetAnimations.ExternalFrameDirectories.TryGetValue(
                mood,
                out var directoryName))
        {
            return false;
        }

        if (_externalSequences.TryGetValue(mood, out var cached))
        {
            sequence = cached;
            return true;
        }

        // Serialize only callers loading the same mood. Decoding a long click
        // sequence must never block the UI from preparing an unrelated grab mood.
        var loadGate = _externalSequenceLoadGates.GetOrAdd(mood, static _ => new object());
        lock (loadGate)
        {
            if (_externalSequences.TryGetValue(mood, out cached))
            {
                sequence = cached;
                return true;
            }

            var loaded = LoadExternalSequence(mood, directoryName);
            if (loaded is null)
            {
                return false;
            }

            // Publish only after every file has been enumerated, decoded,
            // validated, and frozen. Failed loads never expose a prefix.
            sequence = _externalSequences.GetOrAdd(mood, loaded);
            return true;
        }
    }

    private ExternalAnimationSequence? LoadExternalSequence(
        PetMood mood,
        string directoryName)
    {
        var directory = Path.Combine(_animationsRoot, directoryName);
        if (!Directory.Exists(directory))
        {
            _externalSequenceErrors[mood] = $"目录不存在：{directory}";
            return null;
        }

        try
        {
            var paths = Directory
                .EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(
                    path => NaturalSortKey(Path.GetFileName(path)),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                _externalSequenceErrors[mood] = $"目录中没有 PNG 帧：{directory}";
                return null;
            }

            var frames = new List<BitmapSource>(paths.Length);
            foreach (var path in paths)
            {
                using var stream = File.OpenRead(path);
                var decoder = new PngBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth != PetAnimations.CellWidth ||
                    frame.PixelHeight != PetAnimations.CellHeight)
                {
                    throw new InvalidDataException(
                        $"视频动画帧尺寸不正确：{path} 实际 " +
                        $"{frame.PixelWidth}×{frame.PixelHeight}，需要 " +
                        $"{PetAnimations.CellWidth}×{PetAnimations.CellHeight}。");
                }

                frame.Freeze();
                frames.Add(frame);
                _frameDecodedObserver?.Invoke(mood, frames.Count);
            }

            // The directory is the runtime source of truth: validate the fully
            // decoded frame count, not a manifest or nominal definition count.
            ExternalAnimationDurationPolicy.Validate(
                mood,
                frames.Count,
                PetAnimations.GetExternalFrameDuration(mood));

            var snapshot = Array.AsReadOnly(frames.ToArray());
            _externalSequenceErrors.TryRemove(mood, out _);
            return new ExternalAnimationSequence(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   NotSupportedException or InvalidDataException or
                                   FormatException or ArgumentException or
                                   InvalidOperationException)
        {
            _externalSequenceErrors[mood] = ex.Message;
            Debug.WriteLine($"Video animation sequence for {mood} was rejected. {ex}");
            return null;
        }
    }

    private static string NaturalSortKey(string fileName) =>
        NaturalDigits.Replace(
            fileName,
            match => match.Value.Length <= 20
                ? match.Value.PadLeft(20, '0')
                : match.Value);

    private sealed record ExternalAnimationSequence(IReadOnlyList<BitmapSource> Frames);
}
