using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinChime.Core.Cursors;
using WinChime.Core.Model;

namespace WinChime.Core.Tests;

public sealed class CursorPackServiceTests : IDisposable
{
    private readonly TestCursor _cursors = new();

    public void Dispose() => _cursors.Dispose();

    private string PackPath(string name = "pack" + CursorPackService.PackExtension) => _cursors.PathFor(name);

    private string InstallFolder(string name = "installed")
    {
        var folder = _cursors.PathFor(name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static CursorSchemeExport SchemeWith(params (string Role, string Path)[] assignments)
    {
        var scheme = new CursorSchemeExport { Name = "Test" };

        foreach (var (role, path) in assignments) scheme.Assignments[role] = path;

        return scheme;
    }

    // ------------------------------------------------------------------ creating --

    [Fact]
    public void Create_BundlesUserSuppliedCursors()
    {
        var arrow = _cursors.WriteCur("arrow.cur");

        var result = CursorPackService.Create(PackPath(), SchemeWith(("Arrow", arrow)));

        Assert.True(result.Success, result.Message);

        using var archive = ZipFile.OpenRead(PackPath());
        Assert.NotNull(archive.GetEntry(CursorPackService.ManifestEntryName));
        Assert.NotNull(archive.GetEntry($"{CursorPackService.CursorFolderName}/arrow.cur"));
    }

    /// <summary>
    /// Windows' own cursors are on every install. Bundling them would bloat the pack and
    /// redistribute Microsoft's files for no benefit, so they stay as %SystemRoot% strings.
    /// </summary>
    [Fact]
    public void Create_ReferencesWindowsCursorsInsteadOfBundlingThem()
    {
        const string shipped = @"%SystemRoot%\Cursors\aero_arrow.cur";

        Assert.True(CursorPackService.Create(PackPath(), SchemeWith(("Arrow", shipped))).Success);

        using var archive = ZipFile.OpenRead(PackPath());
        Assert.Empty(archive.Entries.Where(e => e.FullName.StartsWith(CursorPackService.CursorFolderName)));
        Assert.Equal(shipped, ReadManifest(archive).Assignments["Arrow"]);
    }

    /// <summary>
    /// Cursor sets routinely point several roles at one file: the two diagonal resize cursors
    /// are often the same artwork, as are the vertical and horizontal ones.
    /// </summary>
    /// <summary>
    /// Caught by exporting a pack from the real registry rather than from a fixture. Windows
    /// stores the cursor values fully expanded — reading Control Panel\Cursors gives
    /// C:\WINDOWS\cursors\aero_arrow.cur, not the %SystemRoot% form that sound assignments
    /// arrive in — so a pack that stored what it read would work on the machine that made it
    /// and break on any machine whose Windows is not on C:.
    /// </summary>
    [Fact]
    public void Create_CollapsesAnExpandedWindowsPathBackToSystemRoot()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var expanded = Path.Combine(windows, "cursors", "aero_arrow.cur");

        Assert.True(CursorPackService.Create(PackPath(), SchemeWith(("Arrow", expanded))).Success);

        using var archive = ZipFile.OpenRead(PackPath());
        var stored = ReadManifest(archive).Assignments["Arrow"];

        Assert.StartsWith("%SystemRoot%", stored, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"cursors\aero_arrow.cur", stored, StringComparison.OrdinalIgnoreCase);

        // And it still resolves back to where it came from.
        Assert.Equal(
            Path.GetFullPath(expanded),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(stored)));
    }

    [Fact]
    public void Create_StoresASharedFileOnlyOnce()
    {
        var shared = _cursors.WriteCur("resize.cur");

        Assert.True(CursorPackService.Create(
            PackPath(), SchemeWith(("SizeNS", shared), ("SizeWE", shared), ("SizeAll", shared))).Success);

        using var archive = ZipFile.OpenRead(PackPath());

        var bundled = archive.Entries
            .Where(e => e.FullName.StartsWith(CursorPackService.CursorFolderName))
            .ToList();

        Assert.Single(bundled);

        var manifest = ReadManifest(archive);
        Assert.Equal(manifest.Assignments["SizeNS"], manifest.Assignments["SizeWE"]);
        Assert.Equal(manifest.Assignments["SizeNS"], manifest.Assignments["SizeAll"]);
    }

