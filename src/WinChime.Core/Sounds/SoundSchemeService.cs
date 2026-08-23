using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WinChime.Core.Model;

namespace WinChime.Core.Sounds;

/// <summary>
/// Read/write access to HKCU\AppEvents, which is the whole of the Windows event-sound system.
///
/// Layout, for reference:
///   AppEvents\Schemes                              (Default) = active scheme key
///   AppEvents\Schemes\Names\{scheme}               (Default) = friendly scheme name
///   AppEvents\Schemes\Apps\{app}                   (Default) = friendly app name
///   AppEvents\Schemes\Apps\{app}\{event}\.Current  (Default) = active wav path
///   AppEvents\Schemes\Apps\{app}\{event}\.Default  (Default) = the original Windows wav
///   AppEvents\Schemes\Apps\{app}\{event}\{scheme}  (Default) = that scheme's wav
///   AppEvents\EventLabels\{event}                  (Default) = friendly event name
///
/// Everything here is per-user and needs no elevation. Windows re-reads the registry each
/// time an event fires, so changes take effect immediately: no reboot, no broadcast.
/// </summary>
public sealed class SoundSchemeService
{
    /// <summary>Where Windows keeps this, relative to HKCU.</summary>
    public const string DefaultRegistryRoot = "AppEvents";

    public const string WindowsDefaultScheme = ".Default";
    public const string NoSoundsScheme = ".None";

    private readonly string _root;

    private string SchemesPath => $@"{_root}\Schemes";
    private string AppsPath => $@"{_root}\Schemes\Apps";
    private string NamesPath => $@"{_root}\Schemes\Names";
    private string LabelsPath => $@"{_root}\EventLabels";

    public SoundSchemeService() : this(DefaultRegistryRoot) { }

    /// <summary>
    /// Points the service at an alternative HKCU subtree. This exists so the test suite can
    /// exercise the real registry code against a scratch key rather than mocking it away
    /// or, worse, rewriting the sound settings of whoever runs the tests.
    /// </summary>
    public SoundSchemeService(string registryRoot)
    {
        if (string.IsNullOrWhiteSpace(registryRoot))
            throw new ArgumentException("Registry root must not be empty.", nameof(registryRoot));

        _root = registryRoot.Trim('\\');
    }

    // ---------------------------------------------------------------- reading --

