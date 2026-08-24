using Microsoft.Win32;
using WinChime.Core.Personalization;

namespace WinChime.Core.Tests;

public sealed class AccentPaletteTests
{
    /// <summary>
    /// The real accent from the machine this was developed against, together with the exact
    /// shade ladder Windows produced for it. Every value was cross-checked against
    /// Windows.UI.ViewManagement.UISettings, so this is Windows' own answer rather than an
    /// assumption about it.
    /// </summary>
    private static readonly AccentRgb SampledAccent = new(37, 89, 74);

    private static readonly AccentRgb[] SampledLadder =
    [
        new(63, 151, 125),   // AccentLight3
        new(52, 125, 104),   // AccentLight2
        new(45, 108, 89),    // AccentLight1
        new(37, 89, 74),     // Accent
        new(30, 71, 59),     // AccentDark1
        new(22, 54, 44),     // AccentDark2
        new(12, 28, 23),     // AccentDark3
    ];

    /// <summary>
    /// The load-bearing test. The shade factors are reverse-engineered from an undocumented
    /// algorithm, so this pins them against real Windows output. Allowing one 8-bit step of
    /// difference is deliberate: the approximation is not exact, and a tint being one value
    /// off is invisible, whereas demanding exactness would make the test fragile for no gain.
    /// </summary>
    [Fact]
    public void Shades_ReproduceTheLadderWindowsGenerated()
    {
        var shades = AccentPalette.Shades(SampledAccent);

        Assert.Equal(SampledLadder.Length, shades.Count);

        for (var i = 0; i < shades.Count; i++)
        {
            Assert.True(Close(shades[i], SampledLadder[i]),
                $"Shade {i}: generated {shades[i].Hex}, Windows produced {SampledLadder[i].Hex}");
        }
    }

    [Fact]
    public void AccentIndex_IsTheColourItself()
    {
        Assert.Equal(SampledAccent, AccentPalette.Shades(SampledAccent)[AccentPalette.AccentIndex]);
    }

    /// <summary>Verified exactly on the sampled machine, so this one is not approximate.</summary>
    [Fact]
    public void MenuShade_MatchesTheStoredValue()
    {
        Assert.True(Close(AccentPalette.MenuShade(SampledAccent), new AccentRgb(5, 12, 10)));
    }

    [Fact]
    public void StartShade_IsTheFirstDarkStep()
    {
        Assert.Equal(AccentPalette.Shades(SampledAccent)[4], AccentPalette.StartShade(SampledAccent));
    }

    [Fact]
    public void ToBytes_ProducesThirtyTwoBytesEndingInTheSentinel()
    {
        var bytes = AccentPalette.ToBytes(SampledAccent);

        Assert.Equal(AccentPalette.ByteLength, bytes.Length);

        // The last entry is not a colour: Windows stores a fixed value with zero alpha.
        Assert.Equal(0x00, bytes[31]);
        Assert.Equal(0x88, bytes[28]);
    }

    [Fact]
    public void ToBytes_StoresShadesAsRgbaWithOpaqueAlpha()
    {
        var bytes = AccentPalette.ToBytes(SampledAccent);
        var offset = AccentPalette.AccentIndex * 4;

        Assert.Equal(SampledAccent.R, bytes[offset]);
        Assert.Equal(SampledAccent.G, bytes[offset + 1]);
        Assert.Equal(SampledAccent.B, bytes[offset + 2]);
        Assert.Equal(0xFF, bytes[offset + 3]);
    }

