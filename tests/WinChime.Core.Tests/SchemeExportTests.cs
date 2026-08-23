using System.Text.Json;
using WinChime.Core.Model;
using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

public sealed class SchemeExportTests : IDisposable
{
    private readonly ScratchRegistry _reg = new();
    private readonly TestWav _wav = new();
    private readonly SoundSchemeService _service;

    public SchemeExportTests()
    {
        _service = _reg.CreateService();
        _reg.SeedApp(".Default", "Windows");
        _reg.SeedEvent(".Default", "SystemHand", current: @"C:\a.wav", defaultValue: @"C:\a.wav");
        _reg.SeedEvent(".Default", "SystemAsterisk", current: @"C:\b.wav", defaultValue: @"C:\b.wav");
    }

    public void Dispose()
    {
        _reg.Dispose();
        _wav.Dispose();
    }

    [Fact]
    public void ExportThenImport_RoundTripsAssignments()
    {
        var file = _wav.PathFor("scheme.winchime.json");
        var export = _service.BuildExport("Round Trip", author: "tester");

        Assert.True(_service.ExportToFile(file, export).Success);

        var (imported, error) = _service.ImportFromFile(file);

        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Equal("Round Trip", imported!.Name);
        Assert.Equal("tester", imported.Author);
        Assert.Equal(export.Assignments.Count, imported.Assignments.Count);
        Assert.Equal(@"C:\a.wav", imported.Assignments[@".Default\SystemHand"]);
    }

    /// <summary>
    /// Schemes deliberately store the unexpanded value so %SystemRoot% resolves on whatever
    /// machine imports them, rather than baking in the exporting machine's paths.
    /// </summary>
    [Fact]
    public void BuildExport_PreservesUnexpandedPaths()
    {
        _service.SetSound(".Default", "SystemHand", @"%SystemRoot%\media\x.wav");

        var export = _service.BuildExport("Unexpanded");

        Assert.Equal(@"%SystemRoot%\media\x.wav", export.Assignments[@".Default\SystemHand"]);
    }

    [Fact]
    public void ImportFromFile_MalformedJson_ReturnsErrorRatherThanThrowing()
    {
        var file = _wav.PathFor("broken.json");
        File.WriteAllText(file, "{ this is not json ");

        var (imported, error) = _service.ImportFromFile(file);

        Assert.Null(imported);
        Assert.NotNull(error);
    }

    [Fact]
    public void ImportFromFile_MissingFile_ReturnsErrorRatherThanThrowing()
    {
        var (imported, error) = _service.ImportFromFile(_wav.PathFor("absent.json"));

        Assert.Null(imported);
        Assert.NotNull(error);
    }

    [Fact]
    public void ImportFromFile_NewerFormatVersion_IsRefused()
    {
        var file = _wav.PathFor("future.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new SchemeExport
        {
            FormatVersion = 99,
            Name = "From the future",
        }));

        var (imported, error) = _service.ImportFromFile(file);

        Assert.Null(imported);
        Assert.Contains("newer", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The common failure when a scheme moves between machines. Windows gives no feedback
    /// at all for an event pointing at a file that is gone, so these are reported instead
    /// of being assigned blindly.
    /// </summary>
    [Fact]
    public void ApplyExport_SkipsAndReportsAssignmentsWhoseFileIsMissing()
    {
        var real = _wav.WritePcm("present.wav");

        var export = new SchemeExport
        {
            Name = "Partly missing",
            Assignments =
            {
                [@".Default\SystemHand"] = real,
                [@".Default\SystemAsterisk"] = @"C:\definitely\absent.wav",
            },
        };

        var (result, missing) = _service.ApplyExport(export);

        Assert.True(result.Success);
        Assert.Single(missing);
        Assert.Contains("absent.wav", missing[0]);

        Assert.Equal(real, _reg.ReadCurrent(".Default", "SystemHand"));
        Assert.Equal(@"C:\b.wav", _reg.ReadCurrent(".Default", "SystemAsterisk"));   // untouched
    }

    [Fact]
    public void ApplyExport_WhenNotSkipping_AssignsMissingFilesAnyway()
    {
        var export = new SchemeExport
        {
            Name = "Force",
            Assignments = { [@".Default\SystemAsterisk"] = @"C:\definitely\absent.wav" },
        };

        var (_, missing) = _service.ApplyExport(export, skipMissingFiles: false);

        Assert.Single(missing);
        Assert.Equal(@"C:\definitely\absent.wav", _reg.ReadCurrent(".Default", "SystemAsterisk"));
    }

    [Fact]
    public void ApplyExport_IgnoresMalformedKeys()
    {
        var export = new SchemeExport
        {
            Name = "Malformed",
            Assignments = { ["NoBackslashHere"] = @"C:\x.wav" },
        };

        var (result, _) = _service.ApplyExport(export);

        Assert.True(result.Success);   // ignored, not crashed
    }

    [Fact]
    public void ExportedFile_IsHumanReadableJson()
    {
        var file = _wav.PathFor("readable.json");
        _service.ExportToFile(file, _service.BuildExport("Readable"));

        var text = File.ReadAllText(file);

        Assert.Contains("\n", text);                 // indented, not minified
        Assert.Contains("\"name\": \"Readable\"", text);
        Assert.False(text.StartsWith('\uFEFF'));     // no BOM
    }
}
