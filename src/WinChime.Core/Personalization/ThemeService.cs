using Microsoft.Win32;
using WinChime.Core.Model;

namespace WinChime.Core.Personalization;

/// <summary>What the user asked for. Distinct from <see cref="AppTheme"/>, which is what gets drawn.</summary>
public enum ThemePreference
{
    /// <summary>Follow the Windows app theme, and keep following it if the user changes it.</summary>
    System,
    Light,
    Dark,
}

/// <summary>A theme that can actually be rendered. <see cref="ThemePreference.System"/> resolves to one of these.</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// Where the theme values live. Both are per-user; neither needs elevation.
/// </summary>
/// <param name="Personalize">Windows' own key, holding the app/system light-theme flags.</param>
/// <param name="Preference">WinChime's own key, holding the user's choice for this app.</param>
public sealed record ThemeRegistryPaths(string Personalize, string Preference)
{
    public static ThemeRegistryPaths Default { get; } = new(
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        @"Software\WinChime");
}

/// <summary>
/// Resolves which theme the app should draw itself in.
///
/// Two separate questions are tangled together here and it is worth keeping them apart: what
/// Windows is set to, and what the user asked WinChime for. Only the second is WinChime's to
/// store, which is why the Windows flag is read-only throughout — an app that quietly rewrote
/// the system theme because someone picked "Dark" in its own title bar would be doing
/// something nobody asked for.
///
/// Everything here is per-user and needs no elevation.
/// </summary>
public sealed class ThemeService
{
    /// <summary>
    /// Windows' flag for the theme *applications* should use. There is a sibling value,
    /// SystemUsesLightTheme, which governs the taskbar, Start and the notification centre —
    /// they are genuinely independent, and a machine with a dark taskbar and light apps is a
    /// perfectly normal configuration. An app that reads the system value instead of this one
    /// will look wrong on every such machine, so this reads AppsUseLightTheme deliberately.
    /// </summary>
    public const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>The value WinChime stores its own preference under.</summary>
    public const string PreferenceValueName = "Theme";

    private readonly ThemeRegistryPaths _paths;

    public ThemeService(ThemeRegistryPaths? paths = null) => _paths = paths ?? ThemeRegistryPaths.Default;

    /// <summary>The full Windows path being watched, for a caller that wants change notifications.</summary>
    public string PersonalizePath => _paths.Personalize;

    // ---------------------------------------------------------------- reading --

    /// <summary>
    /// What Windows currently has applications set to.
    ///
    /// A missing value means light, not "unknown". On a Windows install where nobody has ever
    /// opened the theme settings the value does not exist at all and Windows renders light, so
    /// treating absence as light matches what the user is actually looking at. Junk in the
    /// value is treated the same way rather than throwing: a wrong-but-readable window beats a
    /// crash on startup.
    /// </summary>
    public AppTheme GetSystemTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_paths.Personalize);

        // REG_DWORD reads back as int, but nothing stops another tool writing a REG_SZ here,
        // so this goes through the object rather than casting.
        return key?.GetValue(AppsUseLightThemeValue) switch
        {
            int flag => flag == 0 ? AppTheme.Dark : AppTheme.Light,
            _ => AppTheme.Light,
        };
    }

    /// <summary>
    /// The user's choice for WinChime specifically. Defaults to following Windows, which is
    /// the only defensible default for an app that has never been told otherwise.
    /// </summary>
    public ThemePreference GetPreference()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_paths.Preference);

        // Stored as text rather than a number so the value is legible in regedit and so an
        // unrecognised future name degrades to System instead of to whichever theme happened
        // to be enum value 2.
        return Enum.TryParse<ThemePreference>(key?.GetValue(PreferenceValueName) as string, ignoreCase: true, out var preference)
            ? preference
            : ThemePreference.System;
    }

    /// <summary>Turns a preference into something that can be drawn.</summary>
    public AppTheme Resolve(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => AppTheme.Light,
        ThemePreference.Dark => AppTheme.Dark,
        _ => GetSystemTheme(),
    };

    /// <summary>The theme to draw right now.</summary>
    public AppTheme GetEffective() => Resolve(GetPreference());

    // ---------------------------------------------------------------- writing --

    /// <summary>
    /// Stores the user's choice. Writes only to WinChime's own key: the Windows theme is
    /// never touched.
    /// </summary>
    public OperationResult SetPreference(ThemePreference preference)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_paths.Preference, writable: true);
            if (key is null) return OperationResult.Fail($@"Could not open HKCU\{_paths.Preference}.");

            key.SetValue(PreferenceValueName, preference.ToString(), RegistryValueKind.String);
            return OperationResult.Ok($"Theme set to {preference}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not save the theme preference: {ex.Message}");
        }
    }
}
