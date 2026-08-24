using Microsoft.Win32;
using WinChime.Core.Interop;
using WinChime.Core.Model;

namespace WinChime.Core.Cursors;

/// <summary>One assignable cursor and what is currently in it.</summary>
public sealed class CursorEntry
{
    public required string RoleKey { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Unexpanded registry value. Empty means Windows draws it itself.</summary>
    public string? CurrentPathRaw { get; init; }

    public string? CurrentPath => string.IsNullOrWhiteSpace(CurrentPathRaw)
        ? null
        : Environment.ExpandEnvironmentVariables(CurrentPathRaw);

    /// <summary>
    /// True when no file is assigned. This is a normal, valid state, not a missing value:
    /// several roles are system-drawn in the default scheme.
    /// </summary>
    public bool IsSystemDrawn => string.IsNullOrWhiteSpace(CurrentPathRaw);

    public bool IsBroken
    {
        get
        {
            var path = CurrentPath;
            return path is not null && !File.Exists(path);
        }
    }

    public string FileName => IsSystemDrawn ? "(system)" : SafeFileName(CurrentPath!);

    /// <summary>
    /// Deliberately "Assigned" rather than "Custom". Unlike sound events, Windows records no
    /// per-role default for cursors, so there is no way to tell a file the user chose from
    /// one that shipped with the active scheme. Saying "Custom" would claim knowledge that
    /// does not exist, and every stock Aero cursor would wrongly look user-modified.
    /// </summary>
    public string StatusText => IsBroken ? "Missing" : IsSystemDrawn ? "System" : "Assigned";

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); } catch { return path; }
    }
}

public sealed record CursorSchemeItem(string Name, bool IsSystemScheme)
{
    public override string ToString() => Name;
}

/// <summary>Where a set of cursor schemes lives.</summary>
public sealed record SchemeLocation(RegistryHive Hive, string Path);

/// <summary>
/// Read/write access to HKCU\Control Panel\Cursors.
///
/// Structurally close to the sound scheme system, with one important difference: a cursor
/// scheme is a single comma-separated string where meaning comes entirely from position,
/// rather than a subkey per event. Get the order wrong and every cursor silently becomes the
/// wrong one, so the order lives in <see cref="CursorRoles.All"/> and was verified against
/// the shipped Windows schemes rather than assumed.
///
/// User schemes live under HKCU and can be created and deleted. System schemes live under
/// HKLM, are read-only here, and are the ones Windows ships.
/// </summary>
public sealed class CursorSchemeService
{
    public const string DefaultCursorsRoot = @"Control Panel\Cursors";
    private const string SystemSchemesPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cursors\Schemes";

    /// <summary>Scheme Source tells Windows where the active scheme came from.</summary>
    private const int SchemeSourceNone = 0;
    private const int SchemeSourceUser = 1;
    private const int SchemeSourceSystem = 2;

    private readonly string _root;
    private readonly SchemeLocation _userSchemes;
    private readonly SchemeLocation _systemSchemes;

    public CursorSchemeService(
        string? cursorsRoot = null,
        SchemeLocation? userSchemes = null,
        SchemeLocation? systemSchemes = null)
    {
        _root = (cursorsRoot ?? DefaultCursorsRoot).Trim('\\');
        _userSchemes = userSchemes ?? new SchemeLocation(RegistryHive.CurrentUser, $@"{_root}\Schemes");
        _systemSchemes = systemSchemes ?? new SchemeLocation(RegistryHive.LocalMachine, SystemSchemesPath);
    }

    // ---------------------------------------------------------------- reading --

