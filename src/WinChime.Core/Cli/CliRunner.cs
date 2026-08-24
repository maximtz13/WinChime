using WinChime.Core.Model;
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

    public CliRunner(TextWriter output, string? registryRoot = null, string? backupRoot = null)
    {
        _out = output;
        _sounds = new SoundSchemeService(registryRoot ?? SoundSchemeService.DefaultRegistryRoot);
        _backups = new BackupService(_sounds, backupRoot);
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
        _out.WriteLine("Safety");
        _out.WriteLine("  --backup [label]                          snapshot the current assignments");
        _out.WriteLine();
        _out.WriteLine("Event names are AppKey\\EventKey, for example .Default\\SystemHand.");
        _out.WriteLine("Run --list to see them. Sounds must be uncompressed PCM .wav; the");
        _out.WriteLine("application converts other formats, the CLI does not.");

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

        // Contains alone is useless for the common case, which is a typo rather than a
        // partial name: "SystemHnad" does not contain, and is not contained by, "SystemHand".
        // Matching on a shared prefix catches transpositions and wrong endings, which is most
        // real mistakes, without implementing edit distance for a help message.
        var wanted = split[1];
        var prefix = wanted.Length >= 4 ? wanted[..4] : wanted;

        var suggestions = events
            .Where(e => e.EventKey.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                        || wanted.Contains(e.EventKey, StringComparison.OrdinalIgnoreCase)
                        || e.EventKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            // Closest first. Prefix matching alone returned SystemHand second behind
            // SystemAsterisk, which undersells a suggestion that is almost always right.
            .OrderByDescending(e => CommonPrefixLength(e.EventKey, wanted))
            .ThenBy(e => Math.Abs(e.EventKey.Length - wanted.Length))
            .Take(5)
            .Select(e => $"{e.AppKey}\\{e.EventKey}")
            .ToList();

        error = suggestions.Count > 0
            ? $"No event named {composite}. Did you mean: {string.Join(", ", suggestions)}"
            : $"No event named {composite}. Run --list to see the available events.";

        return false;
    }

    /// <summary>Length of the shared leading run of two strings, case-insensitively.</summary>
    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;

        while (i < max && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;

        return i;
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
