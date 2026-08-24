using WinChime.Core.Personalization;

namespace WinChime.Core.Tests;

/// <summary>
/// The accent is whatever the user set it to, so the guarantees here have to hold for every
/// colour rather than for a chosen example. The sweeps over
/// <see cref="AccentColorService.Presets"/> are the real tests: that list is the complete set
/// of colours reachable from Settings without typing a hex code, and it deliberately includes
/// the awkward extremes — #FFB900 is bright yellow, #4C4A48 is nearly black.
/// </summary>
public sealed class AccentThemeTests
{
    /// <summary>
    /// Mirrors the page background tokens in Theme/Tokens.Light.xaml and Tokens.Dark.xaml.
    /// Duplicated rather than shared because Core has no reference to the WPF layer; if the
    /// palette moves, these move with it.
    /// </summary>
    private static readonly AccentRgb LightPage = new(0xF3, 0xF3, 0xF3);

    private static readonly AccentRgb DarkPage = new(0x1E, 0x1E, 0x1E);

    private static readonly AppTheme[] BothThemes = [AppTheme.Light, AppTheme.Dark];

    // ------------------------------------------------------------- readability --

    /// <summary>
    /// The load-bearing test, and the reason the walk exists at all. Every simpler rule that
    /// was measured fails somewhere on this list.
    /// </summary>
    [Fact]
    public void For_TextClearsAaOnEveryWindowsSwatchInBothThemes()
    {
        foreach (var theme in BothThemes)
        {
            foreach (var swatch in AccentColorService.Presets)
            {
                var accent = AccentTheme.For(swatch, theme);
                var ratio = ColorContrast.Ratio(accent.Foreground, accent.Fill);

                Assert.True(ratio >= ColorContrast.AaNormalText,
                    $"{theme}: {swatch.Hex} resolved to fill {accent.Fill.Hex} with " +
                    $"foreground {accent.Foreground.Hex}, only {ratio:F2}:1");
            }
        }
    }

    /// <summary>
    /// Readable text on an invisible button is not a win. A filled control has to be
    /// distinguishable from the page it sits on, which WCAG puts at 3:1 for a non-text element.
    /// </summary>
    [Fact]
    public void For_KeepsEveryFillVisibleAgainstItsPage()
    {
        foreach (var (theme, page) in new[] { (AppTheme.Light, LightPage), (AppTheme.Dark, DarkPage) })
        {
            foreach (var swatch in AccentColorService.Presets)
            {
                var fill = AccentTheme.For(swatch, theme).Fill;
                var ratio = ColorContrast.Ratio(fill, page);

                Assert.True(ratio >= ColorContrast.AaLargeText,
                    $"{theme}: {swatch.Hex} resolved to fill {fill.Hex}, only {ratio:F2}:1 against the page");
            }
        }
    }

    // ------------------------------------------------------------------ colour --

    /// <summary>
    /// The failure mode that ruled out lightening toward a fixed shade in the dark theme. A
    /// channel already at 255 has no headroom to scale into, so every lighter shade of #FFB900
    /// is pure white and the accent stops being an accent.
    /// </summary>
    [Fact]
    public void For_NeverResolvesToPlainWhiteOrBlack()
    {
        foreach (var theme in BothThemes)
        {
            foreach (var swatch in AccentColorService.Presets)
            {
                var fill = AccentTheme.For(swatch, theme).Fill;

                Assert.False(fill == ColorContrast.White || fill == ColorContrast.Black,
                    $"{theme}: {swatch.Hex} lost its colour entirely, resolving to {fill.Hex}");
            }
        }
    }

    /// <summary>An accent that already works is the user's colour, and is left alone.</summary>
    [Fact]
    public void For_LeavesAReadableAccentExactlyAsChosen()
    {
        var yellow = new AccentRgb(0xFF, 0xB9, 0x00);

        // Black on this yellow is 12:1, so the dark theme has no reason to touch it.
        Assert.Equal(yellow, AccentTheme.For(yellow, AppTheme.Dark).Fill);
    }

    /// <summary>
    /// White on the default Windows blue is 4.4989:1 — just under AA, by enough that it has to
    /// move. One step down the ladder takes it to 6.42:1 while still plainly reading as blue.
    /// </summary>
    [Fact]
    public void For_DarkensTheDefaultBlueJustEnoughInTheLightTheme()
    {
        var resolved = AccentTheme.For(new AccentRgb(0x00, 0x78, 0xD7), AppTheme.Light);

        Assert.Equal(new AccentRgb(0x00, 0x60, 0xAC), resolved.Fill);
        Assert.True(ColorContrast.Ratio(resolved.Foreground, resolved.Fill) > 6.0);
    }

