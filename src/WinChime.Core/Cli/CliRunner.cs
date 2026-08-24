using WinChime.Core.Cursors;
using WinChime.Core.Model;
using WinChime.Core.Personalization;
using WinChime.Core.Safety;
using WinChime.Core.Sounds;

namespace WinChime.Core.Cli;

/// <summary>
/// The command-line surface, kept entirely out of the WPF project.
///
/// Everything writes to an injected TextWriter and returns an exit code rather than
/// touching Console directly, so the whole thing is testable — which matters, because the
/// GUI paths are the ones CI cannot exercise and this is the part that can be.
///
/// The registry root is injectable for the same reason the service is: tests run against a
/// scratch subtree instead of rewriting the sound settings of whoever runs them.
/// </summary>
public sealed class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitFailed = 1;

    private readonly TextWriter _out;
    private readonly SoundSchemeService _sounds;
    private readonly BackupService _backups;
    private readonly CursorSchemeService _cursors;
    private readonly AccentColorService _accent;

    /// <param name="cursors">
    /// Injected whole rather than as a path, because a cursor service needs three registry
    /// locations and threading all of them through here would be noise.
    /// </param>
    public CliRunner(
        TextWriter output,
        string? registryRoot = null,
        string? backupRoot = null,
        CursorSchemeService? cursors = null,
        AccentColorService? accent = null)
    {
        _out = output;
        _sounds = new SoundSchemeService(registryRoot ?? SoundSchemeService.DefaultRegistryRoot);
        _backups = new BackupService(_sounds, backupRoot);
        _cursors = cursors ?? new CursorSchemeService();
        _accent = accent ?? new AccentColorService();
    }

    /// <summary>True when these arguments are meant for the CLI rather than the GUI.</summary>
    public static bool IsCliInvocation(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].StartsWith('-') && !IsInternalSwitch(args[0]);

    /// <summary>
    /// Switches the GUI handles itself. They start with a dash but must not be treated as
    /// user-facing commands, and must never appear in help.
    /// </summary>
    private static bool IsInternalSwitch(string arg) =>
        arg.Equals("--play-chime", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--elevated-op", StringComparison.OrdinalIgnoreCase);

    public int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return WriteHelp();

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();

        try
        {
            return command switch
            {
                "--help" or "-h" or "-?" => WriteHelp(),
                "--version" => WriteVersion(),
                "--list" => List(rest),
                "--get" => Get(rest),
                "--set" => Set(rest),
                "--silence" => Set(rest.Count == 1 ? new List<string> { rest[0], "" } : rest),
                "--restore-default" => RestoreDefault(rest),
                "--apply-scheme" => ApplyScheme(rest),
                "--list-schemes" => ListSchemes(),
                "--export-pack" => ExportPack(rest),
                "--apply-pack" => ApplyPack(rest),
                "--backup" => Backup(rest),
                "--list-cursors" => ListCursors(rest),
                "--get-cursor" => GetCursor(rest),
                "--set-cursor" => SetCursor(rest),
                "--system-cursor" => SetCursor(rest.Count == 1 ? new List<string> { rest[0], "" } : rest),
                "--list-cursor-schemes" => ListCursorSchemes(),
                "--apply-cursor-scheme" => ApplyCursorScheme(rest),
                "--get-accent" => GetAccent(),
                "--set-accent" => SetAccent(rest),
                "--list-accent-presets" => ListAccentPresets(),
                _ => Fail($"Unknown command: {args[0]}. Try --help.", ExitUsage),
            };
        }
        catch (Exception ex)
        {
            // A CLI that stack-traces at the user is a CLI that gets piped to /dev/null.
            return Fail(ex.Message, ExitFailed);
        }
    }

    // ------------------------------------------------------------------ commands --

    private int WriteHelp()
    {
        _out.WriteLine("WinChime - Windows sound personalisation");
        _out.WriteLine();
        _out.WriteLine("  WinChime                                  open the application");
        _out.WriteLine();
        _out.WriteLine("Inspect");
        _out.WriteLine("  --list [text]                             list sound events, optionally filtered");
        _out.WriteLine("  --list-schemes                            list installed sound schemes");
        _out.WriteLine("  --get <App\\Event>                         show one event");
        _out.WriteLine();
        _out.WriteLine("Change");
        _out.WriteLine("  --set <App\\Event> <file.wav>              assign a sound");
        _out.WriteLine("  --silence <App\\Event>                     silence an event");
        _out.WriteLine("  --restore-default <App\\Event>             restore the Windows default");
        _out.WriteLine("  --apply-scheme <name>                     switch to a stored scheme");
        _out.WriteLine();
        _out.WriteLine("Packs");
        _out.WriteLine("  --export-pack <file> [name]               write the current sounds to a pack");
        _out.WriteLine("  --apply-pack <file>                       install and apply a pack");
        _out.WriteLine();
        _out.WriteLine("Cursors");
        _out.WriteLine("  --list-cursors [text]                     list cursor roles, optionally filtered");
        _out.WriteLine("  --list-cursor-schemes                     list cursor schemes");
        _out.WriteLine("  --get-cursor <Role>                       show one cursor");
        _out.WriteLine("  --set-cursor <Role> <file.cur|.ani>       assign a cursor");
        _out.WriteLine("  --system-cursor <Role>                    let Windows draw it");
        _out.WriteLine("  --apply-cursor-scheme <name>              switch to a cursor scheme");
        _out.WriteLine();
        _out.WriteLine("Accent colour");
        _out.WriteLine("  --get-accent                              show the accent colour and its shades");
        _out.WriteLine("  --set-accent <#RRGGBB> [on|off]           set it; on/off shows it on Start and title bars");
        _out.WriteLine("  --list-accent-presets                     list the Windows swatches");
        _out.WriteLine();
        _out.WriteLine("Safety");
        _out.WriteLine("  --backup [label]                          snapshot the current assignments");
        _out.WriteLine();
        _out.WriteLine("Event names are AppKey\\EventKey, for example .Default\\SystemHand.");
        _out.WriteLine("Run --list to see them. Sounds must be uncompressed PCM .wav; the");
        _out.WriteLine("application converts other formats, the CLI does not.");
        _out.WriteLine();
        _out.WriteLine("Cursor roles are single names such as Arrow or Wait. Run --list-cursors");
        _out.WriteLine("to see them. Cursors must be .cur or .ani.");

        return ExitOk;
    }

    private int WriteVersion()
    {
        var version = typeof(CliRunner).Assembly.GetName().Version;
        _out.WriteLine($"WinChime {version?.ToString(3) ?? "unknown"}");
        return ExitOk;
    }

    private int List(IReadOnlyList<string> args)
    {
        var filter = args.Count > 0 ? args[0] : null;

        var events = _sounds.LoadEvents()
            .Where(e => filter is null
                        || e.EventKey.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || e.EventDisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || e.AppDisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (events.Count == 0)
        {
            _out.WriteLine(filter is null ? "No sound events found." : $"No sound events match \"{filter}\".");
            return ExitOk;
        }

        foreach (var e in events)
        {
            var flag = e.IsBroken ? "!" : e.IsCustomised ? "*" : " ";
            _out.WriteLine($"{flag} {e.AppKey}\\{e.EventKey,-34} {e.SoundFileName}");
        }

        _out.WriteLine();
        _out.WriteLine($"{events.Count} event(s).  * = changed from default, ! = file missing");

        return ExitOk;
    }

    private int ListSchemes()
    {
        var active = _sounds.GetActiveSchemeKey();

        foreach (var scheme in _sounds.ListSchemes())
            _out.WriteLine($"{(scheme.Key == active ? "*" : " ")} {scheme.Key,-24} {scheme.DisplayName}");

        return ExitOk;
    }

    private int Get(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --get <App\\Event>", ExitUsage);
        if (!TryFind(args[0], out var soundEvent, out var error)) return Fail(error, ExitFailed);

        // Deliberately not DisplayLabel: it contains a bullet character, and non-ASCII in
        // CLI output is at the mercy of whatever code page the console happens to be in.
        _out.WriteLine($"Event   : {soundEvent!.AppKey}\\{soundEvent.EventKey}");
        _out.WriteLine($"Source  : {soundEvent.AppDisplayName}");
        _out.WriteLine($"Name    : {soundEvent.EventDisplayName}");
        _out.WriteLine($"Sound   : {soundEvent.CurrentPath ?? "(silent)"}");
        _out.WriteLine($"Default : {soundEvent.DefaultPath ?? "(none recorded)"}");
        _out.WriteLine($"Status  : {soundEvent.StatusText}");

        if (soundEvent.HasSound && !soundEvent.IsBroken)
        {
            var info = WaveFile.Inspect(soundEvent.CurrentPath!);
            _out.WriteLine($"Format  : {info.Summary}");

            foreach (var warning in info.Warnings) _out.WriteLine($"Warning : {warning}");
        }

        return ExitOk;
    }

    private int Set(IReadOnlyList<string> args)
    {
        if (args.Count < 2) return Fail("Usage: --set <App\\Event> <file.wav>", ExitUsage);
        if (!TryFind(args[0], out var soundEvent, out var error)) return Fail(error, ExitFailed);

        var path = args[1];

        if (!string.IsNullOrEmpty(path))
        {
            if (!File.Exists(path)) return Fail($"File not found: {path}", ExitFailed);

            var info = WaveFile.Inspect(path);

            // The GUI offers to convert here. The CLI refuses instead of assigning something
            // that would play silently, because a script has nobody to ask.
            if (!info.IsValid) return Fail(info.Error ?? "Unreadable audio file.", ExitFailed);

            if (!info.IsPlayableByWindows)
            {
                return Fail(
                    $"{Path.GetFileName(path)} is {info.FormatName}, not uncompressed PCM. Windows would " +
                    "accept it and then play nothing. Convert it first, or use the application, which offers to.",
                    ExitFailed);
            }

            path = Path.GetFullPath(path);
        }

        return Report(_sounds.SetSound(soundEvent!.AppKey, soundEvent.EventKey, path));
    }

    private int RestoreDefault(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --restore-default <App\\Event>", ExitUsage);
        if (!TryFind(args[0], out var soundEvent, out var error)) return Fail(error, ExitFailed);

        return Report(_sounds.RestoreDefault(soundEvent!.AppKey, soundEvent.EventKey));
    }

    private int ApplyScheme(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --apply-scheme <name>", ExitUsage);

        var requested = args[0];
        var scheme = _sounds.ListSchemes().FirstOrDefault(s =>
            s.Key.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || s.DisplayName.Equals(requested, StringComparison.OrdinalIgnoreCase));

        if (scheme is null)
        {
            return Fail(
                $"No scheme named \"{requested}\". Run --list-schemes to see what is installed.",
                ExitFailed);
        }

        // Same automatic safety net the GUI has. A script applying a scheme unattended is
        // exactly the case where an undo path matters most.
        var (backup, _) = _backups.CreateSoundBackup($"Before applying scheme: {scheme.DisplayName}");
        if (!backup.Success) _out.WriteLine($"Warning: {backup.Message}");

        return Report(_sounds.ApplyScheme(scheme.Key));
    }

    private int ExportPack(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --export-pack <file> [name]", ExitUsage);

        var destination = args[0];
        var name = args.Count > 1 ? args[1] : Path.GetFileNameWithoutExtension(destination);

        var result = SoundPackService.Create(destination, _sounds.BuildExport(name, Environment.UserName));

        foreach (var warning in result.Warnings) _out.WriteLine($"Warning: {warning}");

        _out.WriteLine(result.Message);
        return result.Success ? ExitOk : ExitFailed;
    }

    private int ApplyPack(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --apply-pack <file>", ExitUsage);

        var (scheme, result) = SoundPackService.Install(args[0]);

        foreach (var warning in result.Warnings) _out.WriteLine($"Warning: {warning}");

        if (scheme is null || !result.Success) return Fail(result.Message, ExitFailed);

        _out.WriteLine(result.Message);

        var (backup, _) = _backups.CreateSoundBackup($"Before applying pack: {scheme.Name}");
        if (!backup.Success) _out.WriteLine($"Warning: {backup.Message}");

        var (applied, missing) = _sounds.ApplyExport(scheme);

        foreach (var entry in missing) _out.WriteLine($"Skipped: {entry}");

        return Report(applied);
    }

    private int Backup(IReadOnlyList<string> args)
    {
        var label = args.Count > 0 ? args[0] : "Command line backup";
        var (result, manifest) = _backups.CreateSoundBackup(label);

        if (result.Success && manifest is not null)
            _out.WriteLine($"{result.Message} ({manifest.Id})");
        else
            _out.WriteLine(result.Message);

        return result.Success ? ExitOk : ExitFailed;
    }

    // ------------------------------------------------------------------- cursors --

    private int ListCursors(IReadOnlyList<string> args)
    {
        var filter = args.Count > 0 ? args[0] : null;

        var cursors = _cursors.LoadCursors()
            .Where(c => filter is null
                        || c.RoleKey.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (cursors.Count == 0)
        {
            _out.WriteLine(filter is null ? "No cursors found." : $"No cursors match \"{filter}\".");
            return ExitOk;
        }

        foreach (var c in cursors)
        {
            var flag = c.IsBroken ? "!" : c.IsSystemDrawn ? " " : "*";
            _out.WriteLine($"{flag} {c.RoleKey,-14} {c.DisplayName,-24} {c.FileName}");
        }

        _out.WriteLine();
        _out.WriteLine($"{cursors.Count} cursor(s).  * = file assigned, ! = file missing, blank = drawn by Windows");

        return ExitOk;
    }

    private int ListCursorSchemes()
    {
        var active = _cursors.GetActiveSchemeName();
        var schemes = _cursors.ListSchemes();

        if (schemes.Count == 0)
        {
            _out.WriteLine("No cursor schemes found.");
            return ExitOk;
        }

        foreach (var scheme in schemes)
        {
            var marker = scheme.Name.Equals(active, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            _out.WriteLine($"{marker} {scheme.Name,-40} {(scheme.IsSystemScheme ? "(Windows)" : "(yours)")}");
        }

        _out.WriteLine();
        _out.WriteLine($"Active: {active}");

        return ExitOk;
    }

    private int GetCursor(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --get-cursor <Role>", ExitUsage);
        if (!TryFindCursor(args[0], out var cursor, out var error)) return Fail(error, ExitFailed);

        _out.WriteLine($"Role   : {cursor!.RoleKey}");
        _out.WriteLine($"Name   : {cursor.DisplayName}");
        _out.WriteLine($"File   : {cursor.CurrentPath ?? "(drawn by Windows)"}");
        _out.WriteLine($"Status : {cursor.StatusText}");

        if (!cursor.IsSystemDrawn && !cursor.IsBroken)
        {
            var info = CursorFile.Inspect(cursor.CurrentPath!);
            _out.WriteLine($"Format : {info.Summary}");

            foreach (var warning in info.Warnings) _out.WriteLine($"Warning: {warning}");
        }

        return ExitOk;
    }

    private int SetCursor(IReadOnlyList<string> args)
    {
        if (args.Count < 2) return Fail("Usage: --set-cursor <Role> <file.cur|.ani>", ExitUsage);
        if (!TryFindCursor(args[0], out var cursor, out var error)) return Fail(error, ExitFailed);

        var path = args[1];

        if (!string.IsNullOrEmpty(path))
        {
            if (!File.Exists(path)) return Fail($"File not found: {path}", ExitFailed);

            // Nothing to convert here, unlike audio, so an unusable file is simply refused.
            var info = CursorFile.Inspect(path);
            if (!info.IsValid) return Fail(info.Error ?? "Unreadable cursor file.", ExitFailed);

            path = Path.GetFullPath(path);
        }

        return Report(_cursors.SetCursor(cursor!.RoleKey, path));
    }

    private int ApplyCursorScheme(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --apply-cursor-scheme <name>", ExitUsage);

        var requested = args[0];
        var scheme = _cursors.ListSchemes().FirstOrDefault(s =>
            s.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));

        if (scheme is null)
        {
            return Fail(
                $"No cursor scheme named \"{requested}\". Run --list-cursor-schemes to see what is installed.",
                ExitFailed);
        }

        return Report(_cursors.ApplyScheme(scheme.Name));
    }

    /// <summary>
    /// Resolves a cursor role, suggesting near matches on a miss. Same reasoning as sound
    /// events: the keys are not obvious, so a bare not-found is unhelpful.
    /// </summary>
    private bool TryFindCursor(string roleName, out CursorEntry? cursor, out string error)
    {
        var cursors = _cursors.LoadCursors();

        cursor = cursors.FirstOrDefault(c => c.RoleKey.Equals(roleName, StringComparison.OrdinalIgnoreCase));
        if (cursor is not null) { error = string.Empty; return true; }

        var suggestions = NearestMatches(cursors.Select(c => c.RoleKey), roleName);

        error = suggestions.Count > 0
            ? $"No cursor role named {roleName}. Did you mean: {string.Join(", ", suggestions)}"
            : $"No cursor role named {roleName}. Run --list-cursors to see the available roles.";

        return false;
    }

    // -------------------------------------------------------------------- accent --

    private int GetAccent()
    {
        var state = _accent.GetState();

        if (state.Accent is not { } accent)
        {
            _out.WriteLine("Windows has not recorded an accent colour.");
            return ExitOk;
        }

        _out.WriteLine($"Accent      : {accent.Hex}  (R={accent.R} G={accent.G} B={accent.B})");
        _out.WriteLine($"On surfaces : {(state.ColorPrevalence ? "yes" : "no")}");
        _out.WriteLine($"Transparency: {(state.TransparencyEnabled ? "on" : "off")}");
        _out.WriteLine();
        _out.WriteLine("Shades, lightest to darkest:");

        foreach (var shade in AccentPalette.Shades(accent)) _out.WriteLine($"  {shade.Hex}");

        return ExitOk;
    }

    private int SetAccent(IReadOnlyList<string> args)
    {
        if (args.Count < 1) return Fail("Usage: --set-accent <#RRGGBB> [on|off]", ExitUsage);

        if (!AccentRgb.TryParse(args[0], out var colour))
        {
            return Fail(
                $"\"{args[0]}\" is not a colour. Use #RRGGBB, for example #0078D7, " +
                "or run --list-accent-presets.",
                ExitUsage);
        }

        bool? showOnSurfaces = null;

        if (args.Count > 1)
        {
            showOnSurfaces = args[1].ToLowerInvariant() switch
            {
                "on" or "yes" or "true" or "1" => true,
                "off" or "no" or "false" or "0" => false,
                _ => null,
            };

            if (showOnSurfaces is null)
                return Fail($"Expected on or off for the second argument, got \"{args[1]}\".", ExitUsage);
        }

        return Report(_accent.Apply(colour, showOnSurfaces));
    }

    private int ListAccentPresets()
    {
        foreach (var preset in AccentColorService.Presets) _out.WriteLine($"  {preset.Hex}");

        _out.WriteLine();
        _out.WriteLine($"{AccentColorService.Presets.Count} preset(s). Any #RRGGBB value is accepted.");

        return ExitOk;
    }

    // ------------------------------------------------------------------- helpers --

    /// <summary>
    /// Resolves "App\Event", and on a miss suggests near matches rather than just refusing.
    /// Event keys are not memorable and a bare "not found" makes the CLI hostile.
    /// </summary>
    private bool TryFind(string composite, out SoundEvent? soundEvent, out string error)
    {
        soundEvent = null;
        error = string.Empty;

        var split = composite.Split('\\', 2);
        if (split.Length != 2)
        {
            error = $"Expected AppKey\\EventKey, for example .Default\\SystemHand. Got: {composite}";
            return false;
        }

        var events = _sounds.LoadEvents();

        soundEvent = events.FirstOrDefault(e =>
            e.AppKey.Equals(split[0], StringComparison.OrdinalIgnoreCase)
            && e.EventKey.Equals(split[1], StringComparison.OrdinalIgnoreCase));

        if (soundEvent is not null) return true;

        // Contains alone is useless here: the common case is a typo rather than a partial
        // name, and "SystemHnad" neither contains nor is contained by "SystemHand".
        var wanted = split[1];

        var suggestions = NearestMatches(events.Select(e => e.EventKey), wanted)
            .Select(key => events.First(e =>
                e.EventKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{e.AppKey}\\{e.EventKey}")
            .ToList();

        error = suggestions.Count > 0
            ? $"No event named {composite}. Did you mean: {string.Join(", ", suggestions)}"
            : $"No event named {composite}. Run --list to see the available events.";

        return false;
    }

    /// <summary>
    /// Names close enough to a mistyped one to be worth suggesting, nearest first.
    ///
    /// Edit distance rather than prefix matching. A prefix heuristic handles transpositions
    /// (SystemHnad) but silently fails on a deletion: Arow and Arrow share only two leading
    /// characters, so nothing was suggested for the most obvious typo of the most common
    /// cursor. With at most a few dozen candidates the real computation costs nothing.
    /// </summary>
    private static IReadOnlyList<string> NearestMatches(IEnumerable<string> candidates, string query, int take = 5)
    {
        // Scales with the query so short names do not match everything and long ones still
        // tolerate a couple of slips.
        var threshold = Math.Max(2, query.Length / 3);

        return candidates
            .Select(name => (Name: name, Distance: EditDistance(name, query)))
            .Where(x => x.Distance <= threshold
                        || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || query.Contains(x.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>Levenshtein distance, case-insensitive, with a rolling two-row buffer.</summary>
    private static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private int Report(OperationResult result)
    {
        _out.WriteLine(result.Message);
        return result.Success ? ExitOk : ExitFailed;
    }

    private int Fail(string message, int code)
    {
        _out.WriteLine(message);
        return code;
    }
}
