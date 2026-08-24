using WinChime.Core.Cli;
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
