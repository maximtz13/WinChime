namespace WinChime.SoundPackGenerator;

/// <summary>
/// A small additive synthesiser, which is genuinely how a lot of UI sounds are made: a few
/// sine partials over an exponential decay reads as a struck bell or chime.
///
/// Two details matter more than they look. Partials must decay at different rates — higher
/// ones faster — or the result sounds like an organ rather than something struck. And every
/// sound needs a short attack ramp, because starting a sine at full amplitude puts a step
/// discontinuity at sample zero, which is an audible click.
/// </summary>
public sealed class Synth
{
    public const int SampleRate = 44100;

    /// <summary>
    /// Leaves headroom so nothing clips once partials sum, and keeps the whole set at a
    /// consistent level rather than some sounds being noticeably louder than others.
    /// </summary>
    private const double TargetPeak = 0.70;

    /// <summary>Ramp length at the start of every sound. Long enough to kill the click, short enough to stay percussive.</summary>
    private static readonly TimeSpan Attack = TimeSpan.FromMilliseconds(4);

    private readonly double[] _buffer;

    public Synth(double seconds)
    {
        _buffer = new double[(int)(seconds * SampleRate)];
    }

    /// <summary>
    /// Bell-like: near-harmonic partials, slightly stretched so the upper ones are not exact
    /// multiples. Exact multiples sound synthetic; real struck metal is mildly inharmonic.
    /// </summary>
    public static readonly double[] BellRatios = [1.0, 2.0, 2.99, 4.24];
    public static readonly double[] BellAmps = [1.0, 0.45, 0.22, 0.10];

    /// <summary>Darker and rounder, for the lower warning sounds where shimmer would feel wrong.</summary>
    public static readonly double[] SoftRatios = [1.0, 2.0, 3.01];
    public static readonly double[] SoftAmps = [1.0, 0.28, 0.09];

    /// <summary>
    /// Adds a struck note at <paramref name="startSeconds"/>.
    /// <paramref name="decay"/> is in nepers per second: higher is shorter.
    /// </summary>
    public Synth Note(
        double frequency,
        double startSeconds,
        double decay,
        double amplitude = 1.0,
        double[]? ratios = null,
        double[]? amps = null)
    {
        ratios ??= BellRatios;
        amps ??= BellAmps;

        var start = (int)(startSeconds * SampleRate);
        var attackSamples = Math.Max(1, (int)(Attack.TotalSeconds * SampleRate));

        for (var i = start; i < _buffer.Length; i++)
        {
            var t = (i - start) / (double)SampleRate;
            var value = 0.0;

            for (var k = 0; k < ratios.Length; k++)
            {
                // Higher partials fade faster, which is what makes it read as struck.
                var partialDecay = decay * (1.0 + 0.6 * k);
                value += amps[k] * Math.Sin(2 * Math.PI * frequency * ratios[k] * t) * Math.Exp(-t * partialDecay);
            }

            var envelope = i - start < attackSamples ? (i - start) / (double)attackSamples : 1.0;
            _buffer[i] += value * envelope * amplitude;
        }

        return this;
    }

    /// <summary>
    /// A filtered noise burst, for sounds that should read as movement rather than pitch.
    /// The cutoff sweeps downward over the burst, which is what gives it a sense of travel
    /// instead of sounding like a click of static.
    /// </summary>
    public Synth Swish(double startSeconds, double durationSeconds, double amplitude = 1.0, int seed = 1)
    {
        var random = new Random(seed);
        var start = (int)(startSeconds * SampleRate);
        var length = (int)(durationSeconds * SampleRate);
        var attackSamples = Math.Max(1, (int)(Attack.TotalSeconds * SampleRate));

        var lowpass = 0.0;

        for (var i = 0; i < length && start + i < _buffer.Length; i++)
        {
            var t = i / (double)length;

            // One-pole lowpass with a coefficient that falls as the sound progresses.
            var cutoff = 0.55 * (1.0 - t) + 0.02;
            lowpass += cutoff * ((random.NextDouble() * 2 - 1) - lowpass);

            var envelope = Math.Exp(-t * 4.5);
            if (i < attackSamples) envelope *= i / (double)attackSamples;

            _buffer[start + i] += lowpass * envelope * amplitude * 2.2;
        }

        return this;
    }

    /// <summary>
    /// Normalises to a shared target and applies a short fade at the end.
    ///
    /// The fade is not optional: these sounds decay exponentially and so never reach exactly
    /// zero, and cutting a non-zero sample at the end of a file is the same click as
    /// starting at full amplitude.
    /// </summary>
    public short[] Render()
    {
        var peak = 0.0;
        foreach (var sample in _buffer) peak = Math.Max(peak, Math.Abs(sample));

        var gain = peak > 1e-9 ? TargetPeak / peak : 0.0;

        var fadeSamples = Math.Min(_buffer.Length, (int)(0.012 * SampleRate));
        var result = new short[_buffer.Length];

        for (var i = 0; i < _buffer.Length; i++)
        {
            var value = _buffer[i] * gain;

            var fromEnd = _buffer.Length - 1 - i;
            if (fromEnd < fadeSamples) value *= fromEnd / (double)fadeSamples;

            result[i] = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
        }

        return result;
    }
}
