using System.Text;
using System.Text.Json;
using WinChime.Core.Model;
using WinChime.Core.Sounds;

namespace WinChime.Core.Safety;

/// <summary>
/// The primary undo path. Independent of System Restore, so it still works on machines
/// where restore points are disabled, which is most consumer Windows 11 installs.
///
/// Each backup is a folder under %LOCALAPPDATA%\WinChime\backups\{id} holding a
/// manifest.json. Registry-only by design: every change this app makes is a registry
/// value, so a few KB of JSON is a complete snapshot. There is no file-copy or hashing
/// path because no system file is ever modified.
/// </summary>
public sealed class BackupService
{
    private readonly SoundSchemeService _sounds;
    private readonly string _root;

    /// <param name="backupRoot">
    /// Defaults to <see cref="BackupRoot"/>. Overridable so tests can write somewhere
    /// disposable rather than filling the running user's real backup folder.
    /// </param>
    public BackupService(SoundSchemeService sounds, string? backupRoot = null)
    {
        _sounds = sounds;
        _root = backupRoot ?? BackupRoot;
    }

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinChime", "backups");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Snapshots the full sound configuration. Cheap (a few KB of JSON), so the UI takes
    /// one automatically before any bulk change such as applying or importing a scheme.
    /// </summary>
    public (OperationResult Result, BackupManifest? Manifest) CreateSoundBackup(string label)
    {
        try
        {
            var info = SystemProbe.Capture();
            var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            var folder = Path.Combine(_root, id);
            Directory.CreateDirectory(folder);

            var manifest = new BackupManifest
            {
                Id = id,
                Label = label,
                WindowsBuild = $"{info.BuildNumber}.{info.UpdateBuildRevision}",
                SoundAssignments = _sounds.CaptureAssignments(),
            };

            manifest.RegistryValues[@"HKCU\AppEvents\Schemes!(Default)"] = _sounds.GetActiveSchemeKey();

            WriteManifest(folder, manifest);

            return (OperationResult.Ok($"Backed up {manifest.SoundAssignments.Count} sound assignment(s)."), manifest);
        }
        catch (Exception ex)
        {
            return (OperationResult.Fail($"Backup failed: {ex.Message}"), null);
        }
    }

    public IReadOnlyList<BackupManifest> List()
    {
        var list = new List<BackupManifest>();
        if (!Directory.Exists(_root)) return list;

        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            var manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize<BackupManifest>(
                    File.ReadAllText(manifestPath), JsonOptions);

                if (manifest is not null) list.Add(manifest);
            }
            catch
            {
                // A corrupt manifest should not hide the backups either side of it.
            }
        }

        return list.OrderByDescending(m => m.CreatedUtc).ToList();
    }

    public OperationResult RestoreSounds(BackupManifest manifest)
    {
        if (manifest.SoundAssignments.Count == 0)
            return OperationResult.Fail("This backup contains no sound assignments.");

        var result = _sounds.RestoreAssignments(manifest.SoundAssignments);

        if (manifest.RegistryValues.TryGetValue(@"HKCU\AppEvents\Schemes!(Default)", out var scheme)
            && !string.IsNullOrWhiteSpace(scheme))
        {
            _sounds.SetActiveSchemeKey(scheme);
        }

        return result;
    }

    public OperationResult Delete(BackupManifest manifest)
    {
        try
        {
            var folder = Path.Combine(_root, manifest.Id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return OperationResult.Ok($"Deleted backup {manifest.Id}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not delete backup: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- helpers --

    private static void WriteManifest(string folder, BackupManifest manifest) =>
        File.WriteAllText(
            Path.Combine(folder, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
