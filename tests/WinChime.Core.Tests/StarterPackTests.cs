using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

/// <summary>
/// Validates the pack committed to the repository, not a synthetic one.
///
/// Everything else in the suite tests the pack format against fixtures it built itself. This
/// tests the actual artefact people will download, so a regenerated-but-broken pack, or one
/// whose media stopped matching its manifest, fails CI rather than shipping.
/// </summary>
public sealed class StarterPackTests : IDisposable
{
    private const string PackName = "WinChime Chime.winchimepack";

    private readonly string _installFolder =
        Path.Combine(Path.GetTempPath(), "WinChime.Tests", $"pack-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_installFolder, recursive: true); } catch { /* temp files */ }
    }

    /// <summary>Walks up from the test assembly to the repository root, found by the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 8 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinChime.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test assembly.");
    }

    private static string PackPath() => Path.Combine(RepositoryRoot(), "packs", PackName);

    [Fact]
    public void TheCommittedPackExists()
    {
        Assert.True(File.Exists(PackPath()), $"Expected a committed pack at packs/{PackName}.");
    }

    [Fact]
    public void TheCommittedPackInstallsCleanly()
    {
        var (scheme, result) = SoundPackService.Install(PackPath(), _installFolder);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(scheme);
        Assert.Empty(result.Warnings);
        Assert.Equal("WinChime Chime", scheme!.Name);
    }

    /// <summary>
    /// The failure this is really guarding against: a manifest that references media the zip
    /// does not contain. It installs "successfully" and then every sound is silently absent.
    /// </summary>
    [Fact]
    public void EveryAssignmentResolvesToAFileThatExists()
    {
        var (scheme, _) = SoundPackService.Install(PackPath(), _installFolder);

        Assert.NotNull(scheme);
        Assert.NotEmpty(scheme!.Assignments);

        foreach (var pair in scheme.Assignments)
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"{pair.Key} resolved to nothing.");
            Assert.True(File.Exists(pair.Value), $"{pair.Key} points at a file that does not exist: {pair.Value}");
        }
    }

    /// <summary>
    /// A pack whose audio Windows cannot play would assign cleanly and then be silent, which
    /// is the exact failure this project exists to prevent. Checking it here means the
    /// shipped content is held to the same standard the app enforces on user input.
    /// </summary>
    [Fact]
    public void EverySoundIsPlayablePcm()
    {
        var (scheme, _) = SoundPackService.Install(PackPath(), _installFolder);

        foreach (var path in scheme!.Assignments.Values.Distinct())
        {
            var info = WaveFile.Inspect(path);

            Assert.True(info.IsValid, $"{Path.GetFileName(path)}: {info.Error}");
            Assert.True(info.IsPlayableByWindows, $"{Path.GetFileName(path)} is {info.FormatName}, not PCM.");
        }
    }

    [Fact]
    public void EverySoundIsShortEnoughForAnEventSound()
    {
        var (scheme, _) = SoundPackService.Install(PackPath(), _installFolder);

        foreach (var path in scheme!.Assignments.Values.Distinct())
        {
            var info = WaveFile.Inspect(path);

            Assert.True(
                info.Duration <= TranscodeOptions.SuggestedMaxEventDuration,
                $"{Path.GetFileName(path)} is {info.Duration.TotalSeconds:0.0}s, too long for an event sound.");

            Assert.Empty(info.Warnings);
        }
    }

    /// <summary>
    /// Several events deliberately share a sound, so the pack must store each file once.
    /// If this ever fails, deduplication has regressed and the pack is carrying duplicates.
    /// </summary>
    [Fact]
    public void SharedSoundsAreStoredOnce()
    {
        var (scheme, _) = SoundPackService.Install(PackPath(), _installFolder);

        var assignments = scheme!.Assignments.Count;
        var distinctFiles = scheme.Assignments.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Assert.True(assignments > distinctFiles,
            "The pack is expected to share some sounds across events, exercising deduplication.");
    }

    [Fact]
    public void ThePackTargetsRealWindowsEvents()
    {
        var (scheme, _) = SoundPackService.Install(PackPath(), _installFolder);

        // Every key must be App\Event, and the apps used must be ones Windows actually has.
        foreach (var key in scheme!.Assignments.Keys)
        {
            var split = key.Split('\\', 2);

            Assert.Equal(2, split.Length);
            Assert.Contains(split[0], new[] { ".Default", "Explorer" });
            Assert.False(string.IsNullOrWhiteSpace(split[1]));
        }
    }
}
