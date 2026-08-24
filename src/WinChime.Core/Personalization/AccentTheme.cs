namespace WinChime.Core.Personalization;

/// <summary>
/// The four colours needed to draw an accent-filled control: the fill, the text on it, and the
/// hover and pressed variants of the fill.
/// </summary>
public sealed record AccentTheme(AccentRgb Fill, AccentRgb Foreground, AccentRgb Hover, AccentRgb Pressed)
{
    /// <summary>
    /// Picks an accent treatment that stays readable whatever colour the user has chosen.
    ///
    /// WinChime tints itself with the Windows accent, and the accent is not a colour the app
    /// gets to pick. Windows' own swatch list runs from #FFB900 to #4C4A48, and measuring the
    /// obvious approaches against that list shows all of them failing somewhere:
    ///
    /// - A fixed white foreground on the raw accent fails on eleven of the twenty-eight
    ///   swatches; on the yellow it is 1.72:1, which is unreadable.
    /// - A fixed shade with a fixed foreground, which is what Windows itself does, bottoms out
    ///   at 2.68:1 (white on the yellow darkened one step).
    /// - Simply taking whichever of black or white scores higher passes everywhere, but picks
    ///   black on eighteen swatches including the classic #0078D7 blue, where the two are
    ///   within 4% of each other. Two near-identical reds then get opposite treatments, which
    ///   looks like a bug rather than a decision.
    ///
    /// So the foreground is fixed by convention — white in the light theme, black in the dark
    /// theme, which is what every filled accent button in Windows does — and the *fill* moves
    /// instead. The accent is used as-is when it can carry that text, and otherwise walks the
    /// shade ladder away from the foreground until it can.
    ///
    /// Measured across all twenty-eight Windows swatches this clears AA in both themes with no
    /// exceptions, keeps every fill at least 3:1 against its page background, and never blows a
    /// saturated accent out to plain white. AccentThemeTests pins all three.
    /// </summary>
    public static AccentTheme For(AccentRgb accent, AppTheme theme)
    {
        var dark = theme == AppTheme.Dark;

        // Light theme puts white on the fill, so the fill has to get darker to gain contrast;
        // dark theme puts black on it, so the fill gets lighter. In both cases the walk moves
        // away from the foreground, which means contrast can only ever increase along it.
        var preferred = dark ? ColorContrast.Black : ColorContrast.White;
        var fill = WalkUntilReadable(accent, preferred, lighter: dark);

        // The ladder only has three steps either side of the accent, and lightening a very dark
        // saturated colour barely moves it: every lighter shade of #000033 is still nearly
        // black, so the dark theme cannot reach a fill that carries black text. None of the
        // Windows swatches hits this, but a hand-typed colour can, and unreadable text is worse
        // than an unconventional foreground. Convention gives way only when it has to.
        var foreground = ColorContrast.Ratio(preferred, fill) >= ColorContrast.AaNormalText
            ? preferred
            : ColorContrast.BestForeground(fill);

        // Hover and pressed move away from whichever foreground was settled on, so a control
        // cannot become less legible by being interacted with.
        var away = foreground == ColorContrast.White ? ColorContrast.Black : ColorContrast.White;

        return new AccentTheme(
            fill,
            foreground,
            Blend(fill, away, HoverShift),
            Blend(fill, away, PressedShift));
    }

    private const double HoverShift = 0.08;
    private const double PressedShift = 0.16;

    /// <summary>
    /// The first shade that carries the foreground at WCAG AA, starting from the accent itself
    /// so a colour that already works is left exactly as the user chose it.
    ///
    /// Walking outward rather than jumping straight to a fixed shade is what preserves hue: on
    /// a saturated accent such as #FFB900 every lighter shade is pure white, because a channel
    /// already at 255 has no headroom to scale into. Starting at the accent avoids that
    /// entirely, since the yellow carries black text at 12:1 with no adjustment at all.
    /// </summary>
    private static AccentRgb WalkUntilReadable(AccentRgb accent, AccentRgb foreground, bool lighter)
    {
        var shades = AccentPalette.Shades(accent);

        // Shades run lightest first with the accent in the middle, so walking lighter counts
        // down from the accent and walking darker counts up.
        var step = lighter ? -1 : 1;

        AccentRgb last = accent;

        for (var i = AccentPalette.AccentIndex; i >= 0 && i < shades.Count; i += step)
        {
            last = shades[i];
            if (ColorContrast.Ratio(foreground, last) >= ColorContrast.AaNormalText) return last;
        }

        // Unreachable for any of the Windows swatches, and only possible at all for a
        // hand-typed colour. The end of the ladder is near-black or near-white, so it is the
        // best available answer rather than a failure.
        return last;
    }

    /// <summary>
    /// A straight linear mix. Deliberately not the multiplicative ladder: scaling a colour that
    /// already has a channel at 255 has no headroom, so <see cref="AccentPalette"/> blends the
    /// whole shortfall to white and an intended 8% nudge comes back as pure white.
    /// </summary>
    private static AccentRgb Blend(AccentRgb from, AccentRgb to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        return new AccentRgb(
            Mix(from.R, to.R, amount),
            Mix(from.G, to.G, amount),
            Mix(from.B, to.B, amount));
    }

    private static byte Mix(byte from, byte to, double amount) =>
        (byte)Math.Clamp(Math.Round(from + (to - from) * amount, MidpointRounding.AwayFromZero), 0, 255);
}
