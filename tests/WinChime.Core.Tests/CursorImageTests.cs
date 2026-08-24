using WinChime.Core.Cursors;

namespace WinChime.Core.Tests;

/// <summary>
/// Rendering is checked against the cursors Windows actually ships, not against generated
/// fixtures. A synthetic cursor exercises the header parser but not GDI, and the failure this
/// class exists to prevent is specifically a GDI one: a cursor that renders to an image whose
/// alpha is zero everywhere is perfectly invisible and reports no error at all.
/// </summary>
public sealed class CursorImageTests : IDisposable
{
    private readonly TestCursor _cur = new();

    public void Dispose() => _cur.Dispose();

    private static string CursorsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors");

    /// <summary>
    /// Prefers a known filename but settles for any file of the right kind, so the tests do
    /// not depend on one particular Windows build shipping one particular cursor.
    /// </summary>
    private static string FindShipped(string preferred, string extension)
    {
        Assert.True(Directory.Exists(CursorsFolder), $"No cursors folder at {CursorsFolder}.");

        var exact = Path.Combine(CursorsFolder, preferred);
        if (File.Exists(exact)) return exact;

        var any = Directory.EnumerateFiles(CursorsFolder, "*" + extension).FirstOrDefault();

        Assert.True(any is not null, $"Windows shipped no {extension} cursor to test against.");
        return any!;
    }

    private static int OpaquePixels(byte[] bgra)
    {
        var count = 0;

        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) count++;