    public IReadOnlyList<SoundEvent> LoadEvents()
    {
        var results = new List<SoundEvent>();
        var labels = LoadEventLabels();

        using var apps = Registry.CurrentUser.OpenSubKey(AppsPath);
        if (apps is null) return results;

        foreach (var appKey in apps.GetSubKeyNames())
        {
            using var app = apps.OpenSubKey(appKey);
            if (app is null) continue;

            var appDisplay = ReadDefaultString(app) ?? Prettify(appKey);
            if (appDisplay.StartsWith('@')) appDisplay = Prettify(appKey);

            foreach (var eventKey in app.GetSubKeyNames())
            {
                using var evt = app.OpenSubKey(eventKey);
                if (evt is null) continue;

                using var current = evt.OpenSubKey(".Current");
                using var def = evt.OpenSubKey(".Default");

                // An event with neither .Current nor .Default is scheme-storage residue,
                // not something the user can assign. Skip it.
                if (current is null && def is null) continue;

                results.Add(new SoundEvent
                {
                    AppKey = appKey,
                    AppDisplayName = appDisplay,
                    EventKey = eventKey,
                    EventDisplayName = labels.TryGetValue(eventKey, out var lbl) ? lbl : Prettify(eventKey),
                    CurrentPathRaw = current is null ? null : ReadDefaultString(current),
                    DefaultPathRaw = def is null ? null : ReadDefaultString(def),
                });
            }
        }

        return results
            .OrderBy(e => e.AppKey == WindowsDefaultScheme ? 0 : 1)
            .ThenBy(e => e.AppDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.EventDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private Dictionary<string, string> LoadEventLabels()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var labels = Registry.CurrentUser.OpenSubKey(LabelsPath);
        if (labels is null) return map;

        foreach (var name in labels.GetSubKeyNames())
        {
            using var k = labels.OpenSubKey(name);
            var display = k is null ? null : ReadDefaultString(k);

            // Some labels are unresolved resource references such as "@mmres.dll,-5865".
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@'))
                map[name] = display;
        }

        return map;
    }

    public string GetActiveSchemeKey()
    {
        using var schemes = Registry.CurrentUser.OpenSubKey(SchemesPath);
        return (schemes is null ? null : ReadDefaultString(schemes)) ?? WindowsDefaultScheme;
    }

    public IReadOnlyList<SchemeListItem> ListSchemes()
    {
        var list = new List<SchemeListItem>();
        using var names = Registry.CurrentUser.OpenSubKey(NamesPath);
        if (names is null) return list;

        foreach (var key in names.GetSubKeyNames())
        {
            using var k = names.OpenSubKey(key);
            var display = (k is null ? null : ReadDefaultString(k)) ?? Prettify(key);

            // Windows stores the two built-ins as resource references.
            if (display.StartsWith('@'))
            {
                display = key switch
                {
                    WindowsDefaultScheme => "Windows Default",
                    NoSoundsScheme => "No Sounds",
                    _ => Prettify(key),
                };
            }

            var builtIn = key is WindowsDefaultScheme or NoSoundsScheme;
            list.Add(new SchemeListItem(key, display, builtIn));
        }

        return list
            .OrderBy(s => s.Key == WindowsDefaultScheme ? 0 : s.Key == NoSoundsScheme ? 1 : 2)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- writing --

    /// <summary>Assign a wav to one event. Pass null or empty to silence the event.</summary>
    public OperationResult SetSound(string appKey, string eventKey, string? wavPath)
    {
        try
        {
            var path = $@"{AppsPath}\{appKey}\{eventKey}\.Current";
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            if (key is null) return OperationResult.Fail($"Could not open {path}.");

            WriteDefaultString(key, wavPath ?? string.Empty);

            return OperationResult.Ok(string.IsNullOrEmpty(wavPath)
                ? $"Silenced {eventKey}."
                : $"Assigned {Path.GetFileName(wavPath)} to {eventKey}.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(@"Access denied writing to HKCU\AppEvents.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public OperationResult RestoreDefault(string appKey, string eventKey)
    {
        using var evt = Registry.CurrentUser.OpenSubKey($@"{AppsPath}\{appKey}\{eventKey}");
        using var def = evt?.OpenSubKey(".Default");
        var original = def is null ? null : ReadDefaultString(def);

        if (original is null)
            return OperationResult.Fail($"{eventKey} has no recorded Windows default.");

        return SetSound(appKey, eventKey, original);
    }

    /// <summary>
    /// Switch the system to a stored scheme the way the Sound control panel does: copy every
    /// event's {scheme} subkey into its .Current, then stamp the active scheme name.
    /// </summary>
    public OperationResult ApplyScheme(string schemeKey)
    {
        try
        {
            var applied = 0;
            var skipped = 0;

            using var apps = Registry.CurrentUser.OpenSubKey(AppsPath, writable: true);
            if (apps is null) return OperationResult.Fail(@"AppEvents\Schemes\Apps is missing.");

            foreach (var appKey in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(appKey, writable: true);
                if (app is null) continue;

                foreach (var eventKey in app.GetSubKeyNames())
                {
                    using var evt = app.OpenSubKey(eventKey, writable: true);
                    if (evt is null) continue;

                    string value;
                    if (schemeKey == NoSoundsScheme)
                    {
                        // "No Sounds" has no per-event subkeys; it just means silence everything.
                        value = string.Empty;
                    }
                    else
                    {
                        using var src = evt.OpenSubKey(schemeKey);

                        // Leave events this scheme does not mention alone rather than
                        // silencing them: a partial scheme should not wipe the rest.
                        if (src is null) { skipped++; continue; }

                        value = ReadDefaultString(src) ?? string.Empty;
                    }

                    using var current = evt.CreateSubKey(".Current", writable: true);
                    if (current is null) continue;

                    WriteDefaultString(current, value);
                    applied++;
                }
            }

            SetActiveSchemeKey(schemeKey);

            var msg = $"Applied scheme to {applied} event(s).";
            if (skipped > 0)
                msg += $" {skipped} event(s) had no entry in this scheme and were left unchanged.";

            return OperationResult.Ok(msg);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Failed to apply scheme: {ex.Message}");
        }
    }

    public OperationResult SaveCurrentAsScheme(string schemeName)
    {
        // Check the raw input against the reserved keys BEFORE sanitising. Sanitisation
        // strips the leading dot, so ".Default" would become "Default" and slip past a
        // check made afterwards: the user would silently get a scheme named "Default"
        // instead of being told the name is reserved.
        var trimmed = schemeName.Trim();
        if (trimmed.Equals(WindowsDefaultScheme, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(NoSoundsScheme, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail($"{trimmed} is reserved by Windows and cannot be overwritten.");
        }

        var schemeKey = SanitiseSchemeKey(schemeName);
        if (string.IsNullOrWhiteSpace(schemeKey))
            return OperationResult.Fail("Scheme name is empty or contains only unusable characters.");

        try
        {
            var saved = 0;

            using var apps = Registry.CurrentUser.OpenSubKey(AppsPath, writable: true);
            if (apps is null) return OperationResult.Fail(@"AppEvents\Schemes\Apps is missing.");

            foreach (var appKey in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(appKey, writable: true);
                if (app is null) continue;

                foreach (var eventKey in app.GetSubKeyNames())
                {
                    using var evt = app.OpenSubKey(eventKey, writable: true);
                    using var current = evt?.OpenSubKey(".Current");
                    if (evt is null || current is null) continue;

                    using var dest = evt.CreateSubKey(schemeKey, writable: true);
                    if (dest is null) continue;

                    WriteDefaultString(dest, ReadDefaultString(current) ?? string.Empty);
                    saved++;
                }
            }

            using (var names = Registry.CurrentUser.CreateSubKey($@"{NamesPath}\{schemeKey}", writable: true))
            {
                if (names is not null) WriteDefaultString(names, schemeName.Trim());
            }

            SetActiveSchemeKey(schemeKey);
            return OperationResult.Ok($"Saved scheme {schemeName} with {saved} event(s).");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Failed to save scheme: {ex.Message}");
        }
    }

    public OperationResult DeleteScheme(string schemeKey)
    {
        if (schemeKey is WindowsDefaultScheme or NoSoundsScheme)
            return OperationResult.Fail("The built-in Windows schemes cannot be deleted.");

        try
        {
            using (var apps = Registry.CurrentUser.OpenSubKey(AppsPath, writable: true))
            {
                if (apps is not null)
                {
                    foreach (var appKey in apps.GetSubKeyNames())
                    {
                        using var app = apps.OpenSubKey(appKey, writable: true);
                        if (app is null) continue;

                        foreach (var eventKey in app.GetSubKeyNames())
                        {
                            using var evt = app.OpenSubKey(eventKey, writable: true);
                            evt?.DeleteSubKeyTree(schemeKey, throwOnMissingSubKey: false);
                        }
                    }
                }
            }

            using (var names = Registry.CurrentUser.OpenSubKey(NamesPath, writable: true))
            {
                names?.DeleteSubKeyTree(schemeKey, throwOnMissingSubKey: false);
            }

            if (GetActiveSchemeKey() == schemeKey) SetActiveSchemeKey(WindowsDefaultScheme);

            return OperationResult.Ok($"Deleted scheme {schemeKey}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Failed to delete scheme: {ex.Message}");
        }
    }

    public void SetActiveSchemeKey(string schemeKey)
    {
        using var schemes = Registry.CurrentUser.CreateSubKey(SchemesPath, writable: true);
        if (schemes is not null) WriteDefaultString(schemes, schemeKey);
    }

    // ------------------------------------------------ snapshot / import-export --

    /// <summary>Snapshot of every .Current value, keyed "app\event". Used by BackupService.</summary>
    public Dictionary<string, string> CaptureAssignments()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in LoadEvents())
            map[$@"{e.AppKey}\{e.EventKey}"] = e.CurrentPathRaw ?? string.Empty;
        return map;
    }

    public OperationResult RestoreAssignments(IReadOnlyDictionary<string, string> assignments)
    {
        var restored = 0;
        var failures = new List<string>();

        foreach (var pair in assignments)
        {
            var split = pair.Key.Split('\\', 2);
            if (split.Length != 2) continue;

            var result = SetSound(split[0], split[1], pair.Value);
            if (result.Success) restored++;
            else failures.Add($"{pair.Key}: {result.Message}");
        }

        return failures.Count == 0
            ? OperationResult.Ok($"Restored {restored} assignment(s).")
            : OperationResult.Fail($"Restored {restored}, failed {failures.Count}. First failure: {failures[0]}");
    }

    public SchemeExport BuildExport(string name, string? author = null, string? description = null) => new()
    {
        Name = name,
        Author = author,
        Description = description,
        Assignments = CaptureAssignments(),
    };

    public OperationResult ExportToFile(string filePath, SchemeExport export)
    {
        try
        {
            var json = JsonSerializer.Serialize(export, JsonOptions);
            File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return OperationResult.Ok(
                $"Exported {export.Assignments.Count} assignment(s) to {Path.GetFileName(filePath)}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Export failed: {ex.Message}");
        }
    }

    public (SchemeExport? Export, string? Error) ImportFromFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var export = JsonSerializer.Deserialize<SchemeExport>(json, JsonOptions);

            if (export is null) return (null, "File did not contain a scheme.");
            if (export.FormatVersion > 1)
                return (null, $"Scheme format v{export.FormatVersion} is newer than this build understands.");

            return (export, null);
        }
        catch (Exception ex)
        {
            return (null, $"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply an imported scheme. Missing audio files are reported rather than assigned,
    /// because a dead path is the most common failure when a scheme moves between PCs
    /// and Windows gives no feedback at all when an event points at a file that is gone.
    /// </summary>
    public (OperationResult Result, IReadOnlyList<string> MissingFiles) ApplyExport(
        SchemeExport export, bool skipMissingFiles = true)
    {
        var missing = new List<string>();
        var applied = 0;

        foreach (var pair in export.Assignments)
        {
            var split = pair.Key.Split('\\', 2);
            if (split.Length != 2) continue;

            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                var expanded = Environment.ExpandEnvironmentVariables(pair.Value);
                if (!File.Exists(expanded))
                {
                    missing.Add($"{pair.Key} -> {expanded}");
                    if (skipMissingFiles) continue;
                }
            }

            if (SetSound(split[0], split[1], pair.Value).Success) applied++;
        }

        var msg = $"Applied {applied} assignment(s) from {export.Name}.";
        if (missing.Count > 0)
        {
            msg += skipMissingFiles
                ? $" Skipped {missing.Count} entry(ies) whose audio file is missing on this PC."
                : $" {missing.Count} entry(ies) point at files missing on this PC.";
        }

        return (OperationResult.Ok(msg), missing);
    }

    // ----------------------------------------------------------------- helpers --

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string? ReadDefaultString(RegistryKey key) =>
        key.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    /// <summary>
    /// Mirrors what Windows itself stores: REG_EXPAND_SZ when the path contains an
    /// environment variable, REG_SZ otherwise. Writing the wrong kind works but makes
    /// the value look foreign next to the shipped defaults.
    /// </summary>
    private static void WriteDefaultString(RegistryKey key, string value) =>
        key.SetValue(
            string.Empty,
            value,
            value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);

    private static string SanitiseSchemeKey(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_') sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Turns "Notification.Looping.Alarm2" or "EmptyRecycleBin" into readable text.</summary>
    private static string Prettify(string key)
    {
        var text = key.TrimStart('.').Replace('.', ' ');
        var sb = new StringBuilder(text.Length + 8);

        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && !char.IsUpper(text[i - 1]) && text[i - 1] != ' ')
                sb.Append(' ');

            sb.Append(text[i]);
        }

        return sb.ToString();
    }
}

public sealed record SchemeListItem(string Key, string DisplayName, bool IsBuiltIn)
{
    public override string ToString() => DisplayName;
}
