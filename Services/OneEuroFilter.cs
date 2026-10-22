namespace TouchNumPad.Services;

/// <summary>
/// 1€ Filter (One Euro Filter) - Casiez, Roussel, Vogel (ACM CHI 2012)
/// A speed-based adaptive low-pass filter for noisy input in interactive systems.
/// Suppresses jitter at low speeds while eliminating lag at high speeds.
/// </summary>
public sealed class OneEuroFilter
{
    private readonly double _minCutoff; // Minimum cutoff frequency in Hz
    private readonly double _beta;      // Speed coefficient
    private readonly double _dCutoff;   // Derivative cutoff frequency

    private double _xPrev;
    private double _dxPrev;
    private long _tPrev;
    private bool _initialized;

    public OneEuroFilter(double minCutoff = 1.2, double beta = 0.05, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    public void Reset()
    {
        _initialized = false;
        _xPrev = 0.0;
        _dxPrev = 0.0;
        _tPrev = 0;
    }

    public double Filter(double x, long timestampMs)
    {
        if (!_initialized)
        {
            _initialized = true;
            _xPrev = x;
            _dxPrev = 0.0;
            _tPrev = timestampMs;
            return x;
        }

        double dt = (timestampMs - _tPrev) / 1000.0;
        _tPrev = timestampMs;

        // If time interval is invalid or too long, reset to current
        if (dt <= 0.0 || dt > 0.5)
        {
            _xPrev = x;
            _dxPrev = 0.0;
            return x;
        }

        // Estimate derivative (speed)
        double dx = (x - _xPrev) / dt;
        double aD = SmoothingFactor(dt, _dCutoff);
        double dxHat = ExponentialSmoothing(dx, _dxPrev, aD);
        _dxPrev = dxHat;

        // Dynamic cutoff frequency proportional to speed
        double cutoff = _minCutoff + _beta * Math.Abs(dxHat);
        double a = SmoothingFactor(dt, cutoff);
        double xHat = ExponentialSmoothing(x, _xPrev, a);
        _xPrev = xHat;

        return xHat;
    }

    private static double SmoothingFactor(double dt, double cutoff)
    {
        double r = 2.0 * Math.PI * cutoff * dt;
        return r / (r + 1.0);
    }

    private static double ExponentialSmoothing(double a, double b, double alpha)
    {
        return alpha * a + (1.0 - alpha) * b;
    }
}
