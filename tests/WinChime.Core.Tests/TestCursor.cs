using System.Text;

namespace WinChime.Core.Tests;

/// <summary>
/// Generates .cur and .ani files rather than committing binary fixtures, for the same reason
/// as <see cref="TestWav"/>: a test can state the property it cares about instead of a
/// reader having to open a blob to discover what makes it interesting.
/// </summary>
public sealed class TestCursor : IDisposable
{
    private readonly string _folder;

    public TestCursor()
    {
        _folder = Path.Combine(Path.GetTempPath(), "WinChime.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public string PathFor(string fileName) => Path.Combine(_folder, fileName);

    /// <summary>A minimal but structurally valid static cursor.</summary>
    public string WriteCur(string fileName = "test.cur", byte width = 32, byte height = 32)
        => WriteIconFormat(fileName, imageType: 2, width, height);

    /// <summary>
    /// An icon rather than a cursor: identical layout, type field 1 instead of 2. Windows
    /// silently ignores one of these when assigned to a cursor role, which is exactly the
    /// mistake worth catching.
    /// </summary>
    public string WriteIcoPretendingToBeCur(string fileName = "actually-an-icon.cur")
        => WriteIconFormat(fileName, imageType: 1, 32, 32);

    /// <summary>A RIFF/ACON animated cursor with a populated anih chunk.</summary>
    public string WriteAni(string fileName = "test.ani", int frames = 6, int steps = 6, int size = 32)
    {
        var path = PathFor(fileName);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        var anihPayload = 36;
        var riffSize = 4 + (8 + anihPayload);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("ACON"));

        writer.Write(Encoding.ASCII.GetBytes("anih"));
        writer.Write(anihPayload);
        writer.Write(36);        // cbSize
        writer.Write(frames);
        writer.Write(steps);
        writer.Write(size);      // width
        writer.Write(size);      // height
        writer.Write(32);        // bit count
        writer.Write(1);         // planes
        writer.Write(10);        // display rate
        writer.Write(1);         // flags

        return path;
    }

    /// <summary>A RIFF container that is not an animated cursor.</summary>
    public string WriteRiffButNotAcon(string fileName = "wrong.ani")
    {
        var path = PathFor(fileName);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(16);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(new byte[8]);

        return path;
    }

    public string WriteGarbage(string fileName = "garbage.cur")
    {
        var path = PathFor(fileName);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("this is definitely not a cursor file at all"));
        return path;
    }

    public string WriteTooSmall(string fileName = "tiny.cur")
    {
        var path = PathFor(fileName);
        File.WriteAllBytes(path, new byte[] { 0, 0, 2, 0 });
        return path;
    }

    private string WriteIconFormat(string fileName, ushort imageType, byte width, byte height)
    {
        var path = PathFor(fileName);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write((ushort)0);          // reserved
        writer.Write(imageType);          // 1 = icon, 2 = cursor
        writer.Write((ushort)1);          // image count

        writer.Write(width);
        writer.Write(height);
        writer.Write((byte)0);            // palette size
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)0);          // hotspot x (planes, for icons)
        writer.Write((ushort)0);          // hotspot y (bit count, for icons)
        writer.Write(64);                 // bytes in resource
        writer.Write(22);                 // offset

        writer.Write(new byte[64]);       // placeholder pixel data

        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp files */ }
    }
}
