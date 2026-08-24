using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinChime.Core.Model;
using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

public sealed class SoundPackServiceTests : IDisposable
{
    private readonly TestWav _wav = new();

    public void Dispose() => _wav.Dispose();

    private string PackPath(string name = "pack" + SoundPackService.PackExtension) => _wav.PathFor(name);

    private string InstallFolder(string name = "installed")
    {
        var folder = _wav.PathFor(name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ------------------------------------------------------------------ creating --

    [Fact]
    public void Create_BundlesUserSuppliedAudio()
    {
        var sound = _wav.WritePcm("custom.wav");
        var scheme = new SchemeExport
        {
            Name = "Test",
            Assignments = { [@".Default\SystemHand"] = sound },
        };

        var result = SoundPackService.Create(PackPath(), scheme);

        Assert.True(result.Success, result.Message);

        using var archive = ZipFile.OpenRead(PackPath());
        Assert.NotNull(archive.GetEntry(SoundPackService.ManifestEntryName));
        Assert.NotNull(archive.GetEntry($"{SoundPackService.MediaFolderName}/custom.wav"));
    }

    /// <summary>
    /// Windows' own sounds exist on every install. Copying them in would bloat the pack and
    /// redistribute Microsoft's audio for no benefit, so they stay as %SystemRoot% strings.
    /// </summary>
    [Fact]
    public void Create_ReferencesWindowsSoundsByNameInsteadOfBundlingThem()
    {
        var scheme = new SchemeExport
        {
            Name = "Test",
            Assignments = { [@".Default\SystemHand"] = @"%SystemRoot%\media\Windows Notify.wav" },
        };

        Assert.True(SoundPackService.Create(PackPath(), scheme).Success);

        using var archive = ZipFile.OpenRead(PackPath());
        Assert.Empty(archive.Entries.Where(e => e.FullName.StartsWith(SoundPackService.MediaFolderName)));

        var manifest = ReadManifest(archive);
        Assert.Equal(@"%SystemRoot%\media\Windows Notify.wav", manifest.Assignments[@".Default\SystemHand"]);
    }

    /// <summary>
    /// The registry is not consistent about which form it stores. Counting the assignments on
    /// a stock Windows 11 install found twenty-seven held as a literal C:\WINDOWS\media\...
    /// against twenty-two as %SystemRoot%, so most Windows sounds were going into packs as a
    /// machine-specific path: fine on the machine that made the pack, broken anywhere Windows
    /// is not on C:.
    /// </summary>
    [Fact]
    public void Create_CollapsesAnExpandedWindowsPathBackToSystemRoot()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var expanded = Path.Combine(windows, "media", "Windows Notify.wav");

        var scheme = new SchemeExport
        {
            Name = "Test",
            Assignments = { [@".Default\SystemHand"] = expanded },
        };

        Assert.True(SoundPackService.Create(PackPath(), scheme).Success);

        using var archive = ZipFile.OpenRead(PackPath());

        // Still referenced rather than bundled.
        Assert.Empty(archive.Entries.Where(e => e.FullName.StartsWith(SoundPackService.MediaFolderName)));

        var stored = ReadManifest(archive).Assignments[@".Default\SystemHand"];

        Assert.StartsWith("%SystemRoot%", stored, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"media\Windows Notify.wav", stored, StringComparison.OrdinalIgnoreCase);

        // And it still resolves back to where it came from.
        Assert.Equal(
            Path.GetFullPath(expanded),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(stored)));
    }

    /// <summary>
    /// A pack made from an expanded path and one made from the unexpanded form must be
    /// interchangeable, since which one the registry happens to hold is not something the
    /// person sharing the pack chose or can see.
    /// </summary>
    [Fact]
    public void Create_StoresTheSameThingWhicheverFormTheRegistryHeld()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        string Pack(string value)
        {
            var path = _wav.PathFor($"form-{value.GetHashCode():X}{SoundPackService.PackExtension}");

            var scheme = new SchemeExport
            {
                Name = "Test",
                Assignments = { [@".Default\SystemHand"] = value },
            };

            Assert.True(SoundPackService.Create(path, scheme).Success);

            using var archive = ZipFile.OpenRead(path);
            return ReadManifest(archive).Assignments[@".Default\SystemHand"];
        }

        var fromExpanded = Pack(Path.Combine(windows, "media", "Windows Notify.wav"));
        var fromUnexpanded = Pack(@"%SystemRoot%\media\Windows Notify.wav");

