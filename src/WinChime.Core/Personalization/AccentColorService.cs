using Microsoft.Win32;
using WinChime.Core.Interop;
using WinChime.Core.Model;

namespace WinChime.Core.Personalization;

/// <summary>The three unrelated HKCU locations a single accent colour is spread across.</summary>
public sealed record AccentRegistryPaths(string Accent, string Dwm, string Personalize)
{
    public static AccentRegistryPaths Default { get; } = new(
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
        @"Software\Microsoft\Windows\DWM",
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
}

public sealed record AccentState(AccentRgb? Accent, bool ColorPrevalence, bool TransparencyEnabled);

/// <summary>
/// Reads and writes the Windows accent colour.
///
/// One colour, three keys, and no documentation for any of it. What made this tractable was
/// checking the registry against Windows.UI.ViewManagement.UISettings rather than trusting
/// the registry alone — which immediately showed that DWM\AccentColor on the sampled machine
/// held a stale blue while the real accent was green. Reading the accent from
/// AccentPalette[3] rather than from DWM is a direct consequence.
///
/// Everything here is per-user and needs no elevation.
/// </summary>
public sealed class AccentColorService
{
    private readonly AccentRegistryPaths _paths;

    public AccentColorService(AccentRegistryPaths? paths = null) => _paths = paths ?? AccentRegistryPaths.Default;

    /// <summary>Windows' own accent swatches, as offered in Settings.</summary>
    public static IReadOnlyList<AccentRgb> Presets { get; } =
    [
        new(0xFF, 0xB9, 0x00), new(0xFF, 0x8C, 0x00), new(0xF7, 0x63, 0x0C), new(0xCA, 0x50, 0x10),
        new(0xDA, 0x3B, 0x01), new(0xEF, 0x69, 0x50), new(0xD1, 0x34, 0x38), new(0xFF, 0x40, 0x81),
        new(0xE3, 0x00, 0x8C), new(0xBF, 0x00, 0x77), new(0xC2, 0x39, 0xB3), new(0x9A, 0x00, 0x89),
        new(0x00, 0x78, 0xD7), new(0x00, 0x63, 0xB1), new(0x8E, 0x8C, 0xD8), new(0x6B, 0x69, 0xD6),
        new(0x00, 0x99, 0xBC), new(0x2D, 0x7D, 0x9A), new(0x00, 0xB7, 0xC3), new(0x03, 0x83, 0x87),
        new(0x00, 0xB2, 0x94), new(0x01, 0x80, 0x74), new(0x00, 0xCC, 0x6A), new(0x10, 0x89, 0x3E),
        new(0x74, 0x74, 0x74), new(0x6D, 0x72, 0x78), new(0x4C, 0x4A, 0x48), new(0x69, 0x79, 0x7E),
    ];

    // ---------------------------------------------------------------- reading --

    public AccentState GetState()
    {
        using var accentKey = Registry.CurrentUser.OpenSubKey(_paths.Accent);
        using var personalizeKey = Registry.CurrentUser.OpenSubKey(_paths.Personalize);

        // Read from the palette, not DWM. DWM\AccentColor can be stale: on the machine this
        // was developed against it held a blue while the accent in use was green.
        var accent = AccentPalette.AccentFromBytes(accentKey?.GetValue("AccentPalette") as byte[]);

        return new AccentState(
            accent,
            ReadFlag(personalizeKey, "ColorPrevalence"),
            ReadFlag(personalizeKey, "EnableTransparency"));
    }

    public AccentRgb? GetCurrent() => GetState().Accent;

    // ---------------------------------------------------------------- writing --

