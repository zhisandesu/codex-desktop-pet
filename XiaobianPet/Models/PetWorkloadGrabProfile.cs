namespace XiaobianPet.Models;

/// <summary>
/// Reviewed semantic hit regions in the final video canvas. These landmarks
/// never alter image geometry or animation timing. The caller checks alpha first.
/// </summary>
public sealed class PetWorkloadGrabProfile
{
    public List<PetWorkloadGrabKeyframe> Keyframes { get; set; } = [];

    public void Validate(int frameCount)
    {
        if (frameCount <= 0 || Keyframes.Count == 0 || Keyframes[0].Frame != 0 ||
            Keyframes[^1].Frame != frameCount - 1)
            throw new ArgumentException("Grab landmarks must cover the entire source sequence.");
        var previous = -1;
        var exclusions = Keyframes[0].ExcludeRects.Count;
        foreach (var key in Keyframes)
        {
            if (key.Frame <= previous || key.Frame >= frameCount ||
                key.ExcludeRects.Count != exclusions)
                throw new ArgumentException("Grab landmarks require ordered frames and stable region slots.");
            ValidateEllipse(key.Head);
            ValidateEllipse(key.LeftWing);
            ValidateEllipse(key.RightWing);
            foreach (var rect in key.ExcludeRects)
            {
                ValidateFour(rect);
                if (rect[2] < rect[0] || rect[3] < rect[1])
                    throw new ArgumentException("Furniture exclusion has inverted bounds.");
            }
            previous = key.Frame;
        }
    }

    public bool TryClassify(int frame, double x, double y, out PetGrabRegion region)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (Keyframes.Count == 0) throw new InvalidOperationException("Unvalidated empty grab profile.");
        frame = Math.Clamp(frame, Keyframes[0].Frame, Keyframes[^1].Frame);
        var upper = Keyframes.FindIndex(key => key.Frame >= frame);
        var b = Keyframes[upper];
        var a = Keyframes[Math.Max(0, upper - 1)];
        var t = b.Frame == a.Frame ? 0 : (frame - a.Frame) / (double)(b.Frame - a.Frame);
        region = PetGrabRegion.Body;
        for (var i = 0; i < a.ExcludeRects.Count; i++)
        {
            var ra = a.ExcludeRects[i];
            var rb = b.ExcludeRects[i];
            var left = Lerp(ra, rb, 0, t);
            var top = Lerp(ra, rb, 1, t);
            var right = Lerp(ra, rb, 2, t);
            var bottom = Lerp(ra, rb, 3, t);
            if (right > left && bottom > top && x >= left && x < right && y >= top && y < bottom)
                return false;
        }
        if (Contains(a.Head, b.Head, t, x, y)) region = PetGrabRegion.Head;
        else if (Contains(a.LeftWing, b.LeftWing, t, x, y) ||
                 Contains(a.RightWing, b.RightWing, t, x, y)) region = PetGrabRegion.Wing;
        return true;
    }

    private static bool Contains(double[] a, double[] b, double t, double x, double y)
    {
        var rx = Lerp(a, b, 2, t);
        var ry = Lerp(a, b, 3, t);
        if (rx <= 0 || ry <= 0) return false;
        var dx = (x - Lerp(a, b, 0, t)) / rx;
        var dy = (y - Lerp(a, b, 1, t)) / ry;
        return dx * dx + dy * dy <= 1;
    }

    private static double Lerp(double[] a, double[] b, int i, double t) => a[i] + (b[i] - a[i]) * t;
    private static void ValidateFour(double[] values)
    {
        if (values.Length != 4 || values.Any(value => !double.IsFinite(value) || Math.Abs(value) > 832))
            throw new ArgumentException("Grab regions need four finite canvas coordinates.");
    }

    private static void ValidateEllipse(double[] values)
    {
        ValidateFour(values);
        if (values[2] < 0 || values[3] < 0)
            throw new ArgumentException("Grab ellipse radii cannot be negative.");
    }
}

public sealed class PetWorkloadGrabKeyframe
{
    public int Frame { get; set; }
    public double[] Head { get; set; } = [0, 0, 0, 0];
    public double[] LeftWing { get; set; } = [0, 0, 0, 0];
    public double[] RightWing { get; set; } = [0, 0, 0, 0];
    public List<double[]> ExcludeRects { get; set; } = [];
}
