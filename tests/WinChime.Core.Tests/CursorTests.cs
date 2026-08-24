using Microsoft.Win32;
using WinChime.Core.Cursors;

namespace WinChime.Core.Tests;

public sealed class CursorFileTests : IDisposable
{
    private readonly TestCursor _cur = new();

    public void Dispose() => _cur.Dispose();

    [Fact]
    public void Inspect_StaticCursor_IsValid()
    {
        var info = CursorFile.Inspect(_cur.WriteCur(width: 48, height: 48));

        Assert.True(info.IsValid);
        Assert.False(info.IsAnimated);
        Assert.Equal(48, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal("Static cursor", info.FormatName);
    }

    [Fact]
    public void Inspect_ZeroDimension_MeansTwoFiftySix()
    {
        // The width and height fields are single bytes, so 0 encodes 256.
        var info = CursorFile.Inspect(_cur.WriteCur(width: 0, height: 0));

        Assert.Equal(256, info.Width);
        Assert.Equal(256, info.Height);
    }

    /// <summary>
    /// The mistake worth catching. An .ico renamed to .cur has the same layout but type 1
    /// and no hotspot, so Windows silently ignores it and keeps the system cursor.
    /// </summary>
    [Fact]
    public void Inspect_IconRenamedAsCursor_IsRejectedWithAnExplanation()
    {
        var info = CursorFile.Inspect(_cur.WriteIcoPretendingToBeCur());

        Assert.False(info.IsValid);
        Assert.Contains("icon", info.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hotspot", info.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_AnimatedCursor_ReportsFrames()
    {
        var info = CursorFile.Inspect(_cur.WriteAni(frames: 12, steps: 12));

        Assert.True(info.IsValid);
        Assert.True(info.IsAnimated);
        Assert.Equal(12, info.Frames);
        Assert.Contains("frame", info.Summary);
    }

    [Fact]
    public void Inspect_AnimatedCursorWithNoFrames_Warns()
    {
        var info = CursorFile.Inspect(_cur.WriteAni(frames: 0, steps: 0));

        Assert.True(info.IsValid);
        Assert.Contains(info.Warnings, w => w.Contains("zero frames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inspect_RiffThatIsNotAnAnimatedCursor_IsRejected()
    {
        var info = CursorFile.Inspect(_cur.WriteRiffButNotAcon());

        Assert.False(info.IsValid);
        Assert.Contains("ACON", info.Error);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("toosmall")]
    [InlineData("missing")]
    public void Inspect_UnusableFiles_AreInvalidWithoutThrowing(string kind)
    {
        var path = kind switch
        {
            "garbage" => _cur.WriteGarbage(),
            "toosmall" => _cur.WriteTooSmall(),
            _ => _cur.PathFor("does-not-exist.cur"),
        };

        var info = CursorFile.Inspect(path);

        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
    }
}

/// <summary>
/// Cursor scheme behaviour, against a scratch registry subtree.
///
/// Note these do call SPI_SETCURSORS, which asks Windows to reload the cursors. Because the
/// service is pointed at a scratch key, the real Control Panel\Cursors values are untouched,
/// so Windows reloads exactly what it already had. Harmless, and it keeps the tests
/// exercising the real code path rather than a branch that skips the apply.
/// </summary>
public sealed class CursorSchemeServiceTests : IDisposable
{
    private readonly ScratchRegistry _reg = new();
    private readonly TestCursor _cur = new();
    private readonly CursorSchemeService _service;

    public CursorSchemeServiceTests()
    {
        _service = _reg.CreateCursorService();

        _reg.SeedCursor("Arrow", @"C:\WINDOWS\cursors\aero_arrow.cur");
        _reg.SeedCursor("Wait", @"C:\WINDOWS\cursors\aero_busy.ani");
        _reg.SeedCursor("IBeam", "");
        _reg.SeedActiveCursorScheme("Windows Default");
    }

    public void Dispose()
    {
        _reg.Dispose();
        _cur.Dispose();
    }

    /// <summary>A full scheme string in the canonical role order.</summary>
    private static string[] SchemeValues(params (string Role, string Path)[] assignments)
    {
        var values = new string[CursorRoles.All.Count];
        Array.Fill(values, string.Empty);

        foreach (var (role, path) in assignments)
            values[CursorRoles.IndexOf(role)] = path;

        return values;
    }

    // ------------------------------------------------------------------- roles --

    /// <summary>
    /// The order is load-bearing: a scheme is positional, so reordering this list silently
    /// assigns every cursor to the wrong role. Pinned against the values verified from the
    /// shipped Windows Aero scheme.
    /// </summary>
    [Fact]
    public void RoleOrder_MatchesTheSchemeStringLayout()
    {
        Assert.Equal(17, CursorRoles.All.Count);

        Assert.Equal("Arrow", CursorRoles.All[0].Key);
        Assert.Equal("Help", CursorRoles.All[1].Key);
        Assert.Equal("AppStarting", CursorRoles.All[2].Key);
        Assert.Equal("Wait", CursorRoles.All[3].Key);
        Assert.Equal("Crosshair", CursorRoles.All[4].Key);
        Assert.Equal("IBeam", CursorRoles.All[5].Key);
        Assert.Equal("Hand", CursorRoles.All[14].Key);
        Assert.Equal("Person", CursorRoles.All[16].Key);
    }

    /// <summary>
    /// Control Panel\Cursors also holds CursorBaseSize and Scheme Source. Treating those as
    /// assignable cursors would corrupt the mouse settings.
    /// </summary>
    [Theory]
    [InlineData("Scheme Source")]
    [InlineData("CursorBaseSize")]
    [InlineData("GestureVisualization")]
    public void NonCursorValues_AreNotTreatedAsRoles(string valueName)
    {
        Assert.Null(CursorRoles.Find(valueName));
        Assert.Contains(valueName, CursorRoles.NonCursorValues);
    }

    // ----------------------------------------------------------------- reading --

    [Fact]
    public void LoadCursors_ReturnsEveryRoleEvenWhenUnset()
    {
        var cursors = _service.LoadCursors();

        Assert.Equal(CursorRoles.All.Count, cursors.Count);
        Assert.Contains(cursors, c => c.RoleKey == "Person");
    }

    [Fact]
    public void LoadCursors_EmptyValueMeansSystemDrawnNotMissing()
    {
        var iBeam = _service.LoadCursors().Single(c => c.RoleKey == "IBeam");

        Assert.True(iBeam.IsSystemDrawn);
        Assert.False(iBeam.IsBroken);
        Assert.Equal("System", iBeam.StatusText);
    }

    /// <summary>
    /// Not "Custom": Windows records no per-role default for cursors, so an assigned file
    /// cannot be distinguished from one that shipped with the active scheme.
    /// </summary>
    [Fact]
    public void LoadCursors_AnAssignedFileReadsAsAssignedNotCustom()
    {
        var status = _service.LoadCursors().Single(c => c.RoleKey == "Arrow").StatusText;

        Assert.Equal("Assigned", status);
    }

    [Fact]
    public void LoadCursors_FlagsAFileThatIsGone()
    {
        _reg.SeedCursor("Arrow", @"C:\definitely\absent.cur");

        var arrow = _service.LoadCursors().Single(c => c.RoleKey == "Arrow");

        Assert.True(arrow.IsBroken);
        Assert.Equal("Missing", arrow.StatusText);
    }

    [Fact]
    public void GetActiveSchemeName_FallsBackWhenUnset()
    {
        _reg.SeedActiveCursorScheme("");

        Assert.Equal("Windows Default", _service.GetActiveSchemeName());
    }

    // ----------------------------------------------------------------- writing --

    [Fact]
    public void SetCursor_AssignsTheFile()
    {
        var file = _cur.WriteCur();

        Assert.True(_service.SetCursor("Arrow", file).Success);
        Assert.Equal(file, _reg.ReadCursor("Arrow"));
    }

    [Fact]
    public void SetCursor_NullMakesItSystemDrawn()
    {
        Assert.True(_service.SetCursor("Arrow", null).Success);

        Assert.Equal(string.Empty, _reg.ReadCursor("Arrow"));
        Assert.True(_service.LoadCursors().Single(c => c.RoleKey == "Arrow").IsSystemDrawn);
    }

    [Fact]
    public void SetCursor_RejectsSomethingThatIsNotARole()
    {
        Assert.False(_service.SetCursor("CursorBaseSize", @"C:\x.cur").Success);
    }

    /// <summary>
    /// Changing one cursor means the named scheme no longer describes what is applied.
    /// Windows represents that as source 0 with a blank name, and the Mouse control panel
    /// reads it back as a modified scheme.
    /// </summary>
    [Fact]
    public void SetCursor_MarksTheSchemeAsModified()
    {
        _service.SetCursor("Arrow", _cur.WriteCur());

        Assert.Equal(0, _reg.ReadCursorRawValue("Scheme Source"));
        Assert.Equal("Windows Default", _service.GetActiveSchemeName());   // blank falls back
    }

    // ----------------------------------------------------------------- schemes --

    [Fact]
    public void ListSchemes_IncludesUserAndSystemSchemes()
    {
        _reg.SeedCursorScheme("Shipped", SchemeValues(("Arrow", @"C:\a.cur")), systemScheme: true);
        _reg.SeedCursorScheme("Mine", SchemeValues(("Arrow", @"C:\b.cur")));

        var schemes = _service.ListSchemes();

        Assert.Contains(schemes, s => s.Name == "Shipped" && s.IsSystemScheme);
        Assert.Contains(schemes, s => s.Name == "Mine" && !s.IsSystemScheme);
    }

    [Fact]
    public void ReadScheme_MapsEntriesOntoRolesByPosition()
    {
        _reg.SeedCursorScheme("Positional",
            SchemeValues(("Arrow", @"C:\arrow.cur"), ("Hand", @"C:\hand.cur"), ("Person", @"C:\person.cur")));

        var values = _service.ReadScheme("Positional");

        Assert.NotNull(values);
        Assert.Equal(@"C:\arrow.cur", values![CursorRoles.IndexOf("Arrow")]);
        Assert.Equal(@"C:\hand.cur", values[CursorRoles.IndexOf("Hand")]);
        Assert.Equal(@"C:\person.cur", values[CursorRoles.IndexOf("Person")]);
    }

    /// <summary>
    /// Shipped schemes append a control panel icon path and index after the seventeen
    /// cursors. Those are display metadata and must not be read as an eighteenth cursor.
    /// </summary>
    [Fact]
    public void ReadScheme_IgnoresTheTrailingControlPanelMetadata()
    {
        var withMetadata = SchemeValues(("Arrow", @"C:\arrow.cur"))
            .Concat(new[] { @"@C:\WINDOWS\system32\main.cpl", "-1020" });

        _reg.SeedCursorScheme("WithMetadata", withMetadata, systemScheme: true);

        var values = _service.ReadScheme("WithMetadata");

        Assert.NotNull(values);
        Assert.Equal(CursorRoles.All.Count, values!.Count);
        Assert.Equal(@"C:\arrow.cur", values[0]);
    }

    [Fact]
    public void ReadScheme_ShorterThanTheRoleList_PadsRatherThanThrowing()
    {
        _reg.SeedCursorScheme("Short", new[] { @"C:\arrow.cur", @"C:\help.cur" }, systemScheme: true);

        var values = _service.ReadScheme("Short");

        Assert.NotNull(values);
        Assert.Equal(CursorRoles.All.Count, values!.Count);
        Assert.Equal(string.Empty, values[^1]);
    }

    [Fact]
    public void ApplyScheme_WritesEveryRoleAndRecordsTheName()
    {
        _reg.SeedCursorScheme("Full",
            SchemeValues(("Arrow", @"C:\arrow.cur"), ("Wait", @"C:\wait.ani")), systemScheme: true);

        Assert.True(_service.ApplyScheme("Full").Success);

        Assert.Equal(@"C:\arrow.cur", _reg.ReadCursor("Arrow"));
        Assert.Equal(@"C:\wait.ani", _reg.ReadCursor("Wait"));
        Assert.Equal(string.Empty, _reg.ReadCursor("Hand"));      // unset in the scheme
        Assert.Equal("Full", _service.GetActiveSchemeName());
        Assert.Equal(2, _reg.ReadCursorRawValue("Scheme Source"));   // 2 = system scheme
    }

    [Fact]
    public void ApplyScheme_FromAUserScheme_RecordsSourceAsUser()
    {
        _reg.SeedCursorScheme("Mine", SchemeValues(("Arrow", @"C:\arrow.cur")));

        Assert.True(_service.ApplyScheme("Mine").Success);
        Assert.Equal(1, _reg.ReadCursorRawValue("Scheme Source"));
    }

    [Fact]
    public void ApplyScheme_UnknownName_Fails()
    {
        Assert.False(_service.ApplyScheme("NoSuchScheme").Success);
    }

    [Fact]
    public void SaveThenApply_RoundTrips()
    {
        var file = _cur.WriteCur();
        _service.SetCursor("Arrow", file);

        Assert.True(_service.SaveCurrentAsScheme("Saved").Success);

        _service.SetCursor("Arrow", null);
        Assert.Equal(string.Empty, _reg.ReadCursor("Arrow"));

        Assert.True(_service.ApplyScheme("Saved").Success);
        Assert.Equal(file, _reg.ReadCursor("Arrow"));
    }

    /// <summary>
    /// A scheme is one comma-separated value, so a comma in the name would corrupt it.
    /// </summary>
    [Fact]
    public void SaveCurrentAsScheme_RefusesACommaInTheName()
    {
        Assert.False(_service.SaveCurrentAsScheme("bad,name").Success);
    }

    // --------------------------------------------------- saving supplied values --

    [Fact]
    public void SaveScheme_WritesValuesWithoutReadingTheLiveCursors()
    {
        var values = SchemeValues(("Arrow", @"C:\packed\arrow.cur"), ("Hand", @"C:\packed\hand.cur"));

        Assert.True(_service.SaveScheme("From pack", values).Success);

        // The live Arrow is still the seeded Windows one; only the scheme was written.
        Assert.Equal(@"C:\WINDOWS\cursors\aero_arrow.cur", _reg.ReadCursor("Arrow"));

        var stored = _service.ReadScheme("From pack");
        Assert.NotNull(stored);
        Assert.Equal(@"C:\packed\arrow.cur", stored![CursorRoles.IndexOf("Arrow")]);
        Assert.Equal(@"C:\packed\hand.cur", stored[CursorRoles.IndexOf("Hand")]);
    }

    /// <summary>
    /// The reason SaveScheme validates paths at all, and a case that could not arise before
    /// cursor packs: a comma is a legal Windows filename character, so "arrow,2.cur" is an
    /// ordinary file right up until it is written into a positional comma-separated string,
    /// at which point every later role shifts by one with no error anywhere.
    /// </summary>
    [Fact]
    public void SaveScheme_RefusesAPathContainingAComma()
    {
        var values = SchemeValues(("Arrow", @"C:\cursors\arrow,2.cur"));

        var result = _service.SaveScheme("Comma", values);

        Assert.False(result.Success);
        Assert.Contains("comma", result.Message);

        // And nothing was written, so a later read cannot pick up a half-formed scheme.
        Assert.Null(_service.ReadScheme("Comma"));
    }

    [Fact]
    public void SaveScheme_PadsAShortValueListToTheFullRoleCount()
    {
        Assert.True(_service.SaveScheme("Short", new[] { @"C:\only-arrow.cur" }).Success);

        var stored = _service.ReadScheme("Short");

        Assert.NotNull(stored);
        Assert.Equal(CursorRoles.All.Count, stored!.Count);
        Assert.Equal(@"C:\only-arrow.cur", stored[0]);
        Assert.All(stored.Skip(1), v => Assert.Equal(string.Empty, v));
    }

    [Fact]
    public void SaveScheme_RefusesToOverwriteAShippedScheme()
    {
        _reg.SeedCursorScheme("Windows Black", SchemeValues(("Arrow", @"C:\a.cur")), systemScheme: true);

        Assert.False(_service.SaveScheme("Windows Black", SchemeValues(("Arrow", @"C:\b.cur"))).Success);
    }

    /// <summary>
    /// One %SystemRoot% entry anywhere makes the whole scheme value expandable, which is how
    /// Windows stores its own schemes. Getting this wrong leaves the literal text in the
    /// registry and every cursor in that scheme silently missing.
    /// </summary>
    [Fact]
    public void SaveScheme_StoresAnExpandableValueWhenAnyPathUsesAVariable()
    {
        Assert.True(_service.SaveScheme(
            "Mixed", SchemeValues(("Arrow", @"%SystemRoot%\Cursors\aero_arrow.cur"))).Success);

        Assert.True(_service.SaveScheme(
            "Plain", SchemeValues(("Arrow", @"C:\arrow.cur"))).Success);

        Assert.Equal(RegistryValueKind.ExpandString, _reg.ReadCursorSchemeKind("Mixed"));
        Assert.Equal(RegistryValueKind.String, _reg.ReadCursorSchemeKind("Plain"));
    }

    [Fact]
    public void SaveCurrentAsScheme_RefusesToOverwriteAShippedScheme()
    {
        _reg.SeedCursorScheme("Windows Black", SchemeValues(("Arrow", @"C:\a.cur")), systemScheme: true);

        var result = _service.SaveCurrentAsScheme("Windows Black");

        Assert.False(result.Success);
        Assert.Contains("ships with Windows", result.Message);
    }

    [Fact]
    public void DeleteScheme_RemovesAUserScheme()
    {
        _reg.SeedCursorScheme("Doomed", SchemeValues(("Arrow", @"C:\a.cur")));

        Assert.True(_service.DeleteScheme("Doomed").Success);
        Assert.DoesNotContain(_service.ListSchemes(), s => s.Name == "Doomed");
    }

    [Fact]
    public void DeleteScheme_RefusesAShippedScheme()
    {
        _reg.SeedCursorScheme("Windows Black", SchemeValues(("Arrow", @"C:\a.cur")), systemScheme: true);

        Assert.False(_service.DeleteScheme("Windows Black").Success);
    }

    // ------------------------------------------------------ snapshot / restore --

    [Fact]
    public void CaptureThenRestore_RoundTrips()
    {
        var snapshot = _service.CaptureAssignments();

        _service.SetCursor("Arrow", _cur.WriteCur("different.cur"));
        _service.SetCursor("Wait", null);

        Assert.True(_service.RestoreAssignments(snapshot).Success);

        Assert.Equal(@"C:\WINDOWS\cursors\aero_arrow.cur", _reg.ReadCursor("Arrow"));
        Assert.Equal(@"C:\WINDOWS\cursors\aero_busy.ani", _reg.ReadCursor("Wait"));
    }

    [Fact]
    public void RestoreAssignments_IgnoresKeysThatAreNotRoles()
    {
        var result = _service.RestoreAssignments(new Dictionary<string, string>
        {
            ["Arrow"] = @"C:\arrow.cur",
            ["CursorBaseSize"] = "48",
        });

        Assert.True(result.Success);
        Assert.Equal(@"C:\arrow.cur", _reg.ReadCursor("Arrow"));
        Assert.Null(_reg.ReadCursor("CursorBaseSize"));
    }
}
