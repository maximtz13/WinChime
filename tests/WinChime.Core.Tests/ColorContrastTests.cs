using WinChime.Core.Personalization;

namespace WinChime.Core.Tests;

/// <summary>
/// Pins the contrast arithmetic against values that can be checked independently. Every
/// expected number here comes from the WCAG 2.1 definition rather than from this
/// implementation, so the tests would catch the formula being subtly wrong rather than merely
/// being consistent with itself.
/// </summary>
public sealed class ColorContrastTests
{
    private static readonly AccentRgb Black = new(0, 0, 0);
    private static readonly AccentRgb White = new(255, 255, 255);

    // ---------------------------------------------------------------- luminance --

    [Fact]
    public void RelativeLuminance_OfBlackAndWhite_AreTheEndsOfTheScale()
    {
        Assert.Equal(0.0, ColorContrast.RelativeLuminance(Black), 6);
        Assert.Equal(1.0, ColorContrast.RelativeLuminance(White), 6);
    }

    /// <summary>
    /// Mid-grey is the case that catches a missing sRGB transfer function. #808080 is halfway
    /// up the 8-bit scale but only about 21.6% of the way up in light, not 50%.
    /// </summary>
    [Fact]
    public void RelativeLuminance_OfMidGrey_AccountsForGamma()
    {
        Assert.Equal(0.2158, ColorContrast.RelativeLuminance(new AccentRgb(0x80, 0x80, 0x80)), 3);
    }

    /// <summary>
    /// Green carries far more perceived luminance than blue at the same 8-bit value. If the
    /// channel weights were ever equalised this is the test that would fail.
    /// </summary>
    [Fact]
    public void RelativeLuminance_WeightsGreenFarAboveBlue()
    {
        var green = ColorContrast.RelativeLuminance(new AccentRgb(0, 255, 0));
        var blue = ColorContrast.RelativeLuminance(new AccentRgb(0, 0, 255));

        Assert.Equal(0.7152, green, 4);
        Assert.Equal(0.0722, blue, 4);
    }

    /// <summary>
    /// Below 0.03928 the sRGB curve is linear rather than a power function. A very dark colour
    /// exercises that branch, which an implementation using Math.Pow throughout would get wrong.
    /// </summary>
    [Fact]
    public void RelativeLuminance_UsesTheLinearSegmentForVeryDarkChannels()
    {
        // 8/255 is 0.0314, inside the linear segment. Linear: 0.0314 / 12.92 = 0.002429.
        Assert.Equal(0.002429, ColorContrast.RelativeLuminance(new AccentRgb(8, 8, 8)), 5);
    }

    // -------------------------------------------------------------------- ratio --

    [Fact]
    public void Ratio_BlackOnWhite_IsTwentyOne()
    {
        Assert.Equal(21.0, ColorContrast.Ratio(Black, White), 3);
    }

    [Fact]
    public void Ratio_OfAColourWithItself_IsOne()
    {
        Assert.Equal(1.0, ColorContrast.Ratio(new AccentRgb(0x25, 0x59, 0x4A), new AccentRgb(0x25, 0x59, 0x4A)), 6);
    }

    [Fact]
    public void Ratio_IsSymmetric()
    {
        var a = new AccentRgb(0x00, 0x78, 0xD7);
        var b = new AccentRgb(0xFF, 0xFF, 0xFF);

        Assert.Equal(ColorContrast.Ratio(a, b), ColorContrast.Ratio(b, a), 9);
    }

    /// <summary>
    /// The Windows default accent on white lands at 4.4989:1 — fractionally *under* AA, not
    /// over it. Worth pinning precisely, because the entire shade-walking design in
    /// AccentTheme turns on this colour being on the failing side of the line by a hair.
    /// </summary>
    [Fact]
    public void Ratio_OfTheDefaultWindowsBlueOnWhite_IsJustUnderAa()
    {
        var blue = new AccentRgb(0x00, 0x78, 0xD7);

        Assert.Equal(4.499, ColorContrast.Ratio(blue, White), 3);
        Assert.False(ColorContrast.MeetsAa(White, blue));
    }

    // -------------------------------------------------------------- foreground --

    /// <summary>
    /// The case this class exists for. #FFB900 is a real Windows accent swatch; white on it is
    /// about 1.7:1 and unreadable, black on it is about 12:1.
    /// </summary>
    [Fact]
    public void BestForeground_OnTheBrightYellowSwatch_IsBlack()
    {
        Assert.Equal(Black, ColorContrast.BestForeground(new AccentRgb(0xFF, 0xB9, 0x00)));
    }

    [Fact]
    public void BestForeground_OnTheNearBlackSwatch_IsWhite()
    {
        Assert.Equal(White, ColorContrast.BestForeground(new AccentRgb(0x4C, 0x4A, 0x48)));
    }

    /// <summary>
    /// Black, by 4.67 to 4.50 — a margin small enough to be invisible, on a colour every
    /// Windows user has seen carrying white text. This is exactly why AccentTheme does not use
    /// BestForeground to pick its own foreground: taking the arithmetic at face value would put
    /// black text on the default blue accent button, and give two near-identical reds opposite
    /// treatments. The method is correct; it is just answering a narrower question than "what
    /// should this button look like".
    /// </summary>
    [Fact]
    public void BestForeground_OnTheDefaultBlue_IsBlackByAHair()
    {
        var blue = new AccentRgb(0x00, 0x78, 0xD7);

        Assert.Equal(Black, ColorContrast.BestForeground(blue));

        Assert.Equal(4.67, ColorContrast.Ratio(Black, blue), 2);
        Assert.Equal(4.50, ColorContrast.Ratio(White, blue), 2);
    }

    /// <summary>
    /// Whatever the accent, the chosen foreground must actually be readable on it. Running this
    /// across every swatch Windows offers is the real guarantee: it is the full set of colours
    /// a user can pick in Settings without typing a hex code.
    /// </summary>
    [Fact]
    public void BestForeground_ClearsAaOnEveryWindowsSwatch()
    {
        foreach (var swatch in AccentColorService.Presets)
        {
            var foreground = ColorContrast.BestForeground(swatch);
            var ratio = ColorContrast.Ratio(foreground, swatch);

            Assert.True(ratio >= ColorContrast.AaNormalText,
                $"{swatch.Hex}: best foreground {foreground.Hex} gives only {ratio:F2}:1");
        }
    }

    /// <summary>
    /// A mid-tone where the two candidates are close is where a naive luminance cutoff goes
    /// wrong, so the choice is checked to be the genuinely better of the two rather than merely
    /// plausible.
    /// </summary>
    [Fact]
    public void BestForeground_PicksTheHigherRatioAtTheCrossover()
    {
        foreach (var value in new byte[] { 0x77, 0x7C, 0x80, 0x84, 0x8C })
        {
            var grey = new AccentRgb(value, value, value);
            var chosen = ColorContrast.BestForeground(grey);
            var other = chosen == Black ? White : Black;

            Assert.True(ColorContrast.Ratio(chosen, grey) >= ColorContrast.Ratio(other, grey),
                $"#{value:X2} grey: picked {chosen.Hex} but the other option scored higher");
        }
    }

    [Fact]
    public void MeetsAa_DrawsTheLineAtFourAndAHalf()
    {
        Assert.True(ColorContrast.MeetsAa(Black, White));
        Assert.False(ColorContrast.MeetsAa(new AccentRgb(0x99, 0x99, 0x99), White));
    }
}
