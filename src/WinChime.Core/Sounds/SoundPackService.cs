using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinChime.Core.Model;

namespace WinChime.Core.Sounds;

/// <summary>
/// Sound packs: a whole scheme plus the audio it needs, in one shareable file.
///
/// A plain .winchime.json scheme only travels usefully between machines that already have
/// identical files at identical paths, which in practice means it does not travel at all.
/// A pack is a zip holding scheme.json and a media folder, so sending someone a sound
/// scheme is sending them one file.
///
/// Two things this deliberately does not bundle:
///
///   Windows' own sounds. An assignment pointing at %SystemRoot%\media\... is kept as that
///   literal unexpanded string. Those files exist on every Windows install, so copying them
///   in would bloat the pack and redistribute Microsoft's audio for no benefit.
///
///   Duplicates. One source file referenced by twelve events is stored once and referenced
///   twelve times.
/// </summary>
public static class SoundPackService
{
    public const string PackExtension = ".winchimepack";
    public const string ManifestEntryName = "scheme.json";
    public const string MediaFolderName = "media";

    /// <summary>Guards against zip bombs. Generous for real packs, far below anything harmful.</summary>
    private const long MaxTotalUncompressedBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PacksFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinChime", "packs");

    public static string OpenFileFilter =>
        $"WinChime sound pack (*{PackExtension})|*{PackExtension}|All files (*.*)|*.*";

    // ------------------------------------------------------------------ creating --

    public static PackResult Create(string destinationPath, SchemeExport scheme)
    {
        var warnings = new List<string>();

        try
        {
            var packed = new SchemeExport
            {
                Name = scheme.Name,
                Author = scheme.Author,
                Description = scheme.Description,
                CreatedUtc = scheme.CreatedUtc,
                BundledMediaFolder = MediaFolderName,
            };

            // Source path -> media entry name, so a file used by many events is stored once.
            var entryBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var systemSounds = 0;

            using (var stream = File.Create(destinationPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var pair in scheme.Assignments)
                {
                    var raw = pair.Value;

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        packed.Assignments[pair.Key] = string.Empty;
                        continue;
                    }

                    // Present on every Windows install; keep the unexpanded form so it
                    // resolves wherever the pack lands.
                    if (IsWindowsShippedSound(raw))
                    {
                        packed.Assignments[pair.Key] = raw;
                        systemSounds++;
                        continue;
                    }

                    string full;
                    try
                    {
                        full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"{pair.Key}: unusable path ({ex.Message})");
                        continue;
                    }

                    if (!File.Exists(full))
                    {
                        warnings.Add($"{pair.Key}: file not found, left out of the pack ({full})");
                        continue;
                    }

                    if (!entryBySource.TryGetValue(full, out var entryName))
                    {
                        entryName = UniqueEntryName(Path.GetFileName(full), usedNames);
                        archive.CreateEntryFromFile(full, $"{MediaFolderName}/{entryName}", CompressionLevel.Optimal);
                        entryBySource[full] = entryName;
                    }

                    packed.Assignments[pair.Key] = $"{MediaFolderName}/{entryName}";
                }

                var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
                writer.Write(JsonSerializer.Serialize(packed, JsonOptions));
            }

            var size = new FileInfo(destinationPath).Length;
            var message =
                $"Packed {packed.Assignments.Count} assignment(s): {entryBySource.Count} audio file(s) bundled, " +
                $"{systemSounds} Windows sound(s) referenced by name. {size / 1024.0 / 1024.0:0.##} MB.";