    public IReadOnlyList<CursorEntry> LoadCursors()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_root);

        return CursorRoles.All
            .Select(role => new CursorEntry
            {
                RoleKey = role.Key,
                DisplayName = role.DisplayName,
                CurrentPathRaw = key?.GetValue(
                    role.Key, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            })
            .ToList();
    }

    public string GetActiveSchemeName()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_root);
        var name = key?.GetValue(string.Empty) as string;

        return string.IsNullOrWhiteSpace(name) ? "Windows Default" : name;
    }

    public IReadOnlyList<CursorSchemeItem> ListSchemes()
    {
        var schemes = new List<CursorSchemeItem>();

        foreach (var name in ReadSchemeNames(_systemSchemes))
            schemes.Add(new CursorSchemeItem(name, IsSystemScheme: true));

        foreach (var name in ReadSchemeNames(_userSchemes))
        {
            // A user scheme shadowing a system one is the user's, so do not list it twice.
            if (schemes.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            schemes.Add(new CursorSchemeItem(name, IsSystemScheme: false));
        }

        return schemes.OrderBy(s => s.IsSystemScheme ? 1 : 0)
                      .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                      .ToList();
    }

    private static IReadOnlyList<string> ReadSchemeNames(SchemeLocation location)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Default);
            using var key = root.OpenSubKey(location.Path);

            return key is null
                ? Array.Empty<string>()
                : key.GetValueNames().Where(n => !string.IsNullOrEmpty(n)).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Reads a scheme string and maps it back onto roles by position. Extra trailing entries
    /// are ignored: shipped schemes append a control panel icon path and index after the
    /// cursors, which are display metadata rather than assignments.
    /// </summary>
    public IReadOnlyList<string>? ReadScheme(string name)
    {
        foreach (var location in new[] { _userSchemes, _systemSchemes })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Default);
                using var key = root.OpenSubKey(location.Path);

                if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw)
                    continue;

                var parts = raw.Split(',');
                var values = new string[CursorRoles.All.Count];

                for (var i = 0; i < values.Length; i++)
                    values[i] = i < parts.Length ? parts[i].Trim() : string.Empty;

                return values;
            }
            catch
            {
                // Try the next location.
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- writing --

    public OperationResult SetCursor(string roleKey, string? path)
    {
        if (CursorRoles.Find(roleKey) is null)
            return OperationResult.Fail($"{roleKey} is not a cursor role.");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_root, writable: true);
            if (key is null) return OperationResult.Fail($@"Could not open HKCU\{_root}.");

            WriteCursorValue(key, roleKey, path ?? string.Empty);

            // Changing a single cursor makes the active scheme no longer accurate.
            MarkSchemeModified(key);

            var applied = ApplyToSystem();

            return applied
                ? OperationResult.Ok(string.IsNullOrEmpty(path)
                    ? $"{roleKey} is now drawn by Windows."
                    : $"{roleKey} set to {Path.GetFileName(path)}.")
                : OperationResult.Fail("The registry was updated but Windows did not reload the cursors.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(@"Access denied writing to HKCU\Control Panel\Cursors.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Switches to a stored scheme. Atomic: a scheme writes seventeen values, and failing
    /// part-way would leave a mix of two pointer sets with no way back.
    /// </summary>
    public OperationResult ApplyScheme(string name)
    {
        var values = ReadScheme(name);
        if (values is null) return OperationResult.Fail($"No cursor scheme named {name}.");

        var snapshot = CaptureAssignments();
        var previousName = GetActiveSchemeName();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_root, writable: true);
            if (key is null) return OperationResult.Fail($@"Could not open HKCU\{_root}.");

            for (var i = 0; i < CursorRoles.All.Count; i++)
                WriteCursorValue(key, CursorRoles.All[i].Key, values[i]);

            key.SetValue(string.Empty, name, RegistryValueKind.String);

            var isSystem = ListSchemes().Any(s =>
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && s.IsSystemScheme);

            key.SetValue("Scheme Source", isSystem ? SchemeSourceSystem : SchemeSourceUser, RegistryValueKind.DWord);

            if (!ApplyToSystem())
                return OperationResult.Fail("The registry was updated but Windows did not reload the cursors.");

            var assigned = values.Count(v => !string.IsNullOrWhiteSpace(v));
            return OperationResult.Ok($"Applied {name}: {assigned} cursor(s), the rest drawn by Windows.");
        }
        catch (Exception ex)
        {
            var rollback = RestoreAssignments(snapshot);
            TrySetSchemeName(previousName);
            ApplyToSystem();

            return OperationResult.Fail(rollback.Success
                ? $"Could not apply {name} ({ex.Message}). Your previous cursors were put back."
                : $"Could not apply {name} ({ex.Message}), and restoring the previous cursors also failed " +
                  $"({rollback.Message}).");
        }
    }

    public OperationResult SaveCurrentAsScheme(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return OperationResult.Fail("Scheme name is empty.");

        if (trimmed.Contains(','))
            return OperationResult.Fail("Scheme names cannot contain a comma; it separates entries in the stored value.");

        if (ListSchemes().Any(s => s.IsSystemScheme && s.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return OperationResult.Fail($"{trimmed} is a scheme that ships with Windows and cannot be overwritten.");

        try
        {
            var cursors = LoadCursors();
            var value = string.Join(",", CursorRoles.All.Select(role =>
                cursors.First(c => c.RoleKey == role.Key).CurrentPathRaw ?? string.Empty));

            using var key = Registry.CurrentUser.CreateSubKey(_userSchemes.Path, writable: true);
            if (key is null) return OperationResult.Fail("Could not open the cursor schemes key.");

            key.SetValue(trimmed, value,
                value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);

            TrySetSchemeName(trimmed);
            return OperationResult.Ok($"Saved cursor scheme {trimmed}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not save the scheme: {ex.Message}");
        }
    }

    public OperationResult DeleteScheme(string name)
    {
        if (ListSchemes().Any(s => s.IsSystemScheme && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return OperationResult.Fail($"{name} ships with Windows and cannot be deleted.");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_userSchemes.Path, writable: true);
            if (key is null) return OperationResult.Fail($"No cursor scheme named {name}.");

            if (key.GetValue(name) is null) return OperationResult.Fail($"No cursor scheme named {name}.");

            key.DeleteValue(name, throwOnMissingValue: false);
            return OperationResult.Ok($"Deleted cursor scheme {name}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not delete the scheme: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes Windows reload the cursor values. Without this the registry is correct and the
    /// pointer on screen is unchanged, which reads to the user as the app doing nothing.
    /// </summary>
    public bool ApplyToSystem()
    {
        try
        {
            return NativeMethods.SystemParametersInfoNoParam(
                NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------- snapshot / restore --

    public Dictionary<string, string> CaptureAssignments()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cursor in LoadCursors())
            map[cursor.RoleKey] = cursor.CurrentPathRaw ?? string.Empty;

        return map;
    }

    public OperationResult RestoreAssignments(IReadOnlyDictionary<string, string> assignments)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_root, writable: true);
            if (key is null) return OperationResult.Fail($@"Could not open HKCU\{_root}.");

            var restored = 0;
            foreach (var pair in assignments)
            {
                if (CursorRoles.Find(pair.Key) is null) continue;

                WriteCursorValue(key, pair.Key, pair.Value);
                restored++;
            }

            ApplyToSystem();
            return OperationResult.Ok($"Restored {restored} cursor(s).");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not restore cursors: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- helpers --

    private static void WriteCursorValue(RegistryKey key, string roleKey, string value) =>
        key.SetValue(
            roleKey,
            value,
            value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);

    /// <summary>
    /// After changing one cursor the active scheme no longer describes what is applied.
    /// Windows itself represents that as source 0 with no name, which is what the Mouse
    /// control panel shows as a modified scheme.
    /// </summary>
    private static void MarkSchemeModified(RegistryKey key)
    {
        key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
        key.SetValue("Scheme Source", SchemeSourceNone, RegistryValueKind.DWord);
    }

    private void TrySetSchemeName(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_root, writable: true);
            key?.SetValue(string.Empty, name, RegistryValueKind.String);
        }
        catch
        {
            // Cosmetic only; the cursors themselves are already correct.
        }
    }
}
