using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using WinChime.Core.Interop;
using WinChime.Core.Model;
using WinChime.Core.Personalization;

namespace WinChime.App;

/// <summary>
/// Swaps the application's token dictionary, tints it with the Windows accent, and keeps the
/// title bar in step.
///
/// The decisions live in <see cref="ThemeService"/> and <see cref="AccentTheme"/> in Core, where
/// they are unit tested. What is left here is the part that genuinely needs WPF: replacing a
/// merged dictionary, and calling DWM for the non-client area.
///
/// Two things make the swap work at runtime rather than only at startup:
///
/// - Every style resolves its brushes with DynamicResource. StaticResource would resolve once
///   at load, so a swap would appear to work for controls created afterwards and not for the
///   ones already on screen.
/// - The accent brushes are written straight into Application.Resources rather than into the
///   merged dictionary. Resources set directly on a dictionary win over its merged children, so
///   this overrides the placeholder values in the token files without editing them.
/// </summary>
public static class ThemeManager
{
    private const string LightTokens = "/Theme/Tokens.Light.xaml";
    private const string DarkTokens = "/Theme/Tokens.Dark.xaml";
    private const string HighContrastTokens = "/Theme/Tokens.HighContrast.xaml";

    /// <summary>
    /// The keys ApplyAccent writes directly into Application.Resources. ThemeTokenTests checks
    /// these still exist in the token files, since a rename there would leave this writing to
    /// keys nothing reads and the app would quietly stop following the accent.
    /// </summary>
    private static readonly string[] AccentKeys =
    [
        "Accent.Fill", "Accent.Hover", "Accent.Pressed", "Accent.Text", "Accent.Subtle", "Row.Selected",
    ];

    private static readonly ThemeService Service = new();

    private static ResourceDictionary? _tokens;
    private static RegistryWatcher? _watcher;

    /// <summary>The theme currently being drawn.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>What the user asked for, which may be <see cref="ThemePreference.System"/>.</summary>
    public static ThemePreference Preference { get; private set; } = ThemePreference.System;

    /// <summary>
    /// True when Windows is in a high contrast scheme. The accent tint and the designed palette
    /// are both suppressed in that case: overriding a user's high contrast colours with a
    /// hand-picked palette is an accessibility regression, not a cosmetic choice.
    /// </summary>
    private static bool IsHighContrast => SystemParameters.HighContrast;

    // ------------------------------------------------------------------ startup --

    /// <summary>
    /// Called once, before the first window is shown. Not called on any of the headless paths,
    /// which never create a window and would only pay to load dictionaries they cannot use.
    /// </summary>
    public static void Initialise()
    {
        Preference = Service.GetPreference();
        Apply(Service.Resolve(Preference));
        StartWatchingWindows();
    }

