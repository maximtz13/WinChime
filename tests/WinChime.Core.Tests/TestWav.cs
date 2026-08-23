using System.Text;

namespace WinChime.Core.Tests;

/// <summary>
/// Generates WAV files on the fly rather than committing binary fixtures.
///
/// Synthesising them means a test can state exactly the property it cares about — this file
/// is 2.5 seconds, this one claims to be MP3-in-a-WAV-container — instead of a reader
/// having to open a binary blob to find out what makes it interesting.
/// </summary>
public sealed class TestWav : IDisposable
{
    private readonly string _folder;

    public TestWav()
    {
        _folder = Path.Combine(Path.GetTempPath(), "WinChime.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public string PathFor(string fileName) => System.IO.Path.Combine(_folder, fileName);

    /// <summary>A well-formed uncompressed PCM WAV of the requested shape.</summary>
    public string WritePcm(
        string fileName = "test.wav",
        double seconds = 1.0,
        int sampleRate = 44100,
        short channels = 2,
        short bitsPerSample = 16)
        => Write(fileName, formatTag: 1, seconds, sampleRate, channels, bitsPerSample);

    /// <summary>
    /// A structurally valid WAV whose fmt chunk declares a non-PCM encoding. Windows accepts
    /// files like this for a sound event and then plays nothing at all, which is the exact
    /// failure WaveFile exists to warn about.
    /// </summary>
    public string WriteNonPcm(string fileName = "notpcm.wav", short formatTag = 0x0055)
        => Write(fileName, formatTag, seconds: 1.0, 44100, 2, 16);

    /// <summary>
    /// Not a RIFF container at all, despite the extension. Deliberately longer than the
    /// 44-byte minimum header size, so inspection gets past the too-small check and
    /// actually reaches the RIFF magic-number test. <see cref="WriteTruncated"/> covers
    /// the short case.
    /// </summary>
    public string WriteNotRiff(string fileName = "fake.wav")
    {
        var path = PathFor(fileName);
        var text = "This is plainly not a RIFF file, and it is long enough to prove it. " +
                   "Padding so the size check passes and the magic number check runs.";

        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(text));
        return path;
    }

    /// <summary>Valid header, zero audio frames. Assignable but silent.</summary>
    public string WriteEmptyData(string fileName = "empty.wav")
        => Write(fileName, formatTag: 1, seconds: 0, 44100, 2, 16);

    /// <summary>Too short to contain even a minimal 44-byte WAV header.</summary>
    public string WriteTruncated(string fileName = "tiny.wav")
    {
        var path = PathFor(fileName);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("RIFF"));
        return path;
    }

    private string Write(
        string fileName, short formatTag, double seconds, int sampleRate, short channels, short bitsPerSample)
    {
        var path = PathFor(fileName);

        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var dataBytes = (int)Math.Round(seconds * byteRate / blockAlign) * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);                 // PCM fmt chunk size
        writer.Write(formatTag);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);   // silence is fine; nothing here plays it

        return path;
    }

    /// <summary>
    /// Deletes this instance's own folder only.
    ///
    /// It deliberately does NOT tidy up the shared WinChime.Tests parent. xUnit runs test
    /// classes in parallel, and deleting the parent the moment it looks empty raced with
    /// another class creating its subfolder inside it — Directory.CreateDirectory creates
    /// missing parents, then creates the leaf, and the parent can vanish in between. That
    /// produced intermittent DirectoryNotFoundException failures. An empty directory left
    /// in TEMP is a much smaller problem than a flaky suite.
    /// </summary>
    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp files */ }
    }
}