    [Fact]
    public void Create_LeavesOutAFileThatNoLongerExists()
    {
        var missing = _cursors.PathFor("never-written.cur");

        var result = CursorPackService.Create(PackPath(), SchemeWith(("Arrow", missing)));

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("no longer exists"));

        using var archive = ZipFile.OpenRead(PackPath());
        Assert.Equal(string.Empty, ReadManifest(archive).Assignments["Arrow"]);
    }

    /// <summary>
    /// An .ico renamed to .cur has no hotspot and Windows silently ignores it. Packing one
    /// would produce a pack that installs cleanly and then changes nothing on screen, which is
    /// the most confusing possible outcome.
    /// </summary>
    [Fact]
    public void Create_LeavesOutAFileThatIsNotAUsableCursor()
    {
        var fake = _cursors.WriteIcoPretendingToBeCur();

        var result = CursorPackService.Create(PackPath(), SchemeWith(("Arrow", fake)));

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("not a usable cursor"));

        using var archive = ZipFile.OpenRead(PackPath());
        Assert.Empty(archive.Entries.Where(e => e.FullName.StartsWith(CursorPackService.CursorFolderName)));
    }

    [Fact]
    public void Create_LeavesSystemDrawnRolesEmpty()
    {
        Assert.True(CursorPackService.Create(PackPath(), SchemeWith(("Arrow", ""))).Success);

        using var archive = ZipFile.OpenRead(PackPath());
        var manifest = ReadManifest(archive);

        // Every role is present, and all of them are empty.
        Assert.Equal(CursorRoles.All.Count, manifest.Assignments.Count);
        Assert.All(manifest.Assignments.Values, v => Assert.Equal(string.Empty, v));
    }

    // --------------------------------------------------------------------- commas --

    /// <summary>
    /// The load-bearing test for this whole feature. A cursor scheme is one comma-separated
    /// string where meaning comes from position, and a comma is a perfectly legal Windows
    /// filename character. An entry called "arrow,2.cur" would split into two, shifting every
    /// later role by one, with no error anywhere.
    /// </summary>
    [Fact]
    public void Create_StripsCommasFromBundledFileNames()
    {
        var comma = _cursors.WriteCur("arrow,2.cur");

        var result = CursorPackService.Create(PackPath(), SchemeWith(("Arrow", comma)));

        Assert.True(result.Success, result.Message);

        using var archive = ZipFile.OpenRead(PackPath());

        Assert.All(archive.Entries, e => Assert.DoesNotContain(",", e.FullName));
        Assert.DoesNotContain(",", ReadManifest(archive).Assignments["Arrow"]);
    }

    [Fact]
    public void Install_StripsCommasFromExtractedFileNames()
    {
        var packPath = PackPath("comma" + CursorPackService.PackExtension);

        var manifest = new CursorSchemeExport { Name = "Comma" };
        manifest.Assignments["Arrow"] = $"{CursorPackService.CursorFolderName}/point,er.cur";

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, CursorPackService.ManifestEntryName, JsonSerializer.Serialize(manifest));
            WriteEntry(archive, $"{CursorPackService.CursorFolderName}/point,er.cur", "not really a cursor");
        }

        var (scheme, result) = CursorPackService.Install(packPath, InstallFolder("comma-target"));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(scheme);

        // The manifest still refers to the original name, so the rewrite has to survive the
        // rename; the resulting path is what goes into the scheme string.
        Assert.DoesNotContain(",", scheme!.Assignments["Arrow"]);
        Assert.True(File.Exists(scheme.Assignments["Arrow"]));
    }

    [Fact]
    public void Install_RefusesAFolderPathContainingAComma()
    {
        var arrow = _cursors.WriteCur();
        Assert.True(CursorPackService.Create(PackPath(), SchemeWith(("Arrow", arrow))).Success);

        var folder = _cursors.PathFor("has,comma");
        Directory.CreateDirectory(folder);

        var (scheme, result) = CursorPackService.Install(PackPath(), folder);

        Assert.False(result.Success);
        Assert.Null(scheme);
        Assert.Contains("comma", result.Message);
    }

    [Fact]
    public void Install_SanitisesACommaOutOfThePackNameUsedForTheFolder()
    {
        var arrow = _cursors.WriteCur();

        var scheme = SchemeWith(("Arrow", arrow));
        scheme.Name = "Dark, Round";

        Assert.True(CursorPackService.Create(PackPath(), scheme).Success);

        // No explicit folder: the default is derived from the pack name.
        var (installed, result) = CursorPackService.Install(PackPath());

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(",", result.Path);
        Assert.DoesNotContain(",", installed!.Assignments["Arrow"]);

        TryCleanup(result.Path);
    }

    // ---------------------------------------------------------------- installing --

    [Fact]
    public void Install_RewritesBundledReferencesToRealPaths()
    {
        var arrow = _cursors.WriteCur("arrow.cur");
        Assert.True(CursorPackService.Create(PackPath(), SchemeWith(("Arrow", arrow))).Success);

        var folder = InstallFolder();
        var (scheme, result) = CursorPackService.Install(PackPath(), folder);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(scheme);

        var resolved = scheme!.Assignments["Arrow"];
        Assert.True(Path.IsPathRooted(resolved));
        Assert.True(File.Exists(resolved));
        Assert.StartsWith(Path.GetFullPath(folder), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_KeepsWindowsCursorsUnexpanded()
    {
        const string shipped = @"%SystemRoot%\Cursors\aero_arrow.cur";

        Assert.True(CursorPackService.Create(PackPath(), SchemeWith(("Arrow", shipped))).Success);

        var (scheme, _) = CursorPackService.Install(PackPath(), InstallFolder());

        Assert.Equal(shipped, scheme!.Assignments["Arrow"]);
    }

    /// <summary>
    /// Packs are files people receive from other people, so this is a real attack surface
    /// rather than a theoretical one.
    /// </summary>
    [Fact]
    public void Install_RefusesEntriesThatEscapeTheExtractionFolder()
    {
        var packPath = PackPath("evil" + CursorPackService.PackExtension);

        var manifest = new CursorSchemeExport { Name = "Evil" };
        manifest.Assignments["Arrow"] = $"{CursorPackService.CursorFolderName}/fine.cur";

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, CursorPackService.ManifestEntryName, JsonSerializer.Serialize(manifest));
            WriteEntry(archive, $"{CursorPackService.CursorFolderName}/fine.cur", "cursor");
            WriteEntry(archive, "../../escaped.txt", "you should never see this on disk");
        }

        var folder = InstallFolder("evil-target");
        var (_, result) = CursorPackService.Install(packPath, folder);

        Assert.True(result.Success);   // the pack still installs, the bad entry is dropped
        Assert.Contains(result.Warnings, w => w.Contains("escapes", StringComparison.OrdinalIgnoreCase));

        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(folder, "..", "..", "escaped.txt"))));
    }

    [Fact]
    public void Install_RefusesTooManyEntries()
    {
        var packPath = PackPath("many" + CursorPackService.PackExtension);

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, CursorPackService.ManifestEntryName, "{}");

            for (var i = 0; i < 250; i++)
                WriteEntry(archive, $"{CursorPackService.CursorFolderName}/c{i}.cur", "x");
        }

        var (scheme, result) = CursorPackService.Install(packPath, InstallFolder("many-target"));

        Assert.False(result.Success);
        Assert.Null(scheme);
        Assert.Contains("limit", result.Message);
    }

    [Fact]
    public void Install_RefusesAPackWithNoManifest()
    {
        var packPath = PackPath("bare" + CursorPackService.PackExtension);

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "readme.txt", "no manifest here");
        }

        var (_, result) = CursorPackService.Install(packPath, InstallFolder("bare-target"));

        Assert.False(result.Success);
        Assert.Contains(CursorPackService.ManifestEntryName, result.Message);
    }

    [Fact]
    public void Install_RefusesANewerFormatVersion()
    {
        var packPath = PackPath("future" + CursorPackService.PackExtension);

        var manifest = new CursorSchemeExport { Name = "Future", FormatVersion = 99 };
        manifest.Assignments["Arrow"] = "";

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, CursorPackService.ManifestEntryName, JsonSerializer.Serialize(manifest));
        }

        var (_, result) = CursorPackService.Install(packPath, InstallFolder("future-target"));

        Assert.False(result.Success);
        Assert.Contains("v99", result.Message);
    }

    /// <summary>
    /// Sound packs and cursor packs both keep their manifest in scheme.json, and a sound
    /// manifest deserializes into a cursor manifest perfectly happily with every assignment
    /// silently dropped. Without this check the pack would install and change nothing.
    /// </summary>
    [Fact]
    public void Install_RefusesASoundPack()
    {
        var packPath = PackPath("sounds" + CursorPackService.PackExtension);

        var soundManifest = new SchemeExport { Name = "Sounds" };
        soundManifest.Assignments[@".Default\SystemHand"] = @"%SystemRoot%\media\Windows Notify.wav";

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, CursorPackService.ManifestEntryName, JsonSerializer.Serialize(soundManifest));
        }

        var (scheme, result) = CursorPackService.Install(packPath, InstallFolder("sound-target"));

        Assert.False(result.Success);
        Assert.Null(scheme);
        Assert.Contains("Sounds tab", result.Message);
    }

    [Fact]
    public void Install_WarnsWhenTheArchiveIsMissingAReferencedFile()
    {
        var packPath = PackPath("incomplete" + CursorPackService.PackExtension);

        var manifest = new CursorSchemeExport { Name = "Incomplete" };
        manifest.Assignments["Arrow"] = $"{CursorPackService.CursorFolderName}/absent.cur";

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, CursorPackService.ManifestEntryName, JsonSerializer.Serialize(manifest));
        }

        var (scheme, result) = CursorPackService.Install(packPath, InstallFolder("incomplete-target"));

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("not in the archive"));
        Assert.Equal(string.Empty, scheme!.Assignments["Arrow"]);
    }

    [Fact]
    public void Install_RefusesAFileThatIsNotAZip()
    {
        var notAZip = _cursors.WriteGarbage("notazip" + CursorPackService.PackExtension);

        var (_, result) = CursorPackService.Install(notAZip, InstallFolder("nozip-target"));

        Assert.False(result.Success);
        Assert.Contains("zip", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_RefusesAMissingFile()
    {
        var (_, result) = CursorPackService.Install(_cursors.PathFor("nope.winchimecursorpack"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    // ---------------------------------------------------------------- round trip --

    [Fact]
    public void RoundTrip_PreservesEveryKindOfAssignment()
    {
        var arrow = _cursors.WriteCur("arrow.cur");
        var busy = _cursors.WriteAni("busy.ani");

        var original = SchemeWith(
            ("Arrow", arrow),
            ("Wait", busy),
            ("Help", @"%SystemRoot%\Cursors\aero_helpsel.cur"),
            ("IBeam", ""));

        original.Name = "Round Trip";
        original.Author = "tests";

        Assert.True(CursorPackService.Create(PackPath(), original).Success);

        var (installed, result) = CursorPackService.Install(PackPath(), InstallFolder("roundtrip"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Round Trip", installed!.Name);
        Assert.Equal("tests", installed.Author);

        Assert.True(File.Exists(installed.Assignments["Arrow"]));
        Assert.True(File.Exists(installed.Assignments["Wait"]));
        Assert.Equal(@"%SystemRoot%\Cursors\aero_helpsel.cur", installed.Assignments["Help"]);
        Assert.Equal(string.Empty, installed.Assignments["IBeam"]);

        // The whole point: nothing that reaches a scheme string may contain a comma.
        Assert.All(installed.Assignments.Values, v => Assert.DoesNotContain(",", v));
    }

    [Fact]
    public void ToSchemeValues_IsOrderedLikeTheRoleList()
    {
        var scheme = SchemeWith(("Wait", @"C:\busy.ani"), ("Arrow", @"C:\arrow.cur"));

        var values = CursorPackService.ToSchemeValues(scheme);

        Assert.Equal(CursorRoles.All.Count, values.Count);
        Assert.Equal(@"C:\arrow.cur", values[CursorRoles.IndexOf("Arrow")]);
        Assert.Equal(@"C:\busy.ani", values[CursorRoles.IndexOf("Wait")]);
        Assert.Equal(string.Empty, values[CursorRoles.IndexOf("Crosshair")]);
    }

    // ------------------------------------------------------------------- helpers --

    private static CursorSchemeExport ReadManifest(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry(CursorPackService.ManifestEntryName)!.Open());
        return JsonSerializer.Deserialize<CursorSchemeExport>(reader.ReadToEnd())!;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void TryCleanup(string? folder)
    {
        if (folder is null) return;
        try { Directory.Delete(folder, recursive: true); } catch { /* temp files */ }
    }
}
