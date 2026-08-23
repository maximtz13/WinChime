using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Generates src/WinChime.App/Assets/WinChime.ico.
//
// The mark is a chime: a struck point with sound radiating from it. Chosen over a bell or a
// musical note because concentric arcs stay legible when scaled to 16 px, where fine detail
// turns to mush.

const int CanvasReference = 256;

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

var outputPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "WinChime.App", "Assets", "WinChime.ico"));

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var images = sizes.Select(Render).ToList();
WriteIcon(outputPath, images);

foreach (var image in images) image.Dispose();

Console.WriteLine($"Wrote {outputPath}");
Console.WriteLine($"  sizes : {string.Join(", ", sizes.Select(s => $"{s}x{s}"))}");
Console.WriteLine($"  bytes : {new FileInfo(outputPath).Length:N0}");

static Bitmap Render(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    var scale = size / (float)CanvasReference;

    // Rounded square, inset slightly so antialiasing has room and the shape does not
    // collide with the icon bounds at small sizes.
    var inset = MathF.Max(0.5f, 6f * scale);
    var bounds = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
    var radius = size * 0.22f;

    using var tile = RoundedRect(bounds, radius);

    using (var brush = new LinearGradientBrush(
               new PointF(0, 0), new PointF(size, size),
               ColorTranslator.FromHtml("#4C8DFF"),
               ColorTranslator.FromHtml("#1D4ED8")))
    {
        g.FillPath(brush, tile);
    }

    // Belt and braces: the radii below are sized to fit, but clipping guarantees no stroke
    // can ever bleed past the tile edge if they are ever adjusted.
    g.SetClip(tile);

    // Origin at left-centre with the arcs opening horizontally. An earlier version put the
    // origin bottom-left with arcs opening up-and-right, which is precisely the Wi-Fi
    // glyph — wrong association for an audio app. Horizontal arcs read as sound.
    var originX = size * 0.26f;
    var originY = size * 0.50f;

    var stroke = MathF.Max(1.15f, size * 0.085f);

    using var pen = new Pen(Color.White, stroke)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
    };

    // Below 24 px a third arc collapses into a white smudge, so drop it. Better to render
    // fewer, clearer strokes than to be faithful to the large version and unreadable.
    var arcRadii = size < 24
        ? new[] { 0.24f, 0.42f }
        : new[] { 0.22f, 0.37f, 0.52f };

    // Sized so the widest arc plus half its stroke stays inside the tile. At the previous
    // 0.62 radius over a 104 degree sweep the tips reached the full canvas height and
    // visibly spilled out of the rounded corners.
    foreach (var factor in arcRadii)
    {
        var r = size * factor;

        // Centred on 0 degrees, so the arcs open to the right rather than diagonally.
        g.DrawArc(pen, originX - r, originY - r, r * 2, r * 2, -45f, 90f);
    }

    var dot = MathF.Max(1.6f, size * 0.075f);
    g.FillEllipse(Brushes.White, originX - dot, originY - dot, dot * 2, dot * 2);

    return bitmap;
}

static GraphicsPath RoundedRect(RectangleF bounds, float radius)
{
    var d = radius * 2;
    var path = new GraphicsPath();

    path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
    path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
    path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
    path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
    path.CloseFigure();

    return path;
}

/// <summary>
/// Writes a multi-resolution .ico by hand.
///
/// Frames below 256 px are 32-bit BGRA DIBs, because BMP is what every shell going back to
/// XP understands and an app icon is the wrong place to gamble on a modern renderer.
///
/// The 256 px frame is PNG. That size only exists from Vista onward and every shell able to
/// read it also reads PNG, so the compatibility argument does not apply — while the size
/// argument very much does: as a DIB that one frame is 256 KB, which would more than double
/// the framework-dependent executable on its own. Switching it to PNG took the whole icon
/// from 381 KB to 121 KB.
///
/// Known and accepted caveat: the legacy System.Drawing.Icon API cannot read PNG frames and
/// silently falls back to the next size down. WIC, which is what WPF and the Windows shell
/// actually use, reads all nine frames correctly. Verified, rather than assumed, with
/// BitmapDecoder.
/// </summary>
static void WriteIcon(string path, IReadOnlyList<Bitmap> images)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);                 // reserved
    writer.Write((ushort)1);                 // type: 1 = icon
    writer.Write((ushort)images.Count);

    var frames = images.Select(image => image.Width >= 256 ? BuildPng(image) : BuildDib(image)).ToList();

    // Directory entries come first, so image data starts after all of them.
    var offset = 6 + images.Count * 16;

    for (var i = 0; i < images.Count; i++)
    {
        var image = images[i];

        // 256 is stored as 0: the field is a single byte.
        writer.Write((byte)(image.Width >= 256 ? 0 : image.Width));
        writer.Write((byte)(image.Height >= 256 ? 0 : image.Height));
        writer.Write((byte)0);               // palette size, 0 for true colour
        writer.Write((byte)0);               // reserved
        writer.Write((ushort)1);             // colour planes
        writer.Write((ushort)32);            // bits per pixel
        writer.Write(frames[i].Length);
        writer.Write(offset);

        offset += frames[i].Length;
    }

    foreach (var frame in frames) writer.Write(frame);
}

/// <summary>
/// A PNG frame, stored whole. Unlike a DIB frame there is no doubled height and no AND mask:
/// the PNG is written verbatim and the shell decodes it.
/// </summary>
static byte[] BuildPng(Bitmap image)
{
    using var buffer = new MemoryStream();
    image.Save(buffer, ImageFormat.Png);
    return buffer.ToArray();
}

static byte[] BuildDib(Bitmap image)
{
    var width = image.Width;
    var height = image.Height;

    var xorStride = width * 4;

    // The AND mask is 1 bpp with rows padded to a 4-byte boundary. Alpha does the real work
    // for 32-bit icons, but the mask must still be present and correctly sized.
    var andStride = (width + 31) / 32 * 4;

    var xorSize = xorStride * height;
    var andSize = andStride * height;

    using var buffer = new MemoryStream(40 + xorSize + andSize);
    using var writer = new BinaryWriter(buffer);

    writer.Write(40);                        // BITMAPINFOHEADER size
    writer.Write(width);
    writer.Write(height * 2);                // XOR and AND stacked, per the icon format
    writer.Write((ushort)1);                 // planes
    writer.Write((ushort)32);                // bit count
    writer.Write(0);                         // BI_RGB, no compression
    writer.Write(xorSize + andSize);
    writer.Write(0);                         // horizontal resolution, unused
    writer.Write(0);                         // vertical resolution, unused
    writer.Write(0);                         // palette colours used
    writer.Write(0);                         // important colours

    var data = image.LockBits(
        new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

    try
    {
        var row = new byte[xorStride];

        // DIB scanlines run bottom-up.
        for (var y = height - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                data.Scan0 + y * data.Stride, row, 0, xorStride);

            writer.Write(row);
        }
    }
    finally
    {
        image.UnlockBits(data);
    }

    writer.Write(new byte[andSize]);         // all zero: nothing masked out

    return buffer.ToArray();
}
