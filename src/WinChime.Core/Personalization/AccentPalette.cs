namespace WinChime.Core.Personalization;

/// <summary>A plain 8-bit RGB colour. Deliberately not System.Drawing.Color: Core has no UI dependency.</summary>
public readonly record struct AccentRgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => Hex;

    /// <summary>Parses #RRGGBB or RRGGBB. Returns false rather than throwing on junk.</summary>
    public static bool TryParse(string? text, out AccentRgb colour)
    {
        colour = default;

        var trimmed = text?.Trim().TrimStart('#');
        if (trimmed is not { Length: 6 }) return false;

        if (!byte.TryParse(trimmed[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(trimmed[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(trimmed[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        colour = new AccentRgb(r, g, b);
        return true;
    }
}

/// <summary>
/// The eight-shade ladder Windows stores in Explorer\Accent\AccentPalette.
///
/// Windows does not document this, so the layout and the shade factors were derived from a
/// live machine: the palette was read back and each entry matched byte-for-byte against
/// Windows.UI.ViewManagement.UISettings.GetColorValue, which is the API Windows itself
/// answers accent questions with. That established both the ordering and that index 3, not
/// index 0 or 5, is the primary accent.
///
/// The shades turn out to be a straight multiplicative scale of the accent, which preserves
/// hue and saturation exactly. The factors below reproduce the sampled machine's palette to
/// within one 8-bit step on every channel.
///
/// Being an approximation of an undocumented algorithm, a shade may occasionally differ by
/// one from what the Settings app would produce. That is a cosmetic difference in a tint,
/// not a functional one.
/// </summary>
public static class AccentPalette
{
    /// <summary>Eight entries of four bytes: R, G, B, A.</summary>
    public const int ByteLength = 32;

    /// <summary>Index of the accent itself. The three before it are lighter, the three after darker.</summary>
    public const int AccentIndex = 3;

    /// <summary>
    /// Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, in the order Windows stores them.
    /// </summary>
    private static readonly double[] ShadeFactors = [1.696, 1.405, 1.211, 1.0, 0.802, 0.599, 0.317];

    /// <summary>
    /// Explorer\Accent\AccentColorMenu is a further step below Dark3. Verified: on the
    /// sampled machine 0.135 reproduced the stored value exactly on all three channels.
    /// </summary>
    public const double MenuFactor = 0.135;

    /// <summary>Dark1 is what Windows stores as StartColorMenu.</summary>
    public const int StartColorShadeIndex = 4;

    /// <summary>
    /// The final four bytes are not a colour. Windows stores a fixed value with zero alpha
    /// there, so it is reproduced verbatim rather than computed.
    /// </summary>
    private static readonly byte[] TrailingSentinel = [0x88, 0x17, 0x98, 0x00];

    /// <summary>The seven shades, lightest first, with the accent at <see cref="AccentIndex"/>.</summary>
    public static IReadOnlyList<AccentRgb> Shades(AccentRgb accent) =>
        ShadeFactors.Select(factor => Scale(accent, factor)).ToList();

    public static AccentRgb MenuShade(AccentRgb accent) => Scale(accent, MenuFactor);

    public static AccentRgb StartShade(AccentRgb accent) => Scale(accent, ShadeFactors[StartColorShadeIndex]);

    /// <summary>The 32 bytes to write to AccentPalette.</summary>
    public static byte[] ToBytes(AccentRgb accent)
    {
        var bytes = new byte[ByteLength];
        var shades = Shades(accent);

        for (var i = 0; i < shades.Count; i++)
        {
            bytes[i * 4 + 0] = shades[i].R;
            bytes[i * 4 + 1] = shades[i].G;
            bytes[i * 4 + 2] = shades[i].B;
            bytes[i * 4 + 3] = 0xFF;
        }

        TrailingSentinel.CopyTo(bytes, 7 * 4);
        return bytes;
    }

    /// <summary>Reads the accent back out of a stored palette, or null when the data is unusable.</summary>
    public static AccentRgb? AccentFromBytes(byte[]? palette)
    {
        if (palette is null || palette.Length < ByteLength) return null;

        var offset = AccentIndex * 4;
        return new AccentRgb(palette[offset], palette[offset + 1], palette[offset + 2]);
    }

    /// <summary>
    /// Scales a colour toward black or white.
    ///
    /// Multiplying is correct for darkening and for lightening a mid-tone, and it preserves
    /// hue exactly. It breaks down near white: once a channel would exceed 255 it clamps
    /// while the others keep rising, which shifts the hue — a light blue lightens into grey
    /// rather than into pale blue. When that would happen, the colour is blended toward
    /// white instead, by the amount the scale could not deliver.
    /// </summary>
    private static AccentRgb Scale(AccentRgb colour, double factor)
    {
        if (factor <= 1.0)
        {
            return new AccentRgb(
                ClampToByte(colour.R * factor),
                ClampToByte(colour.G * factor),
                ClampToByte(colour.B * factor));
        }

        var brightest = Math.Max(colour.R, Math.Max(colour.G, colour.B));

        // Black cannot be lightened by multiplying, so treat it as a pure blend to white.
        if (brightest == 0) return BlendToWhite(colour, factor - 1.0);

        var headroom = 255.0 / brightest;

        if (factor <= headroom)
        {
            return new AccentRgb(
                ClampToByte(colour.R * factor),
                ClampToByte(colour.G * factor),
                ClampToByte(colour.B * factor));
        }

        // Multiply as far as the headroom allows, then cover the shortfall by blending.
        var scaled = new AccentRgb(
            ClampToByte(colour.R * headroom),
            ClampToByte(colour.G * headroom),
            ClampToByte(colour.B * headroom));

        var shortfall = Math.Clamp((factor - headroom) / Math.Max(factor - 1.0, 1e-6), 0.0, 1.0);
        return BlendToWhite(scaled, shortfall);
    }

    private static AccentRgb BlendToWhite(AccentRgb colour, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        return new AccentRgb(
            ClampToByte(colour.R + (255 - colour.R) * amount),
            ClampToByte(colour.G + (255 - colour.G) * amount),
            ClampToByte(colour.B + (255 - colour.B) * amount));
    }

    private static byte ClampToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
