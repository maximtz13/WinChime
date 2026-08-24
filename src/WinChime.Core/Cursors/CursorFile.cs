using System.Text;

namespace WinChime.Core.Cursors;

public sealed record CursorInfo(
    bool IsValid,
    string FormatName,
    bool IsAnimated,
    int Width,
    int Height,
    int Frames,
    long FileBytes,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    /// <summary>
    /// Length of the animation sequence, which is not the same as the frame count: a seq
    /// chunk can play twelve steps out of six frames, and it is steps that DrawIconEx indexes.
    /// One for a static cursor.
    /// </summary>
    public int Steps { get; init; } = 1;

    /// <summary>
    /// Default step duration in jiffies (sixtieths of a second), from the anih header. Zero
    /// when the file does not say, which real files sometimes do not.
    /// </summary>
    public int DisplayRateJiffies { get; init; }

    /// <summary>
    /// Per-step durations from the optional rate chunk, in jiffies. Empty when the file has
    /// no rate chunk, in which case every step lasts <see cref="DisplayRateJiffies"/>.
    /// </summary>
    public IReadOnlyList<int> StepRates { get; init; } = Array.Empty<int>();

    /// <summary>
    /// How long a given step should be shown. Falls back through the rate chunk, then the
    /// header rate, then a sixth of a second, which is what Windows uses when a file is
    /// silent on the question.
    /// </summary>
    public TimeSpan DurationOf(int step)
    {
        var jiffies = step >= 0 && step < StepRates.Count ? StepRates[step] : DisplayRateJiffies;

        if (jiffies <= 0) jiffies = 10;

        return TimeSpan.FromSeconds(jiffies / 60.0);
    }

    public string Summary => IsValid
        ? IsAnimated
            ? $"{FormatName}, {Width}x{Height}, {Frames} frame(s)"
            : $"{FormatName}, {Width}x{Height}"
        : Error ?? "Unreadable file";
}

/// <summary>
/// Reads .cur and .ani headers.
///
/// The same reasoning as <see cref="Sounds.WaveFile"/>: Windows fails quietly here too.
/// Point a cursor role at a file that is not a cursor and Windows does not complain — it
/// silently falls back to the system default, so the user sees no change and no error and
/// concludes the app is broken.
/// </summary>
public static class CursorFile
{
    /// <summary>Windows renders a cursor larger than this poorly, if at all.</summary>
    private const int UnusuallyLarge = 256;

    public static CursorInfo Inspect(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return Invalid($"File not found: {path}");
            if (fi.Length < 16) return Invalid("File is too small to be a cursor.");

            using var stream = fi.OpenRead();
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var magic = new string(reader.ReadChars(4));

            // Animated cursors are RIFF containers with an ACON form type.
            if (magic == "RIFF")
            {
                stream.Position = 8;
                var form = new string(reader.ReadChars(4));

                return form == "ACON"
                    ? ReadAni(stream, reader, fi.Length)
                    : Invalid($"RIFF file is {form}, not an animated cursor (ACON).");
            }

            stream.Position = 0;
            return ReadCur(reader, fi.Length);
        }
        catch (Exception ex)
        {
            return Invalid($"Could not read file: {ex.Message}");
        }
    }

    /// <summary>
    /// A .cur is an icon-format file whose type field is 2 rather than 1. The distinction
    /// matters: a renamed .ico has type 1, no hotspot, and Windows will not use it.
    /// </summary>
    private static CursorInfo ReadCur(BinaryReader reader, long fileBytes)
    {
        var reserved = reader.ReadUInt16();
        var type = reader.ReadUInt16();
        var count = reader.ReadUInt16();

        if (reserved != 0) return Invalid("Not a cursor file: the reserved header field is not zero.");

        if (type == 1)
        {
            return Invalid(
                "This is an icon (.ico), not a cursor (.cur). Icons carry no hotspot, so Windows " +
                "will ignore it and keep using the system cursor.");
        }

        if (type != 2) return Invalid($"Unknown image type {type}; expected 2 for a cursor.");
        if (count == 0) return Invalid("Cursor file contains no images.");

        // 0 in the byte-sized dimension fields means 256.
        var width = reader.ReadByte();
        var height = reader.ReadByte();

        var actualWidth = width == 0 ? 256 : width;
        var actualHeight = height == 0 ? 256 : height;

        var warnings = new List<string>();
        if (actualWidth > UnusuallyLarge || actualHeight > UnusuallyLarge)
            warnings.Add($"{actualWidth}x{actualHeight} is unusually large for a cursor and may render poorly.");

        return new CursorInfo(
            true, "Static cursor", false, actualWidth, actualHeight, count, fileBytes, warnings, null);
    }

    /// <summary>
    /// The anih chunk carries the real metadata. Its dimension fields are frequently zero in
    /// files produced by real tools, which means "inherit from the embedded icons" rather
    /// than a malformed file, so zero is reported rather than treated as an error.
    /// </summary>
    private static CursorInfo ReadAni(FileStream stream, BinaryReader reader, long fileBytes)
    {
        stream.Position = 12;

        var found = false;
        int frames = 0, steps = 0, width = 0, height = 0, displayRate = 0;
        var stepRates = Array.Empty<int>();

        // Scans the whole file rather than stopping at anih, because the optional rate chunk
        // follows it and carries the per-step timings the preview animates on.
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var chunkStart = stream.Position;

            if (chunkId == "anih" && chunkSize >= 36)
            {
                reader.ReadUInt32();                        // cbSize
                frames = (int)reader.ReadUInt32();
                steps = (int)reader.ReadUInt32();
                width = (int)reader.ReadUInt32();
                height = (int)reader.ReadUInt32();
                reader.ReadUInt32();                        // bit count
                reader.ReadUInt32();                        // planes
                displayRate = (int)reader.ReadUInt32();     // jiffies per step

                found = true;
            }
            else if (chunkId == "rate" && chunkSize >= 4)
            {
                var count = (int)(chunkSize / 4);
                var rates = new int[count];

                for (var i = 0; i < count; i++) rates[i] = (int)reader.ReadUInt32();

                stepRates = rates;
            }

            var next = chunkStart + chunkSize + (chunkSize % 2);   // chunks are word-aligned
            if (next <= chunkStart || next > stream.Length) break;

            stream.Position = next;
        }

        if (!found) return Invalid("Animated cursor has no anih header chunk.");

        var warnings = new List<string>();
        if (frames == 0) warnings.Add("Animated cursor declares zero frames; it will not animate.");
        if (steps > 1 && frames <= 1) warnings.Add("Animation sequence is longer than the frame count.");

        return new CursorInfo(true, "Animated cursor", true, width, height, frames, fileBytes, warnings, null)
        {
            Steps = steps > 0 ? steps : Math.Max(frames, 1),
            DisplayRateJiffies = displayRate,
            StepRates = stepRates,
        };
    }

    private static CursorInfo Invalid(string error) =>
        new(false, "Unknown", false, 0, 0, 0, 0, Array.Empty<string>(), error);
}
