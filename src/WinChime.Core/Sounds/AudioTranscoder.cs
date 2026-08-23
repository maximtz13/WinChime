using NAudio.MediaFoundation;
using NAudio.Wave;
using WinChime.Core.Model;

namespace WinChime.Core.Sounds;

public sealed record TranscodeResult(bool Success, string? OutputPath, string Message)
{
    public static TranscodeResult Fail(string message) => new(false, null, message);
}

/// <summary>
/// Converts arbitrary audio into the uncompressed PCM WAV that Windows sound events require.
///
/// This exists because of a genuinely bad failure mode: Windows accepts any file for a sound
/// event and, if it is not PCM, plays absolutely nothing — no error, no log entry. Detecting
/// that (see <see cref="WaveFile"/>) is only half an answer; the other half is fixing it,
/// rather than telling the user to go and find an audio editor.
///
/// Decoding runs through Media Foundation, so the set of readable formats is whatever the
/// host Windows install supports: MP3, WMA, AAC/M4A and PCM/ADPCM WAV everywhere, with
/// FLAC on Windows 10 and later. Media Foundation is absent on some Windows Server SKUs
/// that lack the Desktop Experience feature, so <see cref="IsAvailable"/> probes for it and
/// callers are expected to degrade to a clear message rather than crashing.
/// </summary>
public static class AudioTranscoder
{
    /// <summary>Windows event sounds are short; 44.1 kHz 16-bit stereo is ample and universally safe.</summary>
    public const int DefaultSampleRate = 44100;
    public const int DefaultBitsPerSample = 16;
    public const int DefaultChannels = 2;

    private static readonly string[] KnownSourceExtensions =
        [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mp4", ".adts"];

    private static bool? _available;

    /// <summary>
    /// True when Media Foundation can be initialised on this machine. Cached, because the
    /// probe costs a COM round-trip and the answer cannot change while the process runs.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available is not null) return _available.Value;

            try
            {
                MediaFoundationApi.Startup();
                _available = true;
            }
            catch (Exception)
            {
                // Missing on Server SKUs without Desktop Experience, and on stripped "N"
                // editions of Windows until the Media Feature Pack is installed.
                _available = false;
            }

            return _available.Value;
        }
    }

    /// <summary>Extension-based pre-filter for file dialogs. Says nothing about whether the file decodes.</summary>
    public static bool LooksLikeSupportedSource(string path) =>
        KnownSourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Filter string for an OpenFileDialog covering everything Media Foundation may decode.</summary>
    public static string OpenFileFilter =>
        "Audio files (*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac)|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac"
        + "|Wave audio (*.wav)|*.wav"
        + "|All files (*.*)|*.*";

    /// <summary>
    /// Where converted files live. They must persist: the registry points at them, so a
    /// temp folder would leave every converted sound broken after a reboot.
    /// </summary>
    public static string ConvertedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinChime", "converted");

    /// <summary>
    /// True when the file already satisfies Windows without conversion, so callers can skip
    /// pointlessly rewriting a file that is already fine.
    /// </summary>
    public static bool NeedsConversion(string path)
    {
        var info = WaveFile.Inspect(path);
        return !info.IsValid || !info.IsPlayableByWindows;
    }

    /// <summary>
    /// Converts into <see cref="ConvertedFolder"/> under a name derived from the source,
    /// and returns the path to the converted file.
    /// </summary>
    /// <param name="destinationFolder">
    /// Defaults to <see cref="ConvertedFolder"/>. Overridable so tests can write somewhere
    /// disposable instead of into the running user's real sound library.
    /// </param>
    public static TranscodeResult ConvertIntoLibrary(
        string sourcePath,
        string? destinationFolder = null,
        int sampleRate = DefaultSampleRate,
        int bitsPerSample = DefaultBitsPerSample,
        int channels = DefaultChannels)
    {
        var folder = destinationFolder ?? ConvertedFolder;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return TranscodeResult.Fail($"Could not create the converted-sounds folder: {ex.Message}");
        }

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "sound";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');

        var destination = Path.Combine(folder, $"{baseName}.wav");

        // Never silently overwrite a file some other event is already pointing at.
        var attempt = 2;
        while (File.Exists(destination) && !PathsMatch(destination, sourcePath))
        {
            destination = Path.Combine(folder, $"{baseName} ({attempt}).wav");
            attempt++;
        }

        return Convert(sourcePath, destination, sampleRate, bitsPerSample, channels);
    }

    /// <summary>Converts <paramref name="sourcePath"/> to PCM WAV at <paramref name="destinationPath"/>.</summary>
    public static TranscodeResult Convert(
        string sourcePath,
        string destinationPath,
        int sampleRate = DefaultSampleRate,
        int bitsPerSample = DefaultBitsPerSample,
        int channels = DefaultChannels)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return TranscodeResult.Fail($"Source file not found: {sourcePath}");

        if (PathsMatch(sourcePath, destinationPath))
            return TranscodeResult.Fail("Source and destination are the same file.");

        if (!IsAvailable)
        {
            return TranscodeResult.Fail(
                "Media Foundation is not available on this Windows installation, so audio cannot be " +
                "converted here. This usually means a Server SKU without Desktop Experience, or an " +
                "N edition without the Media Feature Pack. Supply an uncompressed PCM .wav instead.");
        }

        try
        {
            using var reader = new MediaFoundationReader(sourcePath);
            var targetFormat = new WaveFormat(sampleRate, bitsPerSample, channels);

            // Even a PCM source usually needs this: sample rate, bit depth and channel count
            // rarely all match the target, and the resampler is a no-op when they do.
            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60,   // NAudio maps 60 to the highest-quality MF setting
            };

            WaveFileWriter.CreateWaveFile(destinationPath, resampler);
        }
        catch (Exception ex)
        {
            // A partial file is worse than none: it would pass the RIFF check and then
            // play a fragment or nothing at all.
            TryDelete(destinationPath);

            return TranscodeResult.Fail($"Could not convert {Path.GetFileName(sourcePath)}: {ex.Message}");
        }

        // Prove the output is what we promised rather than assuming the encode was correct.
        var info = WaveFile.Inspect(destinationPath);
        if (!info.IsValid || !info.IsPlayableByWindows)
        {
            TryDelete(destinationPath);
            return TranscodeResult.Fail(
                $"Conversion produced a file Windows still cannot play ({info.Error ?? info.FormatName}).");
        }

        return new TranscodeResult(
            true,
            destinationPath,
            $"Converted to {info.Summary}.");
    }

    private static bool PathsMatch(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
