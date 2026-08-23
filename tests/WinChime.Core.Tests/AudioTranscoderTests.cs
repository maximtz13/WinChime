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

        var result = AudioTranscoder.Convert(source, destination, sampleRate: 44100, bitsPerSample: 16, channels: 2);

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

    /// <summary>Largest absolute sample in a 16-bit PCM WAV, normalised to 0..1.</summary>
    private static double PeakAmplitude(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var peak = 0;

        // Skip the 44-byte canonical header; good enough for files this code just wrote.
        for (var i = 44; i + 1 < bytes.Length; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(bytes, i));
            if (sample > peak) peak = sample;
        }

        return peak / 32768.0;
    }
}
