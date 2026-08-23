using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

public sealed class AudioTranscoderTests : IDisposable
{
    private readonly TestWav _wav = new();

    public void Dispose() => _wav.Dispose();

    // -------------------------------------------------- environment-independent --

    [Fact]
    public void NeedsConversion_PcmWav_IsFalse()
    {
        Assert.False(AudioTranscoder.NeedsConversion(_wav.WritePcm()));
    }

    [Fact]
    public void NeedsConversion_NonPcmWav_IsTrue()
    {
        Assert.True(AudioTranscoder.NeedsConversion(_wav.WriteNonPcm()));
    }

    [Fact]
    public void NeedsConversion_NotAudioAtAll_IsTrue()
    {
        Assert.True(AudioTranscoder.NeedsConversion(_wav.WriteNotRiff()));
    }

    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("song.MP3", true)]
    [InlineData("clip.m4a", true)]
    [InlineData("clip.flac", true)]
    [InlineData("sound.wav", true)]
    [InlineData("document.txt", false)]
    [InlineData("archive.zip", false)]
    [InlineData("noextension", false)]
    public void LooksLikeSupportedSource_MatchesOnExtension(string fileName, bool expected)
    {
        Assert.Equal(expected, AudioTranscoder.LooksLikeSupportedSource(fileName));
    }

    [Fact]
    public void OpenFileFilter_OffersTheFormatsPeopleActuallyHave()
    {
        var filter = AudioTranscoder.OpenFileFilter;

        Assert.Contains("*.mp3", filter);
        Assert.Contains("*.wav", filter);
        Assert.Contains("*.m4a", filter);
        Assert.Contains("*.flac", filter);
    }

    /// <summary>
    /// Converted files are pointed at by the registry, so they have to outlive a reboot.
    /// A temp folder would leave every converted sound broken.
    /// </summary>
    [Fact]
    public void ConvertedFolder_IsPersistentNotTemporary()
    {
        var folder = AudioTranscoder.ConvertedFolder;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, folder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath(), folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_MissingSource_FailsWithoutThrowing()
    {
        var result = AudioTranscoder.Convert(_wav.PathFor("absent.mp3"), _wav.PathFor("out.wav"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_SourceSameAsDestination_IsRefused()
    {
        var path = _wav.WritePcm();

        var result = AudioTranscoder.Convert(path, path);

        Assert.False(result.Success);
        Assert.Contains("same file", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------ requires Media Foundation --

    [RequiresMediaFoundation]
    public void Convert_PcmWav_ResamplesToTheRequestedFormat()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("tone.wav"), seconds: 1, sampleRate: 22050, channels: 1);
        var destination = _wav.PathFor("resampled.wav");

        var result = AudioTranscoder.Convert(source, destination,
            new TranscodeOptions { SampleRate = 44100, BitsPerSample = 16, Channels = 2 });

        Assert.True(result.Success, result.Message);

        var info = WaveFile.Inspect(destination);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal(2, info.Channels);
        Assert.True(info.IsPlayableByWindows);
    }

    [RequiresMediaFoundation]
    public void Convert_GarbageInput_FailsAndLeavesNoPartialFile()
    {
        var source = _wav.PathFor("liar.mp3");
        File.WriteAllText(source, "text pretending very hard to be an mp3");
        var destination = _wav.PathFor("should-not-exist.wav");

        var result = AudioTranscoder.Convert(source, destination);

        Assert.False(result.Success);

        // A half-written file would pass the RIFF check and then play a fragment or nothing,
        // which is exactly the silent failure this whole feature exists to prevent.
        Assert.False(File.Exists(destination));
    }

    [RequiresMediaFoundation]
    public void ConvertIntoLibrary_NamesOutputAfterTheSource()
    {
        var folder = _wav.PathFor("library");
        var source = TestAudio.WriteTone(_wav.PathFor("Chime Sound.wav"), seconds: 0.5);

        var result = AudioTranscoder.ConvertIntoLibrary(source, folder);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Chime Sound.wav", Path.GetFileName(result.OutputPath));
    }

    /// <summary>
    /// Two different sources with the same file name must not clobber each other: the first
    /// one is probably already assigned to an event and pointed at by the registry.
    /// </summary>
    [RequiresMediaFoundation]
    public void ConvertIntoLibrary_DoesNotOverwriteAnExistingDifferentFile()
    {
        var folder = _wav.PathFor("library");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "tone.wav"), "an unrelated file already here");

        var source = TestAudio.WriteTone(_wav.PathFor("tone.wav"), seconds: 0.5);

        var result = AudioTranscoder.ConvertIntoLibrary(source, folder);

        Assert.True(result.Success, result.Message);
        Assert.Equal("tone (2).wav", Path.GetFileName(result.OutputPath));
        Assert.Equal("an unrelated file already here", File.ReadAllText(Path.Combine(folder, "tone.wav")));
    }

    // ------------------------------------------------------ requires MP3 encoder --

    /// <summary>
    /// The headline case: an MP3 is exactly what a user reaches for, and it is exactly what
    /// Windows accepts and then plays silently.
    /// </summary>
    [RequiresMp3Encoder]
    public void Convert_Mp3_ProducesAFileWindowsCanActuallyPlay()
    {
        var wav = TestAudio.WriteTone(_wav.PathFor("tone.wav"), seconds: 1);
        var mp3 = TestAudio.WriteMp3(wav, _wav.PathFor("tone.mp3"));

        Assert.True(AudioTranscoder.NeedsConversion(mp3));

        var destination = _wav.PathFor("from-mp3.wav");
        var result = AudioTranscoder.Convert(mp3, destination);

        Assert.True(result.Success, result.Message);
        Assert.True(WaveFile.Inspect(destination).IsPlayableByWindows);
        Assert.False(AudioTranscoder.NeedsConversion(destination));
    }

    [RequiresMp3Encoder]
    public void Convert_Mp3_PreservesRoughlyTheOriginalDuration()
    {
        var wav = TestAudio.WriteTone(_wav.PathFor("tone.wav"), seconds: 2);
        var mp3 = TestAudio.WriteMp3(wav, _wav.PathFor("tone.mp3"));
        var destination = _wav.PathFor("from-mp3.wav");

        Assert.True(AudioTranscoder.Convert(mp3, destination).Success);

        // Deliberately loose. MP3 is a lossy, block-based format: encoder padding and
        // decoder delay shift the length by tens of milliseconds in either direction, so
        // asserting an exact duration here would be asserting a fiction.
        var seconds = WaveFile.Inspect(destination).Duration.TotalSeconds;
        Assert.InRange(seconds, 1.8, 2.2);
    }

    [RequiresMp3Encoder]
    public void Convert_Mp3_ProducesAudibleAudioNotSilence()
    {
        var wav = TestAudio.WriteTone(_wav.PathFor("tone.wav"), seconds: 1);
        var mp3 = TestAudio.WriteMp3(wav, _wav.PathFor("tone.mp3"));
        var destination = _wav.PathFor("from-mp3.wav");

        Assert.True(AudioTranscoder.Convert(mp3, destination).Success);

        // A conversion that silently produced a valid, empty WAV would pass every other
        // assertion in this file. Check that actual signal survived.
        Assert.True(PeakAmplitude(destination) > 0.05,
            "Converted audio is effectively silent; the signal did not survive the round trip.");
    }

    // -------------------------------------------------------------- trimming --

    [RequiresMediaFoundation]
    public void Convert_WithMaxDuration_TrimsToThatLength()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("long.wav"), seconds: 3);
        var destination = _wav.PathFor("trimmed.wav");

        var result = AudioTranscoder.Convert(source, destination,
            new TranscodeOptions { MaxDuration = TimeSpan.FromSeconds(1) });

        Assert.True(result.Success, result.Message);
        Assert.InRange(WaveFile.Inspect(destination).Duration.TotalSeconds, 0.95, 1.05);
    }

    [RequiresMediaFoundation]
    public void Convert_WithoutMaxDuration_KeepsTheWholeFile()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("long.wav"), seconds: 3);
        var destination = _wav.PathFor("untrimmed.wav");

        Assert.True(AudioTranscoder.Convert(source, destination).Success);
        Assert.InRange(WaveFile.Inspect(destination).Duration.TotalSeconds, 2.9, 3.1);
    }

    [RequiresMediaFoundation]
    public void Convert_MaxDurationLongerThanTheSource_ChangesNothing()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("short.wav"), seconds: 1);
        var destination = _wav.PathFor("padded.wav");

        var result = AudioTranscoder.Convert(source, destination,
            new TranscodeOptions { MaxDuration = TimeSpan.FromSeconds(30) });

        Assert.True(result.Success);
        Assert.InRange(WaveFile.Inspect(destination).Duration.TotalSeconds, 0.9, 1.1);

        // Nothing was cut, so no fade should have been applied and no trim reported.
        Assert.DoesNotContain("Trimmed", result.Message);
    }

    /// <summary>
    /// Slicing mid-waveform leaves a discontinuity that is audible as a click, which is
    /// exactly what nobody wants from a notification sound.
    /// </summary>
    [RequiresMediaFoundation]
    public void Convert_WhenTrimmingCutsAudio_FadesOutAtTheCut()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("long.wav"), seconds: 3);
        var destination = _wav.PathFor("faded.wav");

        Assert.True(AudioTranscoder.Convert(source, destination,
            new TranscodeOptions { MaxDuration = TimeSpan.FromSeconds(1) }).Success);

        var samples = ReadSamples(destination);

        // Compare the final few milliseconds against a window from the middle. A constant
        // tone would show the same peak in both; a fade makes the tail far quieter.
        var tailPeak = PeakOfRange(samples, samples.Length - 200, 200);
        var middlePeak = PeakOfRange(samples, samples.Length / 2, 200);

        Assert.True(tailPeak < middlePeak * 0.25,
            $"Expected a faded tail, but tail peak {tailPeak:0.000} is not much below middle peak {middlePeak:0.000}.");
    }

    // ---------------------------------------------------------- normalisation --

    [RequiresMediaFoundation]
    public void Convert_Normalise_RaisesQuietAudioToTheTargetPeak()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("quiet.wav"), seconds: 1, amplitude: 0.05);
        var destination = _wav.PathFor("normalised.wav");

        var options = new TranscodeOptions { Normalise = true };
        Assert.True(AudioTranscoder.Convert(source, destination, options).Success);

        Assert.InRange(PeakAmplitude(destination), options.TargetPeak - 0.05, options.TargetPeak + 0.02);
    }

    [RequiresMediaFoundation]
    public void Convert_Normalise_LowersLoudAudioToTheTargetPeak()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("loud.wav"), seconds: 1, amplitude: 0.99);
        var destination = _wav.PathFor("normalised.wav");

        var options = new TranscodeOptions { Normalise = true };
        Assert.True(AudioTranscoder.Convert(source, destination, options).Success);

        Assert.InRange(PeakAmplitude(destination), options.TargetPeak - 0.05, options.TargetPeak + 0.02);
    }

    [RequiresMediaFoundation]
    public void Convert_WithoutNormalise_LeavesTheLevelAlone()
    {
        var source = TestAudio.WriteTone(_wav.PathFor("quiet.wav"), seconds: 1, amplitude: 0.05);
        var destination = _wav.PathFor("asis.wav");

        Assert.True(AudioTranscoder.Convert(source, destination).Success);

        Assert.InRange(PeakAmplitude(destination), 0.02, 0.08);
    }

    /// <summary>
    /// "Normalising" silence would mean multiplying the noise floor by an enormous factor
    /// and turning an inaudible file into an unpleasant one.
    /// </summary>
    [RequiresMediaFoundation]
    public void Convert_Normalise_DoesNotAmplifySilence()
    {
        var source = TestAudio.WriteSilence(_wav.PathFor("silent.wav"), seconds: 1);
        var destination = _wav.PathFor("still-silent.wav");

        var result = AudioTranscoder.Convert(source, destination, new TranscodeOptions { Normalise = true });

        Assert.True(result.Success, result.Message);
        Assert.True(PeakAmplitude(destination) < 0.01, "Silence should stay silent.");
    }

    [RequiresMediaFoundation]
    public void Convert_Normalise_HonoursTheGainCeiling()
    {
        // Roughly -60 dBFS. Reaching the 0.89 target would need about 59 dB of gain, so a
        // 6 dB ceiling must leave it far below target.
        var source = TestAudio.WriteTone(_wav.PathFor("verysoft.wav"), seconds: 1, amplitude: 0.001);
        var destination = _wav.PathFor("capped.wav");

        var result = AudioTranscoder.Convert(source, destination,
            new TranscodeOptions { Normalise = true, MaxGainDb = 6.0 });

        Assert.True(result.Success, result.Message);

        // 0.001 lifted by at most 6 dB is about 0.002, nowhere near the 0.89 target.
        Assert.True(PeakAmplitude(destination) < 0.01,
            "Gain ceiling was not respected; a near-silent clip was amplified too far.");
    }

    [Fact]
    public void Convert_TrimOrNormaliseAt32Bit_IsRefusedClearly()
    {
        var result = AudioTranscoder.Convert(
            _wav.WritePcm(), _wav.PathFor("out.wav"),
            new TranscodeOptions { Normalise = true, BitsPerSample = 32 });

        Assert.False(result.Success);
        Assert.Contains("32-bit", result.Message);
    }

    [Fact]
    public void TranscodeOptions_DefaultsDoNotTouchTheAudio()
    {
        var options = TranscodeOptions.Default;

        Assert.False(options.ModifiesAudio);
        Assert.False(options.Normalise);
        Assert.Null(options.MaxDuration);
    }

    // -------------------------------------------------------------- helpers --

    /// <summary>Samples from a 16-bit PCM WAV, normalised to -1..1.</summary>
    private static float[] ReadSamples(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // Skip the 44-byte canonical header; good enough for files this code just wrote.
        var count = Math.Max(0, (bytes.Length - 44) / 2);
        var samples = new float[count];

        for (var i = 0; i < count; i++)
            samples[i] = BitConverter.ToInt16(bytes, 44 + i * 2) / 32768f;

        return samples;
    }

    private static double PeakOfRange(float[] samples, int start, int length)
    {
        start = Math.Max(0, Math.Min(start, samples.Length));
        var end = Math.Min(samples.Length, start + length);

        var peak = 0.0;
        for (var i = start; i < end; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
        return peak;
    }

    /// <summary>Largest absolute sample in a 16-bit PCM WAV, normalised to 0..1.</summary>
    private static double PeakAmplitude(string path)
    {
        var peak = 0.0;
        foreach (var sample in ReadSamples(path)) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }
}