        Assert.Equal(fromUnexpanded, fromExpanded, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_StoresASharedFileOnlyOnce()
    {
        var sound = _wav.WritePcm("shared.wav");
        var scheme = new SchemeExport
        {
            Name = "Test",
            Assignments =
            {
                [@".Default\SystemHand"] = sound,
                [@".Default\SystemAsterisk"] = sound,
                [@"Explorer\EmptyRecycleBin"] = sound,
            },
        };

        Assert.True(SoundPackService.Create(PackPath(), scheme).Success);

        using var archive = ZipFile.OpenRead(PackPath());
        var media = archive.Entries.Where(e => e.FullName.StartsWith(SoundPackService.MediaFolderName)).ToList();

        Assert.Single(media);

        // All three events still resolve to that one entry.
        var manifest = ReadManifest(archive);
        Assert.All(manifest.Assignments.Values, v => Assert.Equal("media/shared.wav", v));
    }

    [Fact]
    public void Create_DisambiguatesSameFileNameFromDifferentFolders()
    {
        var folderA = _wav.PathFor("a");
        var folderB = _wav.PathFor("b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);

        var first = Path.Combine(folderA, "beep.wav");
        var second = Path.Combine(folderB, "beep.wav");
        File.Copy(_wav.WritePcm("seed.wav"), first);
        File.Copy(_wav.WritePcm("seed2.wav"), second);

        var scheme = new SchemeExport
        {
            Name = "Test",
            Assignments =
            {
                [@".Default\SystemHand"] = first,
                [@".Default\SystemAsterisk"] = second,
            },
        };

        Assert.True(SoundPackService.Create(PackPath(), scheme).Success);

        using var archive = ZipFile.OpenRead(PackPath());
        var media = archive.Entries
            .Where(e => e.FullName.StartsWith(SoundPackService.MediaFolderName))
            .Select(e => e.FullName)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(2, media.Count);
        Assert.Contains("media/beep.wav", media);
        Assert.Contains("media/beep (2).wav", media);
    }

    [Fact]
    public void Create_WarnsAboutMissingFilesRatherThanFailing()
    {
        var scheme = new SchemeExport
        {
            Name = "Test",
            Assignments = { [@".Default\SystemHand"] = @"C:\definitely\absent.wav" },
        };

        var result = SoundPackService.Create(PackPath(), scheme);

        Assert.True(result.Success);
        Assert.Single(result.Warnings);
        Assert.Contains("absent.wav", result.Warnings[0]);
    }

    [Fact]
    public void Create_PreservesSilencedAssignments()
    {
        var scheme = new SchemeExport
        {
            Name = "Test",
            Assignments = { [@".Default\SystemHand"] = "" },
        };

        Assert.True(SoundPackService.Create(PackPath(), scheme).Success);

        using var archive = ZipFile.OpenRead(PackPath());
        Assert.Equal("", ReadManifest(archive).Assignments[@".Default\SystemHand"]);
    }

    // ------------------------------------------------------------------ installing --

    [Fact]
    public void RoundTrip_InstallResolvesBundledAudioToRealFilesOnDisk()
    {
        var sound = _wav.WritePcm("custom.wav");
        var scheme = new SchemeExport
        {
            Name = "Round Trip",
            Author = "tester",
            Assignments =
            {
                [@".Default\SystemHand"] = sound,
                [@".Default\SystemAsterisk"] = @"%SystemRoot%\media\Windows Notify.wav",
                [@"Explorer\EmptyRecycleBin"] = "",
            },
        };

        Assert.True(SoundPackService.Create(PackPath(), scheme).Success);

        var (installed, result) = SoundPackService.Install(PackPath(), InstallFolder());

        Assert.True(result.Success, result.Message);
        Assert.NotNull(installed);
        Assert.Equal("Round Trip", installed!.Name);
        Assert.Equal("tester", installed.Author);

        // Bundled audio now points at a real extracted file.
        var resolved = installed.Assignments[@".Default\SystemHand"];
        Assert.True(Path.IsPathRooted(resolved));
        Assert.True(File.Exists(resolved));

        // Windows sounds keep the portable form, silence stays silence.
        Assert.Equal(@"%SystemRoot%\media\Windows Notify.wav", installed.Assignments[@".Default\SystemAsterisk"]);
        Assert.Equal("", installed.Assignments[@"Explorer\EmptyRecycleBin"]);
    }

    [Fact]
    public void RoundTrip_ExtractedAudioIsByteIdenticalToTheOriginal()
    {
        var sound = _wav.WritePcm("custom.wav", seconds: 0.75);
        var original = File.ReadAllBytes(sound);

        var scheme = new SchemeExport { Name = "T", Assignments = { [@".Default\SystemHand"] = sound } };
        Assert.True(SoundPackService.Create(PackPath(), scheme).Success);

        var (installed, _) = SoundPackService.Install(PackPath(), InstallFolder());

        Assert.Equal(original, File.ReadAllBytes(installed!.Assignments[@".Default\SystemHand"]));
    }

    /// <summary>
    /// Packs are files people receive from other people, so a hostile entry path is a real
    /// attack surface. An entry named ../../evil.txt must not be written outside the folder.
    /// </summary>
    [Fact]
    public void Install_RefusesEntriesThatEscapeTheExtractionFolder()
    {
        var packPath = PackPath("evil" + SoundPackService.PackExtension);
        var manifest = new SchemeExport { Name = "Evil", BundledMediaFolder = "media" };

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, SoundPackService.ManifestEntryName, JsonSerializer.Serialize(manifest));
            WriteEntry(archive, "../../escaped.txt", "you should never see this on disk");
        }

        var folder = InstallFolder("evil-target");
        var (_, result) = SoundPackService.Install(packPath, folder);

        Assert.True(result.Success);   // the pack still installs, the bad entry is dropped
        Assert.Contains(result.Warnings, w => w.Contains("escapes", StringComparison.OrdinalIgnoreCase));

        var escaped = Path.GetFullPath(Path.Combine(folder, "..", "..", "escaped.txt"));
        Assert.False(File.Exists(escaped));
    }

