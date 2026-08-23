using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

public sealed class WaveFileTests : IDisposable
{
    private readonly TestWav _wav = new();

    public void Dispose() => _wav.Dispose();

    [Fact]
    public void Inspect_ValidPcm_ReportsFormatAccurately()
    {
        var path = _wav.WritePcm(sampleRate: 48000, channels: 1, bitsPerSample: 16);

        var info = WaveFile.Inspect(path);

        Assert.True(info.IsValid);
        Assert.Equal(1, info.FormatTag);
        Assert.Equal("PCM", info.FormatName);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.True(info.IsPlayableByWindows);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(3.25)]
    public void Inspect_ValidPcm_ComputesDurationFromDataChunk(double seconds)
    {
        var path = _wav.WritePcm(seconds: seconds);

        var info = WaveFile.Inspect(path);

        Assert.Equal(seconds, info.Duration.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Inspect_TypicalEventSound_ProducesNoWarnings()
    {
        var path = _wav.WritePcm(seconds: 1.5);

        var info = WaveFile.Inspect(path);

        Assert.Empty(info.Warnings);
    }

    /// <summary>
    /// The whole reason WaveFile exists. Windows accepts a non-PCM file for a sound event
    /// and then plays nothing, with no error and no log entry, so the file has to be
    /// reported as valid-but-unplayable rather than simply rejected.
    /// </summary>
    [Fact]
    public void Inspect_NonPcm_IsStructurallyValidButNotPlayable()
    {
        var path = _wav.WriteNonPcm();

        var info = WaveFile.Inspect(path);

        Assert.True(info.IsValid);
        Assert.False(info.IsPlayableByWindows);
        Assert.Contains(info.Warnings, w => w.Contains("PCM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inspect_NonPcm_NamesTheEncodingInTheWarning()
    {
        var path = _wav.WriteNonPcm(formatTag: 0x0055);   // MP3 inside a WAV container

        var info = WaveFile.Inspect(path);

        Assert.Contains("MP3", info.FormatName);
    }

    [Fact]
    public void Inspect_NotRiff_IsInvalidAndSaysWhy()
    {
        var path = _wav.WriteNotRiff();

        var info = WaveFile.Inspect(path);

        Assert.False(info.IsValid);
        Assert.False(info.IsPlayableByWindows);
        Assert.Contains("RIFF", info.Error);
    }

    [Fact]
    public void Inspect_MissingFile_IsInvalid()
    {
        var info = WaveFile.Inspect(_wav.PathFor("does-not-exist.wav"));

        Assert.False(info.IsValid);
        Assert.Contains("not found", info.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_TruncatedFile_IsInvalid()
    {
        var info = WaveFile.Inspect(_wav.WriteTruncated());

        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void Inspect_NoAudioData_WarnsThatItWillBeSilent()
    {
        var info = WaveFile.Inspect(_wav.WriteEmptyData());

        Assert.True(info.IsValid);
        Assert.Contains(info.Warnings, w => w.Contains("silent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inspect_OverlyLongFile_WarnsAboutOverlap()
    {
        // Comfortably past the 10s threshold for a system event sound.
        var info = WaveFile.Inspect(_wav.WritePcm(seconds: 15, sampleRate: 8000, channels: 1, bitsPerSample: 8));

        Assert.True(info.IsValid);
        Assert.Contains(info.Warnings, w => w.Contains("long", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Summary_ForValidFile_IsHumanReadable()
    {
        var info = WaveFile.Inspect(_wav.WritePcm(seconds: 2, sampleRate: 22050, channels: 2, bitsPerSample: 16));

        Assert.Contains("PCM", info.Summary);
        Assert.Contains("22.1 kHz", info.Summary);
        Assert.Contains("16-bit", info.Summary);
        Assert.Contains("stereo", info.Summary);
    }

    [Fact]
    public void Summary_ForInvalidFile_ReportsTheError()
    {
        var info = WaveFile.Inspect(_wav.WriteNotRiff());

        Assert.Equal(info.Error, info.Summary);
    }
}