    /// <summary>
    /// Follows the Windows theme live, so flipping Windows to dark with WinChime open moves the
    /// app with it instead of leaving it stale until the next launch.
    /// </summary>
    private static void StartWatchingWindows()
    {
        if (_watcher is not null) return;

        // The key holds transparency and accent flags too, so this fires for changes that are
        // nothing to do with light and dark. Apply is a no-op when the resolved theme is
        // unchanged, so the extra notifications cost nothing.
        _watcher = new RegistryWatcher(RegistryHive.CurrentUser, Service.PersonalizePath, watchSubtree: false);

        _watcher.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Preference == ThemePreference.System) Apply(Service.GetSystemTheme());
        });

        _watcher.Start();
    }

    public static void Shutdown()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // ------------------------------------------------------------------ applying --

    /// <summary>
    /// Stores the choice and applies it. Never writes the Windows theme.
    ///
    /// The theme is applied even when saving the preference failed: the user asked to see it,
    /// and a registry write they cannot control is no reason to refuse. The failure is returned
    /// so the caller can say it will not be remembered next time.
    /// </summary>
    public static OperationResult SetPreference(ThemePreference preference)
    {
        var saved = Service.SetPreference(preference);

        Preference = preference;
        Apply(Service.Resolve(preference));

        return saved;
    }

    private static void Apply(AppTheme theme)
    {
        // The very first call has to run even though Current already reads Light, because
        // nothing has been merged yet.
        if (_tokens is not null && theme == Current) return;

        Current = theme;

        ApplyTokens(theme);
        ApplyAccent(theme);
        ApplyTitleBars(repaint: true);
    }

    private static void ApplyTokens(AppTheme theme)
    {
        var source = IsHighContrast
            ? HighContrastTokens
            : theme == AppTheme.Dark ? DarkTokens : LightTokens;

        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;

        // Replaced in place, never inserted alongside. Among merged dictionaries the LAST one
        // holding a key wins, so adding a second token dictionary in front of the one App.xaml
        // declares would leave the original still winning and the swap would silently do
        // nothing. On the first call the entry being replaced is the light set from App.xaml.
        var existing = _tokens is null ? FindTokenDictionary(merged) : merged.IndexOf(_tokens);

        if (existing >= 0) merged[existing] = replacement;
        else merged.Add(replacement);

        _tokens = replacement;
    }

    private static int FindTokenDictionary(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString;

            if (source is not null && source.Contains("/Theme/Tokens.", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Tints the app with the user's own Windows accent.
    ///
    /// Fitting for an app whose job is editing that accent, and the shades come from
    /// <see cref="AccentTheme"/>, which guarantees the result stays readable for any accent the
    /// user might have set. A failed read is not an error: the token files carry a designed
    /// fallback, so the app simply keeps that.
    /// </summary>
    private static void ApplyAccent(AppTheme theme)
    {
        var resources = Application.Current.Resources;

        // High contrast defers to SystemColors entirely, and a machine with no recorded accent
        // keeps the designed fallback from the token file. Both cases have to REMOVE any
        // override left by an earlier call rather than just skipping: an override written
        // directly into Application.Resources outranks the token dictionary, so leaving a stale
        // one behind would shadow the values that should now be showing.
        var accent = IsHighContrast ? null : new AccentColorService().GetCurrent();

        if (accent is null)
        {
            foreach (var key in AccentKeys) resources.Remove(key);
            return;
        }

        var resolved = AccentTheme.For(accent.Value, theme);

        resources["Accent.Fill"] = Frozen(resolved.Fill);
        resources["Accent.Hover"] = Frozen(resolved.Hover);
        resources["Accent.Pressed"] = Frozen(resolved.Pressed);
        resources["Accent.Text"] = Frozen(resolved.Foreground);

        // The selection wash. Mixing the accent most of the way into the card keeps a selected
        // row obviously selected while leaving Text.Primary on it as readable as it is on the
        // card itself, which a full strength accent fill would not.
        var card = (resources["Surface.Card"] as SolidColorBrush)?.Color ?? Colors.White;
        var wash = Mix(ToColor(resolved.Fill), card, theme == AppTheme.Dark ? 0.82 : 0.88);

        resources["Accent.Subtle"] = Frozen(wash);
        resources["Row.Selected"] = Frozen(wash);
    }

    // ----------------------------------------------------------------- title bar --

    /// <summary>
    /// Hooks a window so its title bar matches. Called from each window's constructor; the
    /// handle does not exist yet at that point, which is why this waits for SourceInitialized.
    /// </summary>
    public static void Track(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window, repaint: false);
    }

    private static void ApplyTitleBars(bool repaint)
    {
        if (Application.Current is null) return;

        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window, repaint);
        }
    }

    private static void ApplyTitleBar(Window window, bool repaint)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // High contrast draws its own caption; leaving it alone is correct there.
        TitleBarTheme.Apply(hwnd, dark: !IsHighContrast && Current == AppTheme.Dark, repaint);
    }

    // ------------------------------------------------------------------- colours --

    private static Color ToColor(AccentRgb colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    private static SolidColorBrush Frozen(AccentRgb colour) => Frozen(ToColor(colour));

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
