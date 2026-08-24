using System.Runtime.InteropServices;
using WinChime.Core.Interop;

namespace WinChime.Core.Cursors;

/// <summary>One rendered animation step and how long it should be shown.</summary>
public sealed record CursorFrame(byte[] Bgra, TimeSpan Duration);

/// <summary>
/// A cursor rendered to pixels, ready for whatever the UI layer wants to do with it.
///
/// Deliberately raw bytes rather than any image type: Core has no UI dependency, and the
/// alternative types belong to either WPF or System.Drawing.
/// </summary>
public sealed record CursorPreview(
    IReadOnlyList<CursorFrame> Frames,
    int Width,
    int Height,
    int HotspotX,
    int HotspotY,
    string? Error)
{
    public bool IsValid => Error is null && Frames.Count > 0;

    public bool IsAnimated => Frames.Count > 1;

    public static CursorPreview Failed(string error) =>
        new(Array.Empty<CursorFrame>(), 0, 0, 0, 0, error);
}

/// <summary>
/// Rasterises a .cur or .ani so it can actually be looked at.
///
/// There is no managed route to this. WPF decodes neither format, and an animated cursor has
/// no single image to decode in any case: the frames live in a RIFF container and only
/// DrawIconEx will render a chosen step.
///
/// The pixels come back premultiplied, which is what DrawIconEx produces when it composites
/// onto a zeroed surface, and also what WPF wants natively.
///
/// The awkward part is alpha. A modern 32-bit cursor carries its own alpha channel and draws
/// correctly in one pass. A legacy cursor does not: it is a colour bitmap plus a 1-bit AND
/// mask, and GDI signals transparency by leaving those destination pixels untouched. Drawn
/// onto a transparent surface that produces an image whose alpha is zero everywhere — a
/// perfectly invisible preview, with no error to explain it. So the result is checked, and a
/// cursor that came back empty is drawn again in two passes and recombined by hand.
/// </summary>
public static class CursorImage
{
    /// <summary>
    /// Enough for any real animated cursor; the Windows busy pointer has 18. The cap exists
    /// so a malformed or hostile header cannot ask for a million frames of 256x256.
    /// </summary>
    public const int MaxFrames = 64;

    public static CursorPreview Load(string path)
    {
        // Reuses the header parser rather than trusting the file: it is what rejects an .ico
        // renamed to .cur, and it supplies the step count and timings.
        var info = CursorFile.Inspect(path);
        if (!info.IsValid) return CursorPreview.Failed(info.Error ?? "Unreadable cursor file.");

        var cursor = NativeMethods.LoadCursorFromFile(path);
        if (cursor == IntPtr.Zero) return CursorPreview.Failed("Windows could not load that cursor file.");

        try
        {
            if (!NativeMethods.GetIconInfo(cursor, out var iconInfo))
                return CursorPreview.Failed("Windows could not read the cursor's shape.");

            try
            {
                var (width, height) = MeasureFrom(iconInfo);

                if (width <= 0 || height <= 0)
                    return CursorPreview.Failed("The cursor reports no usable size.");

                var steps = info.IsAnimated ? Math.Clamp(info.Steps, 1, MaxFrames) : 1;
                var frames = new List<CursorFrame>(steps);

                for (var step = 0; step < steps; step++)
                {
                    var pixels = RenderStep(cursor, width, height, step);
                    if (pixels is null) break;

                    frames.Add(new CursorFrame(pixels, info.DurationOf(step)));
                }

                if (frames.Count == 0) return CursorPreview.Failed("The cursor produced no image.");

                return new CursorPreview(
                    frames, width, height, iconInfo.xHotspot, iconInfo.yHotspot, null);
            }
            finally
            {
                // GetIconInfo hands over two bitmaps that the caller owns. Leaking them leaks
                // GDI handles, which is a process-wide limit.
                if (iconInfo.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.hbmColor);
            }
        }
        catch (Exception ex)
        {
            return CursorPreview.Failed($"Could not render the cursor: {ex.Message}");
        }
        finally
        {
            NativeMethods.DestroyCursor(cursor);
        }
    }

