namespace WinChime.Core.Personalization;

/// <summary>
/// Contrast arithmetic, used to keep text legible on a colour the user chose rather than one
/// the app picked.
///
/// This exists because WinChime tints itself with the Windows accent, and the accent can be
/// anything. Windows' own swatch list runs from #FFB900 to #4C4A48 — bright yellow through
/// near-black — so no single hardcoded foreground works. White on the yellow is roughly 1.7:1
/// and effectively unreadable; black on it is 12:1.
///
/// The formulas are WCAG 2.1: relative luminance with the sRGB transfer function, and a
/// contrast ratio of (lighter + 0.05) / (darker + 0.05).
/// </summary>
public static class ColorContrast
{
    /// <summary>WCAG AA for normal-size text.</summary>
    public const double AaNormalText = 4.5;

    /// <summary>WCAG AA for large text, and the threshold for non-text elements such as borders.</summary>
    public const double AaLargeText = 3.0;

    /// <summary>
    /// WCAG relative luminance, 0 for black and 1 for white.
    ///
    /// The per-channel curve is not a plain gamma: below a small threshold sRGB is linear, and
    /// using the power function all the way down would misjudge very dark colours.
    /// </summary>
    public static double RelativeLuminance(AccentRgb colour) =>
        0.2126 * Linearise(colour.R) + 0.7152 * Linearise(colour.G) + 0.0722 * Linearise(colour.B);

    private static double Linearise(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>The WCAG contrast ratio between two colours. Always at least 1, at most 21.</summary>
    public static double Ratio(AccentRgb first, AccentRgb second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);

        var (lighter, darker) = a > b ? (a, b) : (b, a);

        return (lighter + 0.05) / (darker + 0.05);
    }

    public static AccentRgb Black { get; } = new(0x00, 0x00, 0x00);

    public static AccentRgb White { get; } = new(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Black or white, whichever is more readable on the given background.
    ///
    /// Deliberately not a luminance threshold. A fixed cutoff gets the mid-tones wrong — the
    /// exact region where the two candidates are closest and the choice matters most — whereas
    /// comparing the two real ratios is both correct and no more work.
    /// </summary>
    public static AccentRgb BestForeground(AccentRgb background) =>
        Ratio(background, Black) >= Ratio(background, White) ? Black : White;

    /// <summary>True when the pair clears WCAG AA for normal text.</summary>
    public static bool MeetsAa(AccentRgb foreground, AccentRgb background) =>
        Ratio(foreground, background) >= AaNormalText;
}
