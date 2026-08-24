namespace WinChime.Core.Model;

/// <summary>
/// One assignable sound slot, e.g. ".Default\Notification.Default".
/// Mirrors HKCU\AppEvents\Schemes\Apps\{AppKey}\{EventKey}.
/// </summary>
public sealed class SoundEvent
{
    public required string AppKey { get; init; }
    public required string AppDisplayName { get; init; }
    public required string EventKey { get; init; }
    public required string EventDisplayName { get; init; }

    /// <summary>Value of the .Current subkey, unexpanded (may contain %SystemRoot%).</summary>
    public string? CurrentPathRaw { get; set; }

    /// <summary>Value of the .Default subkey: what Windows shipped with.</summary>
    public string? DefaultPathRaw { get; init; }

    public string? CurrentPath => Expand(CurrentPathRaw);

    public string? DefaultPath => Expand(DefaultPathRaw);

    public bool HasSound => !string.IsNullOrWhiteSpace(CurrentPathRaw);

    /// <summary>True when the current assignment differs from the Windows default.</summary>
    public bool IsCustomised => !PathEquals(CurrentPathRaw, DefaultPathRaw);

    /// <summary>True when a sound is assigned but the file is gone (silent failure in Windows).</summary>
    public bool IsBroken
    {
        get
        {
            var p = CurrentPath;
            return !string.IsNullOrWhiteSpace(p) && !File.Exists(p);
        }
    }

    public string DisplayLabel => $"{AppDisplayName} • {EventDisplayName}";

    /// <summary>
    /// What a screen reader announces when focus reaches this row.
    ///
    /// A list row has no automation name of its own, so it falls back to whatever ToString
    /// returns. Without this the Sounds tab reads as seventy-two repetitions of
    /// "WinChime.Core.Model.SoundEvent", which is the sort of thing that is invisible on
    /// screen and useless through a screen reader.
    ///
    /// Kept to the identity rather than the whole row: the grid cells are exposed separately
    /// and already carry the file and the status, so repeating them here would only make
    /// every row longer to listen to.
    ///
    /// Deliberately not <see cref="DisplayLabel"/>, for a cousin of the reason the CLI avoids
    /// it. The bullet is read out, and "Windows bullet Alarm 1" is noise in a list being
    /// scanned a row at a time.
    /// </summary>
    public override string ToString() => $"{AppDisplayName}, {EventDisplayName}";

    private static string? Expand(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value);

    private static bool PathEquals(string? a, string? b)
    {
        var ea = Expand(a) ?? string.Empty;
        var eb = Expand(b) ?? string.Empty;
        return string.Equals(ea.Trim(), eb.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File name only, for the list view.</summary>
    public string SoundFileName
    {
        get
        {
            var p = CurrentPath;
            if (string.IsNullOrWhiteSpace(p)) return "(silent)";
            try { return Path.GetFileName(p); } catch { return p; }
        }
    }

    /// <summary>Short state label shown in the Status column.</summary>
    public string StatusText
    {
        get
        {
            if (IsBroken) return "Missing";
            if (IsCustomised) return "Custom";
            return "Default";
        }
    }
}
