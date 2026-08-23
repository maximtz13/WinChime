using NAudio.MediaFoundation;
using NAudio.Wave;
using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

/// <summary>
/// Real audio for the transcoding tests: an audible tone rather than the silence
/// <see cref="TestWav"/> produces, plus genuine MP3 encoding.
///
/// A silent buffer is fine for header-parsing tests but useless for transcoding, where the
/// question is whether audio actually survives a lossy round trip.
/// </summary>
public static class TestAudio
{
    /// <summary>Writes a sine tone as PCM WAV and returns the path.</summary>
    public static string WriteTone(
        string path,
        double seconds = 1.0,
        int frequencyHz = 440,
        int sampleRate = 44100,
        int channels = 1)
    {
        var format = new WaveFormat(sampleRate, 16, channels);
        using var writer = new WaveFileWriter(path, format);

        var frames = (int)(sampleRate * seconds);
        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * 0.25);
            for (var c = 0; c < channels; c++) writer.WriteSample(sample);
        }

        return path;
    }

    /// <summary>
    /// Whether this machine can produce an MP3. The GitHub Actions Windows runners are
    /// Server SKUs, where media codecs are not guaranteed, so tests needing a real MP3
    /// are skipped with a reason rather than silently passing.
    /// </summary>
    public static bool CanEncodeMp3
    {
        get
        {
            if (!AudioTranscoder.IsAvailable) return false;

            try
            {
                MediaFoundationApi.Startup();
                var format = new WaveFormat(44100, 16, 1);
                return MediaFoundationEncoder.SelectMediaType(
                    AudioSubtypes.MFAudioFormat_MP3, format, 128000) is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Encodes an existing PCM WAV to MP3, giving tests a genuinely non-PCM input.</summary>
    public static string WriteMp3(string sourceWavPath, string destinationPath, int bitrate = 128000)
    {
        MediaFoundationApi.Startup();
        using var reader = new AudioFileReader(sourceWavPath);
        MediaFoundationEncoder.EncodeToMp3(reader, destinationPath, bitrate);
        return destinationPath;
    }
}

/// <summary>
/// Marks a test that cannot run without Media Foundation. xUnit reports it as skipped with
/// this reason, so an environment lacking codecs is visible in the run output instead of
/// looking like a pass.
/// </summary>
public sealed class RequiresMediaFoundationAttribute : FactAttribute
{
    public RequiresMediaFoundationAttribute()
    {
        if (!AudioTranscoder.IsAvailable)
            Skip = "Media Foundation is not available on this machine.";
    }
}

/// <summary>As above, but also needs an MP3 encoder to build the fixture.</summary>
public sealed class RequiresMp3EncoderAttribute : FactAttribute
{
    public RequiresMp3EncoderAttribute()
    {
        if (!AudioTranscoder.IsAvailable)
            Skip = "Media Foundation is not available on this machine.";
        else if (!TestAudio.CanEncodeMp3)
            Skip = "No MP3 encoder is available on this machine.";
    }
}