    [Fact]
    public void Install_NotAZipArchive_FailsCleanly()
    {
        var path = PackPath("notazip" + SoundPackService.PackExtension);
        File.WriteAllText(path, "definitely not a zip archive");

        var (scheme, result) = SoundPackService.Install(path, InstallFolder());

        Assert.Null(scheme);
        Assert.False(result.Success);
        Assert.Contains("zip", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_MissingFile_FailsCleanly()
    {
        var (scheme, result) = SoundPackService.Install(_wav.PathFor("absent.winchimepack"));

        Assert.Null(scheme);
        Assert.False(result.Success);
    }

    [Fact]
    public void Install_ZipWithoutManifest_IsRejected()
    {
        var path = PackPath("nomanifest" + SoundPackService.PackExtension);

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "media/lonely.wav", "not a real wav, but present");
        }

        var (scheme, result) = SoundPackService.Install(path, InstallFolder());

        Assert.Null(scheme);
        Assert.False(result.Success);
        Assert.Contains(SoundPackService.ManifestEntryName, result.Message);
    }

    [Fact]
    public void Install_NewerFormatVersion_IsRefused()
    {
        var path = PackPath("future" + SoundPackService.PackExtension);
        var manifest = new SchemeExport { FormatVersion = 99, Name = "Future" };

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, SoundPackService.ManifestEntryName, JsonSerializer.Serialize(manifest));
        }

        var (scheme, result) = SoundPackService.Install(path, InstallFolder());

        Assert.Null(scheme);
        Assert.Contains("newer", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_TooManyEntries_IsRefused()
    {
        var path = PackPath("many" + SoundPackService.PackExtension);

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, SoundPackService.ManifestEntryName,
                JsonSerializer.Serialize(new SchemeExport { Name = "Many" }));

            for (var i = 0; i < 1001; i++) WriteEntry(archive, $"media/f{i}.wav", "x");
        }

        var (scheme, result) = SoundPackService.Install(path, InstallFolder());

        Assert.Null(scheme);
        Assert.False(result.Success);
        Assert.Contains("limit", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_ManifestReferencingAbsentMedia_WarnsAndDropsThatEntry()
    {
        var path = PackPath("dangling" + SoundPackService.PackExtension);
        var manifest = new SchemeExport
        {
            Name = "Dangling",
            BundledMediaFolder = "media",
            Assignments = { [@".Default\SystemHand"] = "media/not-in-the-zip.wav" },
        };

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, SoundPackService.ManifestEntryName, JsonSerializer.Serialize(manifest));
        }

        var (scheme, result) = SoundPackService.Install(path, InstallFolder());

        Assert.NotNull(scheme);
        Assert.Empty(scheme!.Assignments);
        Assert.Contains(result.Warnings, w => w.Contains("not-in-the-zip.wav"));
    }

    // -------------------------------------------------------------------- helpers --

    [Theory]
    [InlineData(@"%SystemRoot%\media\x.wav", true)]
    [InlineData(@"%SYSTEMROOT%\media\x.wav", true)]
    [InlineData(@"%windir%\media\x.wav", true)]
    [InlineData(@"C:\Users\someone\Music\x.wav", false)]
    [InlineData("", false)]
    public void IsWindowsShippedSound_RecognisesTheShippedLocations(string raw, bool expected)
    {
        Assert.Equal(expected, SoundPackService.IsWindowsShippedSound(raw));
    }

    [Fact]
    public void OpenFileFilter_MentionsThePackExtension()
    {
        Assert.Contains(SoundPackService.PackExtension, SoundPackService.OpenFileFilter);
    }

    private static SchemeExport ReadManifest(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry(SoundPackService.ManifestEntryName)!.Open());
        return JsonSerializer.Deserialize<SchemeExport>(reader.ReadToEnd())!;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