    [Fact]
    public void RoundTrip_ThroughBytesRecoversTheAccent()
    {
        Assert.Equal(SampledAccent, AccentPalette.AccentFromBytes(AccentPalette.ToBytes(SampledAccent)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(16)]
    public void AccentFromBytes_RejectsUnusableData(int? length)
    {
        var bytes = length is null ? null : new byte[length.Value];
        Assert.Null(AccentPalette.AccentFromBytes(bytes));
    }

    /// <summary>
    /// Multiplying breaks near white: channels clamp unevenly and the hue drifts toward grey.
    /// A light blue must lighten into a paler blue, not into something desaturated.
    /// </summary>
    [Fact]
    public void Shades_OfALightColour_StayInHueRatherThanWashingOutToGrey()
    {
        var lightBlue = new AccentRgb(120, 190, 250);
        var lightest = AccentPalette.Shades(lightBlue)[0];

        // Blue must remain the dominant channel and red the weakest, as in the source.
        Assert.True(lightest.B >= lightest.G, $"Blue stopped dominating: {lightest.Hex}");
        Assert.True(lightest.G >= lightest.R, $"Channel order inverted: {lightest.Hex}");
        Assert.True(lightest.R > lightBlue.R, "The light shade should be lighter than the accent.");
    }

    [Fact]
    public void Shades_OfWhite_DoNotOverflow()
    {
        var shades = AccentPalette.Shades(new AccentRgb(255, 255, 255));

        Assert.All(shades, s => Assert.True(s.R <= 255 && s.G <= 255 && s.B <= 255));
        Assert.Equal(new AccentRgb(255, 255, 255), shades[0]);
    }

    /// <summary>Multiplying black by anything is still black, so lightening has to blend instead.</summary>
    [Fact]
    public void Shades_OfBlack_StillProduceLighterSteps()
    {
        var shades = AccentPalette.Shades(new AccentRgb(0, 0, 0));

        Assert.True(shades[0].R > 0, "Lightening black produced black.");
        Assert.True(shades[0].R > shades[2].R, "The light steps are not ordered.");
    }

    [Fact]
    public void Shades_AreOrderedLightestToDarkest()
    {
        var shades = AccentPalette.Shades(new AccentRgb(0, 120, 215));

        for (var i = 1; i < shades.Count; i++)
        {
            var previous = shades[i - 1].R + shades[i - 1].G + shades[i - 1].B;
            var current = shades[i].R + shades[i].G + shades[i].B;

            Assert.True(current < previous, $"Shade {i} is not darker than shade {i - 1}.");
        }
    }

    [Theory]
    [InlineData("#0078D7", 0x00, 0x78, 0xD7)]
    [InlineData("0078D7", 0x00, 0x78, 0xD7)]
    [InlineData("  #FFB900  ", 0xFF, 0xB9, 0x00)]
    public void TryParse_AcceptsTheUsualForms(string text, int r, int g, int b)
    {
        Assert.True(AccentRgb.TryParse(text, out var colour));
        Assert.Equal(new AccentRgb((byte)r, (byte)g, (byte)b), colour);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("blue")]
    public void TryParse_RejectsJunkWithoutThrowing(string? text)
    {
        Assert.False(AccentRgb.TryParse(text, out _));
    }

    [Fact]
    public void Hex_RoundTripsThroughParse()
    {
        var colour = new AccentRgb(0x2D, 0x7D, 0x9A);

        Assert.True(AccentRgb.TryParse(colour.Hex, out var parsed));
        Assert.Equal(colour, parsed);
    }

    private static bool Close(AccentRgb a, AccentRgb b) =>
        Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1;
}

/// <summary>
/// Registry behaviour, against scratch keys. The real accent is never touched, and no
/// broadcast reaches anything meaningful because the values written are not the ones
/// Windows reads.
/// </summary>
public sealed class AccentColorServiceTests : IDisposable
{
    private readonly ScratchRegistry _reg = new();
    private readonly AccentColorService _service;

    public AccentColorServiceTests()
    {
        _service = new AccentColorService(new AccentRegistryPaths(
            $@"{_reg.Root}\Accent",
            $@"{_reg.Root}\DWM",
            $@"{_reg.Root}\Personalize"));
    }

    public void Dispose() => _reg.Dispose();

    private object? Read(string subKey, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_reg.Root}\{subKey}");
        return key?.GetValue(valueName);
    }

    [Fact]
    public void GetCurrent_WithNothingStored_IsNull()
    {
        Assert.Null(_service.GetCurrent());
    }

    [Fact]
    public void ApplyThenGet_RoundTrips()
    {
        var colour = new AccentRgb(0x00, 0x78, 0xD7);

        Assert.True(_service.Apply(colour).Success);
        Assert.Equal(colour, _service.GetCurrent());
    }

    [Fact]
    public void Apply_WritesAThirtyTwoBytePalette()
    {
        _service.Apply(new AccentRgb(0x00, 0x78, 0xD7));

        var palette = Read("Accent", "AccentPalette") as byte[];

        Assert.NotNull(palette);
        Assert.Equal(AccentPalette.ByteLength, palette!.Length);
    }

    /// <summary>
    /// AccentColor is ABGR and ColorizationColor is ARGB. Same colour, opposite byte order,
    /// which is a genuine Windows quirk and exactly the kind of thing to get backwards.
    /// </summary>
    [Fact]
    public void Apply_UsesTheDifferentByteOrdersEachValueExpects()
    {
        _service.Apply(new AccentRgb(0x12, 0x34, 0x56));

        var accentColor = unchecked((uint)(int)Read("DWM", "AccentColor")!);
        var colorization = unchecked((uint)(int)Read("DWM", "ColorizationColor")!);

        Assert.Equal(0xFF563412u, accentColor);      // AABBGGRR
        Assert.Equal(0xC4123456u, colorization);     // AARRGGBB
    }

    [Fact]
    public void Apply_SetsTheStartColourToTheFirstDarkShade()
    {
        var colour = new AccentRgb(37, 89, 74);
        _service.Apply(colour);

        var expected = AccentPalette.StartShade(colour);
        var stored = unchecked((uint)(int)Read("Accent", "StartColorMenu")!);

        Assert.Equal((uint)((0xFF << 24) | (expected.B << 16) | (expected.G << 8) | expected.R), stored);
    }

    [Fact]
    public void Apply_LeavesColorPrevalenceAloneWhenNotAsked()
    {
        _service.Apply(new AccentRgb(1, 2, 3));

        Assert.Null(Read("Personalize", "ColorPrevalence"));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Apply_SetsColorPrevalenceWhenAsked(bool show, int expected)
    {
        _service.Apply(new AccentRgb(1, 2, 3), showOnSurfaces: show);

        Assert.Equal(expected, Read("Personalize", "ColorPrevalence"));
        Assert.Equal(expected, Read("DWM", "ColorPrevalence"));
    }

    [Fact]
    public void CaptureThenRestore_RoundTrips()
    {
        var original = new AccentRgb(0xC2, 0x39, 0xB3);
        _service.Apply(original, showOnSurfaces: true);

        var snapshot = _service.CaptureAssignments();

        _service.Apply(new AccentRgb(0x10, 0x89, 0x3E), showOnSurfaces: false);
        Assert.NotEqual(original, _service.GetCurrent());

        Assert.True(_service.RestoreAssignments(snapshot).Success);

        Assert.Equal(original, _service.GetCurrent());
        Assert.Equal(1, Read("Personalize", "ColorPrevalence"));
    }

    [Fact]
    public void RestoreAssignments_WithoutAnAccent_FailsClearly()
    {
        var result = _service.RestoreAssignments(new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("no accent", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Presets_AreTheWindowsSwatches()
    {
        Assert.NotEmpty(AccentColorService.Presets);

        // The default Windows blue must be offered.
        Assert.Contains(new AccentRgb(0x00, 0x78, 0xD7), AccentColorService.Presets);
    }
}
