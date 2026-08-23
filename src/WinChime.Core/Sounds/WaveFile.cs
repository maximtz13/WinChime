using System.Text;
using WinChime.Core.Model;

namespace WinChime.Core.Sounds;

/// <summary>
/// Minimal RIFF/WAVE header reader.
///
/// This exists because Windows event sounds fail *silently* on unsupported files:
/// assign an MP3-with-a-.wav-extension and the event simply produces no sound, with
/// no error anywhere. Validating up front and telling the user is the whole point.
/// </summary>
public static class WaveFile
{
    private const int MaxReasonableEventSeconds = 10;
    private const long MaxReasonableBytes = 5 * 1024 * 1024;

    public static WaveInfo Inspect(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return Invalid($"File not found: {path}");

            using var fs = fi.OpenRead();
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

            if (fi.Length < 44)
                return Invalid("File is too small to be a valid WAV.");

            if (ReadFourCC(br) != "RIFF")
                return Invalid("Not a RIFF file. Windows sound events require an uncompressed PCM .wav.");

            br.ReadUInt32(); // riff chunk size (unreliable in the wild; ignored)

            if (ReadFourCC(br) != "WAVE")
                return Invalid("RIFF container is not WAVE.");

            int formatTag = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
            uint avgBytesPerSec = 0, dataBytes = 0;
            var sawFmt = false;

            while (fs.Position + 8 <= fs.Length)
            {
                var chunkId = ReadFourCC(br);
                var chunkSize = br.ReadUInt32();
                var chunkStart = fs.Position;

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    formatTag = br.ReadUInt16();
                    channels = br.ReadUInt16();
                    sampleRate = (int)br.ReadUInt32();
                    avgBytesPerSec = br.ReadUInt32();
                    br.ReadUInt16(); // nBlockAlign
                    bitsPerSample = br.ReadUInt16();
                    sawFmt = true;
                }
                else if (chunkId == "data")
                {
                    dataBytes = chunkSize;
                }

                // Chunks are word-aligned; odd sizes carry a pad byte.
                var next = chunkStart + chunkSize + (chunkSize % 2);
                if (next <= chunkStart || next > fs.Length) break;
                fs.Position = next;
            }

            if (!sawFmt)
                return Invalid("WAV file has no 'fmt ' chunk.");

            var duration = avgBytesPerSec > 0
                ? TimeSpan.FromSeconds((double)dataBytes / avgBytesPerSec)
                : TimeSpan.Zero;

            var warnings = new List<string>();
            var formatName = DescribeFormat(formatTag);

            if (formatTag != 1 && formatTag != 0xFFFE)
            {
                warnings.Add(
                    $"Encoding is {formatName}, not uncompressed PCM. Windows will assign this file " +
                    "but the event will play silently. Re-encode to 16-bit PCM WAV.");
            }

            if (duration.TotalSeconds > MaxReasonableEventSeconds)
                warnings.Add($"{duration.TotalSeconds:0.#}s is long for a system event; it will overlap other sounds.");

            if (fi.Length > MaxReasonableBytes)
                warnings.Add($"{fi.Length / 1024.0 / 1024.0:0.#} MB is large for an event sound; it adds disk I/O to every trigger.");

            if (dataBytes == 0)
                warnings.Add("No audio data chunk found; this file will be silent.");

            return new WaveInfo(
                IsValid: true,
                FormatName: formatName,
                FormatTag: formatTag,
                Channels: channels,
                SampleRate: sampleRate,
                BitsPerSample: bitsPerSample,
                Duration: duration,
                FileBytes: fi.Length,
                Warnings: warnings,
                Error: null);
        }
        catch (Exception ex)
        {
            return Invalid($"Could not read file: {ex.Message}");
        }
    }

    private static string ReadFourCC(BinaryReader br) => new(br.ReadChars(4));

    private static WaveInfo Invalid(string error) =>
        new(false, "Unknown", 0, 0, 0, 0, TimeSpan.Zero, 0, Array.Empty<string>(), error);

    private static string DescribeFormat(int tag) => tag switch
    {
        0x0001 => "PCM",
        0x0002 => "Microsoft ADPCM",
        0x0003 => "IEEE float",
        0x0006 => "A-law",
        0x0007 => "mu-law",
        0x0011 => "IMA ADPCM",
        0x0031 => "GSM 6.10",
        0x0055 => "MP3 (in WAV container)",
        0xFFFE => "WAVE_FORMAT_EXTENSIBLE",
        _ => $"format 0x{tag:X4}",
    };
}
