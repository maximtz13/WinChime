using WinChime.Core.Cli;
using WinChime.Core.Cursors;
using WinChime.Core.Personalization;
using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

/// <summary>
/// The CLI is the part of the app CI can actually exercise end to end, since it has no
/// window. Everything here runs against a scratch registry subtree and a disposable backup
/// folder, so a test run never touches the real sound settings.
/// </summary>
public sealed class CliRunnerTests : IDisposable
{
    private readonly ScratchRegistry _reg = new();
    private readonly TestWav _wav = new();
    private readonly TestCursor _cur = new();
    private readonly StringWriter _out = new();
    private readonly CliRunner _cli;

    public CliRunnerTests()
    {
        _cli = new CliRunner(_out, _reg.Root, backupRoot: null);

        _reg.SeedApp(".Default", "Windows");
        _reg.SeedApp("Explorer", "File Explorer");
        _reg.SeedEvent(".Default", "SystemHand", @"C:\a.wav", @"C:\a.wav");
        _reg.SeedEvent(".Default", "SystemAsterisk", @"C:\b.wav", @"C:\b.wav");
        _reg.SeedEvent("Explorer", "EmptyRecycleBin", @"C:\c.wav", @"C:\c.wav");
        _reg.SeedLabel("SystemHand", "Critical Stop");
    }

    public void Dispose()
    {
        _reg.Dispose();
        _wav.Dispose();
        _cur.Dispose();
        _out.Dispose();
    }

    private string Output => _out.ToString();

    /// <summary>A runner whose backups go somewhere disposable.</summary>
    private CliRunner WithBackups(out string folder)
    {
        folder = _wav.PathFor("backups");
        return new CliRunner(_out, _reg.Root, folder);
    }

    // ------------------------------------------------------------- invocation --

    [Theory]
    [InlineData(new string[0], false)]                       // no args: open the GUI
    [InlineData(new[] { "--list" }, true)]
    [InlineData(new[] { "--help" }, true)]
    [InlineData(new[] { "--play-chime", "x.wav" }, false)]   // internal, handled by the GUI
    [InlineData(new[] { "--elevated-op", "x.json" }, false)] // internal
    [InlineData(new[] { "somefile.wav" }, false)]            // not a switch
    public void IsCliInvocation_DistinguishesCliArgsFromGuiAndInternalSwitches(string[] args, bool expected)
    {
        Assert.Equal(expected, CliRunner.IsCliInvocation(args));
    }

    [Fact]
    public void NoArguments_PrintsHelp()
    {
        Assert.Equal(CliRunner.ExitOk, _cli.Run(Array.Empty<string>()));
        Assert.Contains("--list", Output);
    }

    [Fact]
    public void Help_ListsTheCommandsAndExitsZero()
    {
        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--help" }));

        Assert.Contains("--set", Output);
        Assert.Contains("--apply-pack", Output);
        Assert.Contains("--backup", Output);
    }

    /// <summary>Internal switches are implementation detail and must not be advertised.</summary>
    [Fact]
    public void Help_DoesNotAdvertiseInternalSwitches()
    {
        _cli.Run(new[] { "--help" });

        Assert.DoesNotContain("--elevated-op", Output);
        Assert.DoesNotContain("--play-chime", Output);
    }

    [Fact]
    public void UnknownCommand_ReturnsUsageExitCode()
    {
        Assert.Equal(CliRunner.ExitUsage, _cli.Run(new[] { "--nonsense" }));
        Assert.Contains("--help", Output);
    }

    [Fact]
    public void Version_PrintsSomething()
    {
        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--version" }));
        Assert.Contains("WinChime", Output);
    }

    // ------------------------------------------------------------------ list --

    [Fact]
    public void List_ShowsEverySeededEvent()
    {
        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--list" }));

        Assert.Contains(@".Default\SystemHand", Output);
        Assert.Contains(@"Explorer\EmptyRecycleBin", Output);
        Assert.Contains("3 event(s)", Output);
    }