    /// <param name="showOnSurfaces">
    /// Windows calls this "Show accent colour on Start and taskbar" and on title bars.
    /// Null leaves the current setting alone.
    /// </param>
    public OperationResult Apply(AccentRgb accent, bool? showOnSurfaces = null)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(_paths.Accent, writable: true))
            {
                if (key is null) return OperationResult.Fail($@"Could not open HKCU\{_paths.Accent}.");

                key.SetValue("AccentPalette", AccentPalette.ToBytes(accent), RegistryValueKind.Binary);
                key.SetValue("AccentColorMenu", unchecked((int)ToAbgr(AccentPalette.MenuShade(accent))), RegistryValueKind.DWord);
                key.SetValue("StartColorMenu", unchecked((int)ToAbgr(AccentPalette.StartShade(accent))), RegistryValueKind.DWord);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(_paths.Dwm, writable: true))
            {
                if (key is null) return OperationResult.Fail($@"Could not open HKCU\{_paths.Dwm}.");

                // These two use different byte orders for the same colour, which is a genuine
                // Windows quirk rather than a mistake here.
                key.SetValue("AccentColor", unchecked((int)ToAbgr(accent)), RegistryValueKind.DWord);
                key.SetValue("ColorizationColor", unchecked((int)ToArgb(accent, 0xC4)), RegistryValueKind.DWord);
                key.SetValue("ColorizationAfterglow", unchecked((int)ToArgb(accent, 0xC4)), RegistryValueKind.DWord);
            }

            if (showOnSurfaces is { } prevalence)
            {
                using var personalize = Registry.CurrentUser.CreateSubKey(_paths.Personalize, writable: true);
                personalize?.SetValue("ColorPrevalence", prevalence ? 1 : 0, RegistryValueKind.DWord);

                using var dwm = Registry.CurrentUser.CreateSubKey(_paths.Dwm, writable: true);
                dwm?.SetValue("ColorPrevalence", prevalence ? 1 : 0, RegistryValueKind.DWord);
            }

            NotifyColourChanged();

            return OperationResult.Ok(
                $"Accent colour set to {accent.Hex}. Some surfaces update immediately; the Start menu and " +
                "taskbar may need a sign-out, since Windows caches the colour there.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail("Access denied writing the accent colour.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not set the accent colour: {ex.Message}");
        }
    }

    /// <summary>Everything needed to put the accent back, for backups and undo.</summary>
    public Dictionary<string, string> CaptureAssignments()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var state = GetState();

        if (state.Accent is { } accent) map["Accent"] = accent.Hex;
        map["ColorPrevalence"] = state.ColorPrevalence ? "1" : "0";

        return map;
    }

    public OperationResult RestoreAssignments(IReadOnlyDictionary<string, string> assignments)
    {
        if (!assignments.TryGetValue("Accent", out var hex) || !AccentRgb.TryParse(hex, out var accent))
            return OperationResult.Fail("That snapshot contains no accent colour.");

        bool? prevalence = assignments.TryGetValue("ColorPrevalence", out var flag) ? flag == "1" : null;

        return Apply(accent, prevalence);
    }

    // ----------------------------------------------------------------- helpers --

    /// <summary>
    /// Asks running applications to re-read the colour settings.
    ///
    /// This is best effort by nature. Desktop windows and most apps pick the change up, but
    /// Start and the taskbar cache the accent and generally need a sign-out. Saying so in the
    /// result beats letting the user conclude nothing happened.
    /// </summary>
    private static void NotifyColourChanged()
    {
        foreach (var setting in new[] { "ImmersiveColorSet", "WindowsThemeElement", "Policy" })
        {
            NativeMethods.SendMessageTimeout(
                NativeMethods.HwndBroadcast,
                NativeMethods.WM_SETTINGCHANGE,
                IntPtr.Zero,
                setting,
                NativeMethods.SMTO_ABORTIFHUNG,
                1000,
                out _);
        }

        NativeMethods.SendMessageTimeout(
            NativeMethods.HwndBroadcast,
            NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED,
            IntPtr.Zero,
            null,
            NativeMethods.SMTO_ABORTIFHUNG,
            1000,
            out _);
    }

    /// <summary>0xAABBGGRR, which is what Explorer and DWM\AccentColor use.</summary>
    private static uint ToAbgr(AccentRgb c, byte alpha = 0xFF) =>
        (uint)((alpha << 24) | (c.B << 16) | (c.G << 8) | c.R);

    /// <summary>0xAARRGGBB, which is what ColorizationColor uses. Same colour, opposite order.</summary>
    private static uint ToArgb(AccentRgb c, byte alpha) =>
        (uint)((alpha << 24) | (c.R << 16) | (c.G << 8) | c.B);

    private static bool ReadFlag(RegistryKey? key, string name) => key?.GetValue(name) is int value && value != 0;
}