    /// <summary>
    /// The size Windows actually loaded, which is not always the size in the file: a cursor is
    /// loaded at the system cursor size. That is the right thing to show, since it is the size
    /// the pointer will really be.
    /// </summary>
    private static (int Width, int Height) MeasureFrom(NativeMethods.IconInfo iconInfo)
    {
        var header = default(NativeMethods.BitmapHeader);
        var size = Marshal.SizeOf<NativeMethods.BitmapHeader>();

        if (iconInfo.hbmColor != IntPtr.Zero
            && NativeMethods.GetObject(iconInfo.hbmColor, size, ref header) != 0)
        {
            return (header.bmWidth, header.bmHeight);
        }

        if (iconInfo.hbmMask != IntPtr.Zero
            && NativeMethods.GetObject(iconInfo.hbmMask, size, ref header) != 0)
        {
            // A cursor with no colour bitmap is monochrome, and its mask holds the AND and XOR
            // halves stacked vertically, so the real height is half of what the bitmap reports.
            return (header.bmWidth, header.bmHeight / 2);
        }

        return (0, 0);
    }

    private static byte[]? RenderStep(IntPtr cursor, int width, int height, int step)
    {
        var pixels = DrawToBuffer(cursor, width, height, step, NativeMethods.DI_NORMAL);
        if (pixels is null) return null;

        // A modern cursor is already done. Anything fully transparent is either a legacy
        // cursor whose alpha GDI never wrote, or a genuinely blank frame; the two-pass path
        // resolves both correctly, so it is cheaper to just try it than to distinguish them.
        if (HasAnyOpaquePixel(pixels)) return pixels;

        var colour = DrawToBuffer(cursor, width, height, step, NativeMethods.DI_IMAGE);
        var mask = DrawToBuffer(cursor, width, height, step, NativeMethods.DI_MASK);

        if (colour is null || mask is null) return pixels;

        CombineWithMask(colour, mask);
        return colour;
    }

    /// <summary>
    /// Draws one step into a fresh top-down 32-bit DIB and copies the result out.
    ///
    /// Top-down (a negative height) matters: the default for a DIB is bottom-up, which would
    /// hand back an upside-down cursor.
    /// </summary>
    private static byte[]? DrawToBuffer(IntPtr cursor, int width, int height, int step, uint flags)
    {
        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;

        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            var header = new NativeMethods.BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            };

            bitmap = NativeMethods.CreateDIBSection(
                dc, ref header, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);

            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;

            previous = NativeMethods.SelectObject(dc, bitmap);

            if (!NativeMethods.DrawIconEx(dc, 0, 0, cursor, width, height, (uint)step, IntPtr.Zero, flags))
                return null;

            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            return buffer;
        }
        finally
        {
            // The bitmap cannot be deleted while it is selected into the DC.
            if (previous != IntPtr.Zero) NativeMethods.SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);

            NativeMethods.DeleteDC(dc);
        }
    }

    private static bool HasAnyOpaquePixel(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return true;

        return false;
    }

    /// <summary>
    /// Rebuilds the alpha channel of a legacy cursor from its AND and XOR bits, in place on
    /// the colour buffer.
    ///
    /// A legacy cursor composites as <c>screen = (screen AND mask) XOR image</c>, which gives
    /// four cases, not two:
    ///
    ///   AND 0, XOR anything — the image is painted directly. Opaque.
    ///   AND 1, XOR 0        — the screen is left alone. Transparent.
    ///   AND 1, XOR 1        — the screen is INVERTED.
    ///
    /// That last case is not an edge case: it is how the classic text I-beam works, and
    /// Windows still ships it. beam_l.cur has an all-ones mask and carries its whole shape in
    /// the XOR bits, so treating "AND 1" as simply transparent renders it completely
    /// invisible — which is exactly what the first version of this did, and what the sweep
    /// over every shipped cursor caught.
    ///
    /// An inverting pixel has no colour of its own, so there is no correct answer without a
    /// background to invert. Black is the useful one: it is what inverting over a light
    /// surface looks like, and it keeps the shape legible.
    ///
    /// Transparent pixels are cleared to zero throughout, because the result is premultiplied
    /// and a premultiplied pixel with zero alpha must be zero or it renders as a halo.
    /// </summary>
    private static void CombineWithMask(byte[] colour, byte[] mask)
    {
        for (var i = 0; i < colour.Length; i += 4)
        {
            var masked = mask[i] > 127 && mask[i + 1] > 127 && mask[i + 2] > 127;

            if (!masked)
            {
                colour[i + 3] = 255;
                continue;
            }

            var inverts = colour[i] > 127 && colour[i + 1] > 127 && colour[i + 2] > 127;

            colour[i] = 0;
            colour[i + 1] = 0;
            colour[i + 2] = 0;
            colour[i + 3] = inverts ? (byte)255 : (byte)0;
        }
    }
}