    [Fact]
    public void List_FiltersOnEventAndAppName()
    {
        _cli.Run(new[] { "--list", "recycle" });

        Assert.Contains("EmptyRecycleBin", Output);
        Assert.DoesNotContain("SystemHand", Output);
    }

    [Fact]
    public void List_WithNoMatches_SaysSoRatherThanPrintingNothing()
    {
        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--list", "zzzznope" }));
        Assert.Contains("No sound events match", Output);
    }

    [Fact]
    public void List_MarksBrokenAssignments()
    {
        _cli.Run(new[] { "--list", "SystemHand" });

        // C:\a.wav does not exist, so it should carry the missing-file marker.
        Assert.Contains("!", Output);
    }

    // ------------------------------------------------------------------- get --

    [Fact]
    public void Get_ShowsTheEventDetail()
    {
        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--get", @".Default\SystemHand" }));

        Assert.Contains(@"C:\a.wav", Output);
        Assert.Contains("Critical Stop", Output);
    }

    /// <summary>
    /// Non-ASCII in CLI output is at the mercy of the console code page, so the detail view
    /// must not use the bullet that DisplayLabel contains.
    /// </summary>
    [Fact]
    public void Get_OutputIsPureAscii()
    {
        _cli.Run(new[] { "--get", @".Default\SystemHand" });

        Assert.All(Output, c => Assert.True(c < 128, $"Non-ASCII character in CLI output: {c}"));
    }

    [Fact]
    public void Get_MalformedName_ExplainsTheExpectedFormat()
    {
        Assert.Equal(CliRunner.ExitFailed, _cli.Run(new[] { "--get", "NoBackslashHere" }));
        Assert.Contains(@"AppKey\EventKey", Output);
    }

    /// <summary>
    /// A bare "not found" makes the CLI hostile, because event keys are not memorable.
    /// Contains alone misses transpositions, which is what a typo usually is.
    /// </summary>
    [Fact]
    public void Get_TypoInEventName_SuggestsTheRealOne()
    {
        Assert.Equal(CliRunner.ExitFailed, _cli.Run(new[] { "--get", @".Default\SystemHnad" }));
        Assert.Contains("SystemHand", Output);
    }

    [Fact]
    public void Get_CompletelyUnknownName_FallsBackToPointingAtList()
    {
        _cli.Run(new[] { "--get", @".Default\Zzzzqqq" });
        Assert.Contains("--list", Output);
    }

    // ------------------------------------------------------------------- set --

    [Fact]
    public void Set_AssignsAPcmWav()
    {
        var wav = _wav.WritePcm("chime.wav");

        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--set", @".Default\SystemHand", wav }));
        Assert.Equal(Path.GetFullPath(wav), _reg.ReadCurrent(".Default", "SystemHand"));
    }

    /// <summary>
    /// The GUI offers to convert. A script has nobody to ask, so the CLI refuses rather than
    /// assigning a file Windows would accept and then play silently.
    /// </summary>
    [Fact]
    public void Set_NonPcmFile_IsRefusedRatherThanSilentlyAssigned()
    {
        var notPcm = _wav.WriteNonPcm();
        var before = _reg.ReadCurrent(".Default", "SystemHand");

        Assert.Equal(CliRunner.ExitFailed, _cli.Run(new[] { "--set", @".Default\SystemHand", notPcm }));

        Assert.Contains("not uncompressed PCM", Output);
        Assert.Equal(before, _reg.ReadCurrent(".Default", "SystemHand"));
    }

