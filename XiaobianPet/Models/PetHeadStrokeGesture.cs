namespace XiaobianPet.Models;

/// <summary>Hover-only strokes; positions are normalized to the displayed viewport.</summary>
public sealed class PetHeadStrokeGesture
{
    private long _lastSample = -1;
    private long _startedAt;
    private long _cooldownUntil;
    private double _extremeX;
    private double _startY;
    private int _direction;
    private int _strokes;

    public void Reset()
    {
        _lastSample = -1;
        _direction = _strokes = 0;
    }

    public bool Observe(long now, double x, double y, bool overHead, bool buttonPressed)
    {
        if (!overHead || buttonPressed || now < _cooldownUntil ||
            !double.IsFinite(x) || !double.IsFinite(y))
        {
            Reset();
            return false;
        }

        if (_lastSample < 0 || now - _lastSample > 400 || now - _startedAt > 2400 ||
            Math.Abs(y - _startY) > 0.08)
        {
            Reset();
            _startedAt = now;
            _extremeX = x;
            _startY = y;
        }
        _lastSample = now;

        var delta = x - _extremeX;
        if (_direction != 0 && Math.Sign(delta) == _direction)
            _extremeX = x;
        else if (Math.Abs(delta) >= 0.065)
        {
            _direction = Math.Sign(delta);
            _extremeX = x;
            _strokes++;
        }

        if (_strokes < 3 || now - _startedAt < 350) return false;
        _cooldownUntil = now + 9000;
        Reset();
        return true;
    }
}
