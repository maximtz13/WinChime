namespace WinChime.Core.Sounds;

/// <summary>
/// How to convert a source file into an event sound.
///
/// Defaults produce a straight format conversion with no alteration of the audio itself,
/// because changing someone's sound without being asked is worse than leaving it imperfect.
/// Trimming and normalising are opt-in.
/// </summary>
public sealed record TranscodeOptions
{
    /// <summary>44.1 kHz 16-bit stereo is ample for a system event and universally safe.</summary>
    public int SampleRate { get; init; } = 44100;
    public int BitsPerSample { get; init; } = 16;
    public int Channels { get; init; } = 2;

    /// <summary>Cut the audio at this length. Null keeps the whole file.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>
    /// Scale the audio so its loudest peak sits at <see cref="TargetPeak"/>. Peak
    /// normalisation, not loudness normalisation: it fixes "this clip is far quieter than
    /// every other system sound" without the complexity of an LUFS measurement, which is
    /// overkill for something that plays for two seconds.
    /// </summary>
    public bool Normalise { get; init; }

    /// <summary>Roughly -1 dBFS. Leaves headroom so downstream mixing cannot clip.</summary>
    public double TargetPeak { get; init; } = 0.89;

    /// <summary>
    /// Ceiling on normalisation gain, in decibels. Without it, a near-silent or empty clip
    /// would have its noise floor amplified enormously.
    ///
    /// 30 dB, not 20: a clip peaking at 0.05 is -26 dBFS, which is quiet but perfectly real
    /// audio, and reaching the target from there needs 25 dB. A 20 dB ceiling capped exactly
    /// the files people most want normalised and left them half-fixed at 0.5 — quieter than
    /// asked for, with nothing said about why. True silence is already excluded separately
    /// by a peak threshold, so this ceiling only needs to stop noise floors from being
    /// hauled up into audibility.
    /// </summary>
    public double MaxGainDb { get; init; } = 30.0;

    /// <summary>
    /// Fade applied at the cut when trimming actually removes audio. Slicing mid-waveform
    /// leaves a discontinuity that is audible as a click, and a click is exactly what nobody
    /// wants from a notification sound. Not applied when the file ends naturally.
    /// </summary>
    public TimeSpan FadeOut { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>True when the audio itself is altered, not merely re-encoded.</summary>
    public bool ModifiesAudio => MaxDuration is not null || Normalise;

    public static TranscodeOptions Default { get; } = new();

    /// <summary>Windows event sounds longer than this overlap each other and outstay their welcome.</summary>
    public static TimeSpan SuggestedMaxEventDuration { get; } = TimeSpan.FromSeconds(10);
}