    [Fact]
    public void Set_MissingFile_IsRefused()
    {
        Assert.Equal(CliRunner.ExitFailed,
            _cli.Run(new[] { "--set", @".Default\SystemHand", _wav.PathFor("absent.wav") }));

        Assert.Contains("not found", Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_WithoutEnoughArguments_ReturnsUsage()
    {
        Assert.Equal(CliRunner.ExitUsage, _cli.Run(new[] { "--set", @".Default\SystemHand" }));
    }

    [Fact]
    public void Silence_ClearsTheAssignment()
    {
        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--silence", @".Default\SystemHand" }));
        Assert.Equal(string.Empty, _reg.ReadCurrent(".Default", "SystemHand"));
    }

    [Fact]
    public void RestoreDefault_PutsBackTheShippedValue()
    {
        _cli.Run(new[] { "--silence", @".Default\SystemHand" });

        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--restore-default", @".Default\SystemHand" }));
        Assert.Equal(@"C:\a.wav", _reg.ReadCurrent(".Default", "SystemHand"));
    }

    // ---------------------------------------------------------------- schemes --

    [Fact]
    public void ListSchemes_MarksTheActiveOne()
    {
        _reg.SeedSchemeName("Custom", "Custom");

        Assert.Equal(CliRunner.ExitOk, _cli.Run(new[] { "--list-schemes" }));
        Assert.Contains("Custom", Output);
    }

    [Fact]
    public void ApplyScheme_UnknownName_FailsWithAPointer()
    {
        Assert.Equal(CliRunner.ExitFailed, _cli.Run(new[] { "--apply-scheme", "NoSuchScheme" }));
        Assert.Contains("--list-schemes", Output);
    }

    [Fact]
    public void ApplyScheme_AppliesAndTakesABackupFirst()
    {
        var cli = WithBackups(out var folder);

        _reg.SeedSchemeName("Quiet", "Quiet");
        _reg.SeedSchemeValue(".Default", "SystemHand", "Quiet", "");

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--apply-scheme", "Quiet" }));

        Assert.Equal(string.Empty, _reg.ReadCurrent(".Default", "SystemHand"));
        Assert.True(Directory.Exists(folder) && Directory.EnumerateDirectories(folder).Any(),
            "Applying a scheme unattended should still leave a backup behind.");
    }

    // ------------------------------------------------------------------ packs --

    [Fact]
    public void ExportThenApplyPack_RoundTrips()
    {
        var cli = WithBackups(out _);
        var sound = _wav.WritePcm("packed.wav");

        _cli.Run(new[] { "--set", @".Default\SystemHand", sound });

        var pack = _wav.PathFor("test" + SoundPackService.PackExtension);
        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--export-pack", pack, "Test Pack" }));
        Assert.True(File.Exists(pack));

        // Change it, then prove applying the pack puts it back.
        _cli.Run(new[] { "--silence", @".Default\SystemHand" });
        Assert.Equal(string.Empty, _reg.ReadCurrent(".Default", "SystemHand"));

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--apply-pack", pack }));

        var restored = _reg.ReadCurrent(".Default", "SystemHand");
        Assert.False(string.IsNullOrEmpty(restored));
        Assert.True(File.Exists(restored));
    }

    [Fact]
    public void ApplyPack_MissingFile_FailsCleanly()
    {
        Assert.Equal(CliRunner.ExitFailed,
            _cli.Run(new[] { "--apply-pack", _wav.PathFor("nope.winchimepack") }));
    }

    [Fact]
    public void ExportPack_WithoutAPath_ReturnsUsage()
    {
        Assert.Equal(CliRunner.ExitUsage, _cli.Run(new[] { "--export-pack" }));
    }

    // ---------------------------------------------------------------- cursors --

    /// <summary>A runner whose cursor service points entirely at scratch keys.</summary>
    private CliRunner WithCursors()
    {
        _reg.SeedCursor("Arrow", @"C:\WINDOWS\cursors\aero_arrow.cur");
        _reg.SeedCursor("IBeam", "");
        _reg.SeedActiveCursorScheme("Windows Default");

        return new CliRunner(_out, _reg.Root, _wav.PathFor("backups"), _reg.CreateCursorService());
    }

    [Fact]
    public void ListCursors_ShowsEveryRole()
    {
        Assert.Equal(CliRunner.ExitOk, WithCursors().Run(new[] { "--list-cursors" }));

        Assert.Contains("Arrow", Output);
        Assert.Contains("Person", Output);
        Assert.Contains("17 cursor(s)", Output);
    }

    [Fact]
    public void ListCursors_FiltersOnRoleAndDisplayName()
    {
        WithCursors().Run(new[] { "--list-cursors", "resize" });

        Assert.Contains("SizeNS", Output);
        Assert.DoesNotContain("Person", Output);
    }

    [Fact]
    public void GetCursor_ShowsTheDetail()
    {
        Assert.Equal(CliRunner.ExitOk, WithCursors().Run(new[] { "--get-cursor", "Arrow" }));

        Assert.Contains("aero_arrow.cur", Output);
        Assert.Contains("Normal Select", Output);
    }

    [Fact]
    public void GetCursor_SystemDrawnRole_SaysSoRatherThanLookingBroken()
    {
        WithCursors().Run(new[] { "--get-cursor", "IBeam" });

        Assert.Contains("drawn by Windows", Output);
    }

    [Fact]
    public void GetCursor_TypoInRoleName_Suggests()
    {
        Assert.Equal(CliRunner.ExitFailed, WithCursors().Run(new[] { "--get-cursor", "Arow" }));
        Assert.Contains("Arrow", Output);
    }

    [Fact]
    public void GetCursor_CompletelyUnknownRole_PointsAtTheList()
    {
        WithCursors().Run(new[] { "--get-cursor", "Zzzqqq" });
        Assert.Contains("--list-cursors", Output);
    }

    [Fact]
    public void SetCursor_AssignsAValidCursorFile()
    {
        var cursor = _cur.WriteCur("pointer.cur");

        Assert.Equal(CliRunner.ExitOk, WithCursors().Run(new[] { "--set-cursor", "Arrow", cursor }));
        Assert.Equal(Path.GetFullPath(cursor), _reg.ReadCursor("Arrow"));
    }

    /// <summary>
    /// Unlike audio there is nothing to convert, so an unusable file is simply refused
    /// rather than assigned and left to fail silently.
    /// </summary>
    [Fact]
    public void SetCursor_IconRenamedAsCursor_IsRefused()
    {
        var fake = _cur.WriteIcoPretendingToBeCur();

        // WithCursors does the seeding, so the "before" value has to be read after it.
        var cli = WithCursors();
        var before = _reg.ReadCursor("Arrow");

        Assert.Equal(CliRunner.ExitFailed, cli.Run(new[] { "--set-cursor", "Arrow", fake }));

        Assert.Contains("icon", Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, _reg.ReadCursor("Arrow"));
    }

    [Fact]
    public void SetCursor_MissingFile_IsRefused()
    {
        Assert.Equal(CliRunner.ExitFailed,
            WithCursors().Run(new[] { "--set-cursor", "Arrow", _cur.PathFor("absent.cur") }));
    }

    [Fact]
    public void SystemCursor_ClearsTheAssignment()
    {
        Assert.Equal(CliRunner.ExitOk, WithCursors().Run(new[] { "--system-cursor", "Arrow" }));
        Assert.Equal(string.Empty, _reg.ReadCursor("Arrow"));
    }

    [Fact]
    public void ListCursorSchemes_MarksTheActiveScheme()
    {
        var cli = WithCursors();
        _reg.SeedCursorScheme("Windows Black", new[] { @"C:\a.cur" }, systemScheme: true);

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--list-cursor-schemes" }));

        Assert.Contains("Windows Black", Output);
        Assert.Contains("Active:", Output);
    }

    [Fact]
    public void ApplyCursorScheme_UnknownName_PointsAtTheSchemeList()
    {
        Assert.Equal(CliRunner.ExitFailed,
            WithCursors().Run(new[] { "--apply-cursor-scheme", "NoSuchScheme" }));

        Assert.Contains("--list-cursor-schemes", Output);
    }

    [Fact]
    public void ApplyCursorScheme_AppliesEveryRolePositionally()
    {
        var cli = WithCursors();

        var values = new string[CursorRoles.All.Count];
        Array.Fill(values, string.Empty);
        values[CursorRoles.IndexOf("Arrow")] = @"C:\scheme-arrow.cur";
        values[CursorRoles.IndexOf("Hand")] = @"C:\scheme-hand.cur";

        _reg.SeedCursorScheme("Test Scheme", values, systemScheme: true);

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--apply-cursor-scheme", "Test Scheme" }));

        Assert.Equal(@"C:\scheme-arrow.cur", _reg.ReadCursor("Arrow"));
        Assert.Equal(@"C:\scheme-hand.cur", _reg.ReadCursor("Hand"));
    }

    [Fact]
    public void CursorCommands_WithoutEnoughArguments_ReturnUsage()
    {
        var cli = WithCursors();

        Assert.Equal(CliRunner.ExitUsage, cli.Run(new[] { "--get-cursor" }));
        Assert.Equal(CliRunner.ExitUsage, cli.Run(new[] { "--set-cursor", "Arrow" }));
        Assert.Equal(CliRunner.ExitUsage, cli.Run(new[] { "--apply-cursor-scheme" }));
    }

    [Fact]
    public void Help_ListsTheCursorCommands()
    {
        _cli.Run(new[] { "--help" });

        Assert.Contains("--list-cursors", Output);
        Assert.Contains("--set-cursor", Output);
        Assert.Contains("--apply-cursor-scheme", Output);
        Assert.Contains("--export-cursor-pack", Output);
        Assert.Contains("--apply-cursor-pack", Output);
    }

    // ----------------------------------------------------------- cursor packs --

    [Fact]
    public void ExportThenApplyCursorPack_RoundTrips()
    {
        var cli = WithCursors();
        var cursor = _cur.WriteCur("packed.cur");

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--set-cursor", "Arrow", cursor }));

        var pack = _wav.PathFor("test" + CursorPackService.PackExtension);
        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--export-cursor-pack", pack, "Test Cursors" }));
        Assert.True(File.Exists(pack));

        // Change it, then prove applying the pack puts a working cursor back.
        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--system-cursor", "Arrow" }));
        Assert.Equal(string.Empty, _reg.ReadCursor("Arrow"));

        try
        {
            Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--apply-cursor-pack", pack }));

            var restored = _reg.ReadCursor("Arrow");
            Assert.False(string.IsNullOrEmpty(restored));
            Assert.True(File.Exists(restored));

            // It points at the extracted copy, not at the original that was packed.
            Assert.NotEqual(cursor, restored);
        }
        finally
        {
            CleanInstalledPack("Test Cursors");
        }
    }

    /// <summary>
    /// Installing registers the pack as a named scheme rather than only rewriting the live
    /// values, so it can be switched back to after trying something else.
    /// </summary>
    [Fact]
    public void ApplyCursorPack_RegistersAReusableScheme()
    {
        var cli = WithCursors();

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--set-cursor", "Arrow", _cur.WriteCur("reusable.cur") }));

        var pack = _wav.PathFor("reusable" + CursorPackService.PackExtension);
        cli.Run(new[] { "--export-cursor-pack", pack, "Reusable" });

        try
        {
            Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--apply-cursor-pack", pack }));

            _out.GetStringBuilder().Clear();
            Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--list-cursor-schemes" }));

            Assert.Contains("Reusable", Output);
        }
        finally
        {
            CleanInstalledPack("Reusable");
        }
    }

    [Fact]
    public void ApplyCursorPack_MissingFile_FailsCleanly()
    {
        Assert.Equal(CliRunner.ExitFailed,
            WithCursors().Run(new[] { "--apply-cursor-pack", _wav.PathFor("nope" + CursorPackService.PackExtension) }));
    }

    [Fact]
    public void ExportCursorPack_WithoutAPath_ReturnsUsage()
    {
        Assert.Equal(CliRunner.ExitUsage, WithCursors().Run(new[] { "--export-cursor-pack" }));
        Assert.Contains("Usage:", Output);
    }

    /// <summary>
    /// Installing writes into LocalApplicationData, which is outside the scratch registry the
    /// rest of the suite lives in. Removing it keeps a test run from accumulating folders.
    /// </summary>
    private static void CleanInstalledPack(string name)
    {
        try { Directory.Delete(Path.Combine(CursorPackService.PacksFolder, name), recursive: true); }
        catch { /* best effort */ }
    }

    // ----------------------------------------------------------------- accent --

    private CliRunner WithAccent() => new(
        _out, _reg.Root, _wav.PathFor("backups"), null,
        new AccentColorService(new AccentRegistryPaths(
            $@"{_reg.Root}\Accent", $@"{_reg.Root}\DWM", $@"{_reg.Root}\Personalize")));

    [Fact]
    public void GetAccent_WithNothingSet_SaysSoRatherThanFailing()
    {
        Assert.Equal(CliRunner.ExitOk, WithAccent().Run(new[] { "--get-accent" }));
        Assert.Contains("not recorded", Output);
    }

    [Fact]
    public void SetThenGetAccent_RoundTrips()
    {
        var cli = WithAccent();

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--set-accent", "#0078D7" }));
        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--get-accent" }));

        Assert.Contains("#0078D7", Output);
    }

    [Fact]
    public void GetAccent_ListsTheShadeLadder()
    {
        var cli = WithAccent();
        cli.Run(new[] { "--set-accent", "#25594A" });
        _out.GetStringBuilder().Clear();

        cli.Run(new[] { "--get-accent" });

        // Seven shades plus the accent line itself.
        Assert.Contains("Shades", Output);
        Assert.Contains("#3F977", Output);   // the lightest step, allowing the last digit to vary
    }

    [Theory]
    [InlineData("notacolour")]
    [InlineData("#12345")]
    [InlineData("")]
    public void SetAccent_RejectsJunkWithUsageExit(string value)
    {
        Assert.Equal(CliRunner.ExitUsage, WithAccent().Run(new[] { "--set-accent", value }));
    }

    [Fact]
    public void SetAccent_WithoutAnArgument_ReturnsUsage()
    {
        Assert.Equal(CliRunner.ExitUsage, WithAccent().Run(new[] { "--set-accent" }));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("true")]
    [InlineData("0")]
    public void SetAccent_AcceptsTheUsualBooleanSpellings(string flag)
    {
        Assert.Equal(CliRunner.ExitOk, WithAccent().Run(new[] { "--set-accent", "#0078D7", flag }));
    }

    [Fact]
    public void SetAccent_RejectsAnUnrecognisedSecondArgument()
    {
        var result = WithAccent().Run(new[] { "--set-accent", "#0078D7", "maybe" });

        Assert.Equal(CliRunner.ExitUsage, result);
        Assert.Contains("on or off", Output);
    }

    [Fact]
    public void ListAccentPresets_ShowsTheWindowsSwatches()
    {
        Assert.Equal(CliRunner.ExitOk, WithAccent().Run(new[] { "--list-accent-presets" }));

        Assert.Contains("#0078D7", Output);
        Assert.Contains("preset(s)", Output);
    }

    [Fact]
    public void Help_ListsTheAccentCommands()
    {
        _cli.Run(new[] { "--help" });

        Assert.Contains("--get-accent", Output);
        Assert.Contains("--set-accent", Output);
    }

    // ----------------------------------------------------------------- backup --

    [Fact]
    public void Backup_WritesASnapshotAndReportsTheId()
    {
        var cli = WithBackups(out var folder);

        Assert.Equal(CliRunner.ExitOk, cli.Run(new[] { "--backup", "cli test" }));

        Assert.True(Directory.Exists(folder));
        Assert.Single(Directory.EnumerateDirectories(folder));
        Assert.Contains("assignment", Output);
    }
}
