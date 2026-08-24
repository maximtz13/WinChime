using System.IO.Compression;
using System.Text.Json;
using WinChime.Core.Model;

namespace WinChime.Core.Cursors;

/// <summary>
/// Cursor packs: a whole cursor scheme plus the .cur and .ani files it needs, in one
/// shareable file.
///
/// Cursors travel worse than sounds. A sound scheme at least has %SystemRoot% fallbacks that
/// resolve everywhere; a cursor scheme is a list of absolute paths into whatever folder the
/// author happened to download a cursor set into, so sending one to another machine produces
/// seventeen broken pointers and no error message. A pack is a zip holding scheme.json and a
/// cursors folder, so sending someone a cursor theme is sending them one file.
///
/// Mirrors <see cref="Sounds.SoundPackService"/> closely, with three differences that matter:
///
///   Commas are structurally fatal. A cursor scheme is stored as one comma-separated string
///   where meaning comes entirely from position, and a comma is a perfectly legal Windows
///   filename character. An extracted file called "arrow,2.cur" would shift every later role
///   by one, silently. Every name this service produces is stripped of commas, and
///   CursorSchemeService.SaveScheme refuses any that slip through.
///
///   Files are validated before being packed. Windows fails silently on a file that is not a
///   real cursor, so a pack that bundled one would install cleanly and then do nothing.
///
///   Installing registers a named scheme rather than only rewriting the live values, so the
///   pack appears in the scheme list and the user can switch back to it later.
/// </summary>
public static class CursorPackService
{
    public const string PackExtension = ".winchimecursorpack";
    public const string ManifestEntryName = "scheme.json";
    public const string CursorFolderName = "cursors";

    /// <summary>
    /// Guards against zip bombs. Far smaller than the sound equivalent because cursors are
    /// tiny: seventeen animated cursors is a few hundred KB, so anything near this is either
    /// a mistake or hostile.
    /// </summary>
    private const long MaxTotalUncompressedBytes = 32L * 1024 * 1024;

    private const int MaxEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PacksFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinChime", "cursorpacks");

    public static string OpenFileFilter =>
        $"WinChime cursor pack (*{PackExtension})|*{PackExtension}|All files (*.*)|*.*";

    // ------------------------------------------------------------------ creating --