            return new PackResult(true, message, destinationPath) { Warnings = warnings };
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);   // never leave a half-written pack behind
            return PackResult.Fail($"Could not create the pack: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- installing --

    /// <summary>
    /// Extracts a pack and returns a scheme whose assignments point at the extracted files,
    /// ready to hand to <see cref="SoundSchemeService.ApplyExport"/>.
    /// </summary>
    public static (SchemeExport? Scheme, PackResult Result) Install(string packPath, string? installFolder = null)
    {
        if (!File.Exists(packPath))
            return (null, PackResult.Fail($"Pack not found: {packPath}"));

        var warnings = new List<string>();

        try
        {
            using var archive = ZipFile.OpenRead(packPath);

            if (archive.Entries.Count > MaxEntries)
                return (null, PackResult.Fail($"Pack contains {archive.Entries.Count} entries, which exceeds the {MaxEntries} limit."));

            var totalBytes = archive.Entries.Sum(e => e.Length);
            if (totalBytes > MaxTotalUncompressedBytes)
            {
                return (null, PackResult.Fail(
                    $"Pack expands to {totalBytes / 1024.0 / 1024.0:0.#} MB, which exceeds the " +
                    $"{MaxTotalUncompressedBytes / 1024 / 1024} MB limit."));
            }

            var manifestEntry = archive.GetEntry(ManifestEntryName);
            if (manifestEntry is null)
                return (null, PackResult.Fail($"Pack has no {ManifestEntryName}; it may not be a WinChime pack."));

            SchemeExport? manifest;
            using (var reader = new StreamReader(manifestEntry.Open()))
            {
                manifest = JsonSerializer.Deserialize<SchemeExport>(reader.ReadToEnd(), JsonOptions);
            }

            if (manifest is null)
                return (null, PackResult.Fail("Pack manifest could not be read."));

            if (manifest.FormatVersion > 1)
                return (null, PackResult.Fail($"Pack format v{manifest.FormatVersion} is newer than this build understands."));

            var folder = installFolder ?? Path.Combine(PacksFolder, SanitiseFolderName(manifest.Name));
            Directory.CreateDirectory(folder);
            var root = Path.GetFullPath(folder);

            // Extract media, resolving each entry to an absolute path on disk.
            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(entry.Name)) continue;   // directory marker

                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

                // Zip slip: an entry named ../../evil.exe would otherwise write outside the
                // extraction folder. Packs are files people receive from other people, so
                // this is a real attack surface, not a theoretical one.
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Refused entry that escapes the pack folder: {entry.FullName}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                extracted[entry.FullName.Replace('\\', '/')] = destination;
            }

            // Rewrite relative media references to the extracted absolute paths. Windows
            // sounds keep their %SystemRoot% form and resolve natively.
            var resolved = new SchemeExport
            {
                Name = manifest.Name,
                Author = manifest.Author,
                Description = manifest.Description,
                CreatedUtc = manifest.CreatedUtc,
            };

            foreach (var pair in manifest.Assignments)
            {
                var value = pair.Value;

                if (string.IsNullOrWhiteSpace(value) || IsWindowsShippedSound(value) || Path.IsPathRooted(value))
                {
                    resolved.Assignments[pair.Key] = value;
                    continue;
                }

                var normalised = value.Replace('\\', '/');
                if (extracted.TryGetValue(normalised, out var actual))
                {
                    resolved.Assignments[pair.Key] = actual;
                }
                else
                {
                    warnings.Add($"{pair.Key}: pack references {value}, which is not in the archive");
                }
            }

            var message =
                $"Installed {resolved.Assignments.Count} assignment(s) from {manifest.Name}, " +
                $"{extracted.Count} audio file(s) extracted to {folder}.";

            return (resolved, new PackResult(true, message, folder) { Warnings = warnings });
        }
        catch (InvalidDataException)
        {
            return (null, PackResult.Fail("That file is not a readable zip archive."));
        }
        catch (Exception ex)
        {
            return (null, PackResult.Fail($"Could not install the pack: {ex.Message}"));
        }
    }

    // -------------------------------------------------------------------- helpers --

    /// <summary>
    /// True for sounds that ship with Windows, which are referenced rather than bundled.
    /// The rule itself lives in <see cref="WindowsShippedFile"/> because cursor packs make
    /// exactly the same decision, and two copies of it would eventually disagree.
    /// </summary>
    public static bool IsWindowsShippedSound(string rawValue) => WindowsShippedFile.Is(rawValue);

    private static string UniqueEntryName(string fileName, HashSet<string> used)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        if (string.IsNullOrWhiteSpace(fileName)) fileName = "sound.wav";
        if (used.Add(fileName)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string SanitiseFolderName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? "pack" : name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