    /// <summary>
    /// Fixed by convention rather than computed. Letting the arithmetic choose picks black on
    /// eighteen of the twenty-eight swatches, including ones where the two options are within
    /// 4% of each other, so near-identical colours end up looking deliberately different.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Light, 0xFF)]
    [InlineData(AppTheme.Dark, 0x00)]
    public void For_UsesWhiteTextInLightAndBlackInDark(AppTheme theme, byte expectedChannel)
    {
        foreach (var swatch in AccentColorService.Presets)
        {
            var foreground = AccentTheme.For(swatch, theme).Foreground;

            Assert.Equal(new AccentRgb(expectedChannel, expectedChannel, expectedChannel), foreground);
        }
    }

    // ------------------------------------------------------------------- state --

    /// <summary>
    /// Hover and pressed move away from the foreground, so interacting with a control can only
    /// ever make its label easier to read. This is what makes the states safe without needing
    /// their own contrast walk.
    /// </summary>
    [Fact]
    public void For_HoverAndPressedNeverReduceContrast()
    {
        foreach (var theme in BothThemes)
        {
            foreach (var swatch in AccentColorService.Presets)
            {
                var accent = AccentTheme.For(swatch, theme);

                var rest = ColorContrast.Ratio(accent.Foreground, accent.Fill);
                var hover = ColorContrast.Ratio(accent.Foreground, accent.Hover);
                var pressed = ColorContrast.Ratio(accent.Foreground, accent.Pressed);

                Assert.True(hover >= rest, $"{theme} {swatch.Hex}: hover {hover:F2} below rest {rest:F2}");
                Assert.True(pressed >= hover, $"{theme} {swatch.Hex}: pressed {pressed:F2} below hover {hover:F2}");
            }
        }
    }

    /// <summary>The three states must be visibly different, or the control feels dead.</summary>
    [Fact]
    public void For_GivesTheThreeStatesDistinctColours()
    {
        foreach (var theme in BothThemes)
        {
            foreach (var swatch in AccentColorService.Presets)
            {
                var accent = AccentTheme.For(swatch, theme);

                Assert.NotEqual(accent.Fill, accent.Hover);
                Assert.NotEqual(accent.Hover, accent.Pressed);
            }
        }
    }

    /// <summary>
    /// The light theme darkens on interaction and the dark theme lightens. Checking the
    /// direction explicitly guards against the foreground and the shift being inverted
    /// together, which would keep every ratio test passing while looking wrong.
    /// </summary>
    [Fact]
    public void For_ShiftsTowardsBlackInLightAndTowardsWhiteInDark()
    {
        var blue = new AccentRgb(0x00, 0x78, 0xD7);

        var light = AccentTheme.For(blue, AppTheme.Light);
        Assert.True(ColorContrast.RelativeLuminance(light.Pressed) < ColorContrast.RelativeLuminance(light.Fill));

        var dark = AccentTheme.For(blue, AppTheme.Dark);
        Assert.True(ColorContrast.RelativeLuminance(dark.Pressed) > ColorContrast.RelativeLuminance(dark.Fill));
    }

    /// <summary>
    /// A hand-typed colour is not restricted to the swatch list, so the guarantees are also
    /// checked across a spread of the whole cube rather than only the colours Settings offers.
    /// </summary>
    [Fact]
    public void For_HoldsUpAcrossTheWholeColourCube()
    {
        foreach (var theme in BothThemes)
        {
            for (var r = 0; r <= 255; r += 51)
            {
                for (var g = 0; g <= 255; g += 51)
                {
                    for (var b = 0; b <= 255; b += 51)
                    {
                        var swatch = new AccentRgb((byte)r, (byte)g, (byte)b);
                        var accent = AccentTheme.For(swatch, theme);
                        var ratio = ColorContrast.Ratio(accent.Foreground, accent.Fill);

                        Assert.True(ratio >= ColorContrast.AaNormalText,
                            $"{theme}: {swatch.Hex} resolved to {accent.Fill.Hex}, only {ratio:F2}:1");
                    }
                }
            }
        }
    }
}