    /// <summary>
    /// Writes a pack from a set of cursor assignments, bundling every file that is not part
    /// of Windows.
    /// </summary>
    public static PackResult Create(string destinationPath, CursorSchemeExport scheme)
    {
        var warnings = new List<string>();

        try
        {
            var packed = new CursorSchemeExport
            {
                Name = scheme.Name,
                Author = scheme.Author,
                Description = scheme.Description,
                CreatedUtc = scheme.CreatedUtc,
                BundledCursorFolder = CursorFolderName,
            };

            // Source path -> entry name, so one file used by several roles is stored once.
            // Resize cursors in particular are often the same file under two roles.
            var entryBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var systemDrawn = 0;
            var windowsShipped = 0;

            using (var stream = File.Create(destinationPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var role in CursorRoles.All)
                {
                    if (!scheme.Assignments.TryGetValue(role.Key, out var raw) || string.IsNullOrWhiteSpace(raw))
                    {
                        // Windows draws this one. A normal state, not a gap.
                        packed.Assignments[role.Key] = string.Empty;
                        systemDrawn++;
                        continue;
                    }

                    if (WindowsShippedFile.Is(raw))
                    {
                        // Collapsed rather than stored as-is. Unlike sound assignments, the
                        // cursor values in the registry are typically already expanded to
                        // C:\WINDOWS\cursors\..., and a pack carrying that literal path works
                        // on the machine that made it and nowhere else.
                        packed.Assignments[role.Key] = WindowsShippedFile.Collapse(raw);
                        windowsShipped++;
                        continue;
                    }

                    if (entryBySource.TryGetValue(raw, out var existing))
                    {
                        packed.Assignments[role.Key] = existing;
                        continue;
                    }

                    var expanded = Environment.ExpandEnvironmentVariables(raw);

                    if (!File.Exists(expanded))
                    {
                        warnings.Add($"{role.DisplayName}: file no longer exists, left out ({expanded})");
                        packed.Assignments[role.Key] = string.Empty;
                        continue;
                    }

                    // Windows silently ignores a file that is not a real cursor, so packing
                    // one would produce a pack that installs cleanly and changes nothing.
                    var info = CursorFile.Inspect(expanded);
                    if (!info.IsValid)
                    {
                        warnings.Add($"{role.DisplayName}: not a usable cursor, left out ({info.Error})");
                        packed.Assignments[role.Key] = string.Empty;
                        continue;
                    }

                    var entryName = $"{CursorFolderName}/{UniqueEntryName(Path.GetFileName(expanded), usedNames)}";

                    archive.CreateEntryFromFile(expanded, entryName, CompressionLevel.Optimal);

                    entryBySource[raw] = entryName;
                    packed.Assignments[role.Key] = entryName;
                }

                var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifest.Open());
                writer.Write(JsonSerializer.Serialize(packed, JsonOptions));
            }

            var bundled = entryBySource.Count;

            var message =
                $"Wrote {Path.GetFileName(destinationPath)}: {bundled} cursor file(s) bundled, " +
                $"{windowsShipped} referenced from Windows, {systemDrawn} drawn by Windows.";

            return new PackResult(true, message, destinationPath) { Warnings = warnings };
        }
        catch (Exception ex)
        {
            // A half-written zip is worse than none: it looks like a pack and fails on open.
            TryDelete(destinationPath);
            return PackResult.Fail($"Could not create the pack: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- installing --

    /// <summary>
    /// Extracts a pack and returns its assignments rewritten to point at the extracted files.
    /// Does not apply anything; the caller decides when the pointer changes.
    /// </summary>
    public static (CursorSchemeExport? Scheme, PackResult Result) Install(string packPath, string? installFolder = null)
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
                return (null, PackResult.Fail($"Pack has no {ManifestEntryName}; it may not be a WinChime cursor pack."));

            CursorSchemeExport? manifest;
            using (var reader = new StreamReader(manifestEntry.Open()))
            {
                manifest = JsonSerializer.Deserialize<CursorSchemeExport>(reader.ReadToEnd(), JsonOptions);
            }

            if (manifest is null)
                return (null, PackResult.Fail("Pack manifest could not be read."));

            if (manifest.FormatVersion > 1)
                return (null, PackResult.Fail($"Pack format v{manifest.FormatVersion} is newer than this build understands."));

            // A pack whose manifest holds no cursor roles at all is almost certainly a SOUND
            // pack: both use scheme.json, and a sound manifest deserializes into this type
            // perfectly happily with every assignment silently discarded.
            if (!manifest.Assignments.Keys.Any(k => CursorRoles.Find(k) is not null))
            {
                return (null, PackResult.Fail(
                    "That pack contains no cursor assignments. If it is a sound pack, import it from the Sounds tab."));
            }

            var folder = installFolder ?? Path.Combine(PacksFolder, SanitiseFolderName(manifest.Name));
            Directory.CreateDirectory(folder);
            var root = Path.GetFullPath(folder);

            // The extracted paths end up inside a comma-separated scheme string, so a comma
            // anywhere along them would corrupt it. The folder name is sanitised above; this
            // catches the rest of the path, which the caller chose.
            if (root.Contains(','))
            {
                return (null, PackResult.Fail(
                    $"Cursor packs cannot be installed into a path containing a comma, because a cursor scheme " +
                    $"separates its entries with one: {root}"));
            }

            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(entry.Name)) continue;   // directory marker

                // Commas are stripped from the name on the way out. Renaming is safe because
                // nothing outside the pack refers to these files; the manifest is rewritten
                // to the new path below.
                var safeName = entry.Name.Replace(",", string.Empty);
                var directory = Path.GetDirectoryName(entry.FullName)?.Replace(",", string.Empty) ?? string.Empty;

                var destination = Path.GetFullPath(Path.Combine(root, directory, safeName));

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

            var resolved = new CursorSchemeExport
            {
                Name = manifest.Name,
                Author = manifest.Author,
                Description = manifest.Description,
                CreatedUtc = manifest.CreatedUtc,
            };

            foreach (var role in CursorRoles.All)
            {
                if (!manifest.Assignments.TryGetValue(role.Key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    resolved.Assignments[role.Key] = string.Empty;
                    continue;
                }

                // Windows cursors keep their unexpanded form and resolve natively. An absolute
                // path in a pack is someone else's machine, but it is not this code's place to
                // second-guess it; it simply will not exist and will show as Missing.
                if (WindowsShippedFile.Is(value) || Path.IsPathRooted(value))
                {
                    resolved.Assignments[role.Key] = value;
                    continue;
                }

                var normalised = value.Replace('\\', '/');

                if (extracted.TryGetValue(normalised, out var actual))
                {
                    resolved.Assignments[role.Key] = actual;
                }
                else
                {
                    warnings.Add($"{role.DisplayName}: pack references {value}, which is not in the archive");
                    resolved.Assignments[role.Key] = string.Empty;
                }
            }

            var assigned = resolved.Assignments.Count(a => !string.IsNullOrWhiteSpace(a.Value));

            var message =
                $"Installed {manifest.Name}: {assigned} cursor(s) assigned, " +
                $"{extracted.Count} file(s) extracted to {folder}.";

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
    /// Ordered values for <c>CursorSchemeService.SaveScheme</c>, taken from an installed pack.
    /// </summary>
    public static IReadOnlyList<string> ToSchemeValues(CursorSchemeExport scheme) =>
        CursorRoles.All
            .Select(role => scheme.Assignments.TryGetValue(role.Key, out var value) ? value : string.Empty)
            .ToList();

    private static string UniqueEntryName(string fileName, HashSet<string> used)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        // Not an invalid filename character, which is exactly why it is dangerous here.
        fileName = fileName.Replace(",", string.Empty);

        if (string.IsNullOrWhiteSpace(fileName)) fileName = "cursor.cur";
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

        name = name.Replace(",", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(name) ? "pack" : name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
