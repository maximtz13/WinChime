using NAudio.MediaFoundation;
using NAudio.Wave;
using WinChime.Core.Model;

namespace WinChime.Core.Sounds;

public sealed record TranscodeResult(bool Success, string? OutputPath, string Message)
{
    public static TranscodeResult Fail(string message) => new(false, null, message);
}

/// <summary>
/// Converts arbitrary audio into the uncompressed PCM WAV that Windows sound events require,
/// optionally trimming and normalising on the way through.
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
    private static readonly string[] KnownSourceExtensions =
        [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mp4", ".adts"];

    /// <summary>
    /// Trimming and normalising need the audio in memory. Event sounds are seconds long, so
    /// this ceiling is far above any legitimate input while keeping a pathological file from
    /// exhausting memory. Two minutes of 44.1 kHz stereo float is roughly 42 MB.
    /// </summary>
    private static readonly TimeSpan MaxBufferedDuration = TimeSpan.FromMinutes(2);

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

    /// <param name="destinationFolder">
    /// Defaults to <see cref="ConvertedFolder"/>. Overridable so tests can write somewhere
    /// disposable instead of into the running user's real sound library.
    /// </param>
    public static TranscodeResult ConvertIntoLibrary(
        string sourcePath,
        string? destinationFolder = null,
        TranscodeOptions? options = null)
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

        return Convert(sourcePath, destination, options);
    }

    /// <summary>Converts <paramref name="sourcePath"/> to PCM WAV at <paramref name="destinationPath"/>.</summary>
    public static TranscodeResult Convert(
        string sourcePath,
        string destinationPath,
        TranscodeOptions? options = null)
    {
        options ??= TranscodeOptions.Default;

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return TranscodeResult.Fail($"Source file not found: {sourcePath}");

        if (PathsMatch(sourcePath, destinationPath))
            return TranscodeResult.Fail("Source and destination are the same file.");

        if (options.ModifiesAudio && options.BitsPerSample is not (16 or 24))
        {
            return TranscodeResult.Fail(
                $"Trimming and normalising are implemented for 16- and 24-bit output, not {options.BitsPerSample}-bit.");
        }

        if (!IsAvailable)
        {
            return TranscodeResult.Fail(
                "Media Foundation is not available on this Windows installation, so audio cannot be " +
                "converted here. This usually means a Server SKU without Desktop Experience, or an " +
                "N edition without the Media Feature Pack. Supply an uncompressed PCM .wav instead.");
        }

        ProcessingOutcome? outcome = null;

        try
        {
            using var reader = new MediaFoundationReader(sourcePath);
            var targetFormat = new WaveFormat(options.SampleRate, options.BitsPerSample, options.Channels);

            // Even a PCM source usually needs this: sample rate, bit depth and channel count
            // rarely all match the target, and the resampler is a no-op when they do.
            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60,   // NAudio maps 60 to the highest-quality MF setting
            };

            if (options.ModifiesAudio)
            {
                outcome = WriteProcessed(resampler, targetFormat, destinationPath, options);
            }
            else
            {
                // Streaming path: no reason to buffer when nothing is being altered.
                WaveFileWriter.CreateWaveFile(destinationPath, resampler);
            }
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

        return new TranscodeResult(true, destinationPath, DescribeResult(info, outcome, options));
    }

    // ------------------------------------------------------------------ processing --

    private sealed record ProcessingOutcome(bool Trimmed, double GainDb, bool HitBufferCap);

    /// <summary>
    /// Decodes into memory so the peak can be measured before any gain is applied, then
    /// trims, fades and writes. Two passes over the audio are unavoidable for peak
    /// normalisation: you cannot know the correct gain until you have seen every sample.
    /// </summary>
    private static ProcessingOutcome WriteProcessed(
        IWaveProvider source, WaveFormat targetFormat, string destinationPath, TranscodeOptions options)
    {
        var channels = targetFormat.Channels;
        var sampleRate = targetFormat.SampleRate;

        var capFrames = (long)(MaxBufferedDuration.TotalSeconds * sampleRate);
        var requestedFrames = options.MaxDuration is { } limit
            ? (long)Math.Round(limit.TotalSeconds * sampleRate)
            : long.MaxValue;

        var maxFrames = Math.Min(requestedFrames, capFrames);

        var samples = source.ToSampleProvider();
        var buffer = new List<float>();
        var chunk = new float[sampleRate * channels];   // one second at a time
        var cutShort = false;

        int read;
        while (buffer.Count / channels < maxFrames && (read = samples.Read(chunk, 0, chunk.Length)) > 0)
        {
            var room = (int)Math.Min(read, (maxFrames - buffer.Count / channels) * channels);

            for (var i = 0; i < room; i++) buffer.Add(chunk[i]);

            if (room < read) { cutShort = true; break; }
        }

        // Reaching the limit exactly is not the same as cutting audio off. Only a further
        // read proves there was more, and only then does the fade earn its place.
        if (!cutShort && maxFrames != long.MaxValue && buffer.Count / channels >= maxFrames)
            cutShort = samples.Read(chunk, 0, chunk.Length) > 0;

        var gainDb = 0.0;
        if (options.Normalise && buffer.Count > 0)
        {
            var peak = 0f;
            foreach (var sample in buffer)
            {
                var magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }

            // Below this the clip is effectively silence, and "normalising" it would just
            // amplify the noise floor into something audible and unpleasant.
            if (peak > 0.0001f)
            {
                var gain = options.TargetPeak / peak;
                var ceiling = Math.Pow(10, options.MaxGainDb / 20.0);
                gain = Math.Min(gain, ceiling);

                for (var i = 0; i < buffer.Count; i++) buffer[i] = (float)(buffer[i] * gain);

                gainDb = 20 * Math.Log10(gain);
            }
        }

        if (cutShort && options.FadeOut > TimeSpan.Zero && buffer.Count > 0)
        {
            var totalFrames = buffer.Count / channels;
            var fadeFrames = (int)Math.Min(
                Math.Round(options.FadeOut.TotalSeconds * sampleRate),
                totalFrames);

            var startFrame = totalFrames - fadeFrames;

            for (var frame = 0; frame < fadeFrames; frame++)
            {
                var gain = 1f - (float)frame / fadeFrames;
                for (var channel = 0; channel < channels; channel++)
                    buffer[(startFrame + frame) * channels + channel] *= gain;
            }
        }

        using (var writer = new WaveFileWriter(destinationPath, targetFormat))
        {
            // Clamp rather than let gain wrap around into loud digital distortion.
            foreach (var sample in buffer) writer.WriteSample(Math.Clamp(sample, -1f, 1f));
        }

        var hitCap = cutShort && requestedFrames > capFrames;
        return new ProcessingOutcome(cutShort, gainDb, hitCap);
    }

    private static string DescribeResult(WaveInfo info, ProcessingOutcome? outcome, TranscodeOptions options)
    {
        var message = $"Converted to {info.Summary}.";

        if (outcome is null) return message;

        if (outcome.Trimmed)
        {
            message += options.MaxDuration is { } limit && !outcome.HitBufferCap
                ? $" Trimmed to {limit.TotalSeconds:0.#}s with a short fade at the cut."
                : $" Trimmed at the {MaxBufferedDuration.TotalMinutes:0} minute processing limit.";
        }

        if (Math.Abs(outcome.GainDb) > 0.05)
            message += $" Volume {(outcome.GainDb > 0 ? "raised" : "lowered")} by {Math.Abs(outcome.GainDb):0.#} dB.";
        else if (options.Normalise)
            message += " Volume was already at the target level.";

        return message;
    }

    // -------------------------------------------------------------------- helpers --

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