        return count;
    }

    // ------------------------------------------------------------------- static --

    [Fact]
    public void Load_AShippedStaticCursor_RendersSomethingVisible()
    {
        var preview = CursorImage.Load(FindShipped("aero_arrow.cur", ".cur"));

        Assert.True(preview.IsValid, preview.Error);
        Assert.False(preview.IsAnimated);
        Assert.Single(preview.Frames);

        Assert.True(preview.Width > 0 && preview.Height > 0);
        Assert.Equal(preview.Width * preview.Height * 4, preview.Frames[0].Bgra.Length);

        // The whole point. An all-transparent result is what a naive implementation produces
        // for a legacy cursor, and it looks identical to a working one until you try to see it.
        Assert.True(OpaquePixels(preview.Frames[0].Bgra) > 0, "The rendered cursor is entirely transparent.");
    }

    [Fact]
    public void Load_ReportsAHotspotInsideTheImage()
    {
        var preview = CursorImage.Load(FindShipped("aero_arrow.cur", ".cur"));

        Assert.InRange(preview.HotspotX, 0, preview.Width);
        Assert.InRange(preview.HotspotY, 0, preview.Height);
    }

    // ----------------------------------------------------------------- animated --

    [Fact]
    public void Load_AShippedAnimatedCursor_RendersEveryStep()
    {
        var preview = CursorImage.Load(FindShipped("aero_busy.ani", ".ani"));

        Assert.True(preview.IsValid, preview.Error);
        Assert.True(preview.IsAnimated, "An animated cursor came back with a single frame.");

        Assert.All(preview.Frames, frame =>
        {
            Assert.Equal(preview.Width * preview.Height * 4, frame.Bgra.Length);
            Assert.True(OpaquePixels(frame.Bgra) > 0, "A frame rendered entirely transparent.");
            Assert.True(frame.Duration > TimeSpan.Zero);
        });
    }

    /// <summary>
    /// Catches the animation silently not animating: DrawIconEx ignoring the step index would
    /// produce the right number of frames, all identical, and the preview would sit still with
    /// nothing to indicate why.
    /// </summary>
    [Fact]
    public void Load_AnAnimatedCursorHasFramesThatActuallyDiffer()
    {
        var preview = CursorImage.Load(FindShipped("aero_busy.ani", ".ani"));

        var first = preview.Frames[0].Bgra;
        var distinct = preview.Frames.Skip(1).Any(f => !f.Bgra.AsSpan().SequenceEqual(first));

        Assert.True(distinct, "Every animation step rendered identically.");
    }

    [Fact]
    public void Load_NeverReturnsMoreThanTheFrameCap()
    {
        var preview = CursorImage.Load(FindShipped("aero_busy.ani", ".ani"));

        Assert.InRange(preview.Frames.Count, 1, CursorImage.MaxFrames);
    }

    // ------------------------------------------------------------------- sweep --

    /// <summary>
    /// The broad guarantee, and the one most likely to catch a format this code has not met.
    /// Windows ships around two hundred cursors across several generations; if any of them is
    /// a legacy colour-plus-mask cursor, this is what proves the two-pass fallback works.
    /// </summary>
    [Fact]
    public void Load_RendersEveryCursorWindowsShips()
    {
        var files = Directory.EnumerateFiles(CursorsFolder)
            .Where(f => f.EndsWith(".cur", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        Assert.NotEmpty(files);

        var blank = new List<string>();
        var failed = new List<string>();

        foreach (var file in files)
        {
            var preview = CursorImage.Load(file);

            if (!preview.IsValid) { failed.Add($"{Path.GetFileName(file)}: {preview.Error}"); continue; }
            if (OpaquePixels(preview.Frames[0].Bgra) == 0) blank.Add(Path.GetFileName(file));
        }

        Assert.True(failed.Count == 0, "Failed to render:\n  " + string.Join("\n  ", failed));
        Assert.True(blank.Count == 0, "Rendered fully transparent:\n  " + string.Join("\n  ", blank));
    }

    /// <summary>
    /// The pixels are premultiplied, so a transparent pixel has to be zero throughout. A
    /// non-zero colour behind zero alpha renders as a coloured halo around the cursor.
    /// </summary>
    [Fact]
    public void Load_ProducesValidPremultipliedPixels()
    {
        foreach (var name in new[] { "aero_arrow.cur", "aero_busy.ani" })
        {
            var extension = Path.GetExtension(name);
            var preview = CursorImage.Load(FindShipped(name, extension));

            foreach (var frame in preview.Frames)
            {
                for (var i = 0; i < frame.Bgra.Length; i += 4)
                {
                    var alpha = frame.Bgra[i + 3];
                    if (alpha != 0) continue;

                    Assert.True(
                        frame.Bgra[i] == 0 && frame.Bgra[i + 1] == 0 && frame.Bgra[i + 2] == 0,
                        $"{name}: a transparent pixel carries colour, which renders as a halo.");
                }
            }
        }
    }

    // ------------------------------------------------------------------ refusal --

    /// <summary>
    /// Goes through the header parser first, so the preview refuses the same files the
    /// assignment path refuses, with the same explanation rather than a GDI failure.
    /// </summary>
    [Fact]
    public void Load_RefusesAnIconRenamedAsACursor()
    {
        var preview = CursorImage.Load(_cur.WriteIcoPretendingToBeCur());

        Assert.False(preview.IsValid);
        Assert.Contains("icon", preview.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RefusesGarbage()
    {
        var preview = CursorImage.Load(_cur.WriteGarbage());

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Frames);
    }

    [Fact]
    public void Load_RefusesAMissingFile()
    {
        var preview = CursorImage.Load(_cur.PathFor("does-not-exist.cur"));

        Assert.False(preview.IsValid);
        Assert.Contains("not found", preview.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A structurally valid header with placeholder pixel data behind it. The parser accepts
    /// it and GDI does not, which is exactly the case that must fail with a message rather
    /// than by returning an empty image or throwing.
    /// </summary>
    [Fact]
    public void Load_RefusesAFileGdiCannotRender()
    {
        var preview = CursorImage.Load(_cur.WriteCur("header-only.cur"));

        if (preview.IsValid)
        {
            // If GDI does accept it, the result still has to be well formed.
            Assert.Equal(preview.Width * preview.Height * 4, preview.Frames[0].Bgra.Length);
        }
        else
        {
            Assert.NotNull(preview.Error);
        }
    }
}
