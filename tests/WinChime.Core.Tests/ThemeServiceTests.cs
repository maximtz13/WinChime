using Microsoft.Win32;
using WinChime.Core.Personalization;

namespace WinChime.Core.Tests;

/// <summary>
/// Exercises theme resolution against a real throwaway registry subtree rather than a mock.
/// The interesting cases here are all registry semantics — a value that is absent versus zero,
/// a value written with the wrong type, two similarly named values that mean different things —
/// which is precisely what a mock would paper over.
/// </summary>
public sealed class ThemeServiceTests : IDisposable
{
    private readonly ScratchRegistry _reg = new();
    private readonly ThemeService _service;

    private readonly string _personalizePath;
    private readonly string _preferencePath;

    public ThemeServiceTests()
    {
        _personalizePath = $@"{_reg.Root}\Personalize";
        _preferencePath = $@"{_reg.Root}\WinChime";

        _service = new ThemeService(new ThemeRegistryPaths(_personalizePath, _preferencePath));
    }

    public void Dispose() => _reg.Dispose();

    // ------------------------------------------------------------ system theme --

    /// <summary>
    /// The load-bearing case. On an install where nobody has opened the theme settings the
    /// value does not exist, and Windows renders light. Absence therefore has to mean light,
    /// not "unknown" and not a coin flip.
    /// </summary>
    [Fact]
    public void GetSystemTheme_WithNoKeyAtAll_IsLight()
    {
        Assert.Equal(AppTheme.Light, _service.GetSystemTheme());
    }

    [Fact]
    public void GetSystemTheme_WithKeyButNoValue_IsLight()
    {
        Registry.CurrentUser.CreateSubKey(_personalizePath)?.Dispose();

        Assert.Equal(AppTheme.Light, _service.GetSystemTheme());
    }

    [Theory]
    [InlineData(1, AppTheme.Light)]
    [InlineData(0, AppTheme.Dark)]
    public void GetSystemTheme_FollowsTheWindowsFlag(int flag, AppTheme expected)
    {
        SeedAppsUseLightTheme(flag);

        Assert.Equal(expected, _service.GetSystemTheme());
    }

    /// <summary>
    /// AppsUseLightTheme and SystemUsesLightTheme are independent, and a dark taskbar with
    /// light apps is a normal configuration that Settings offers directly ("Custom"). Reading
    /// the wrong one would render this app dark on a machine whose apps are all light, so the
    /// distinction is pinned here.
    /// </summary>
    [Fact]
    public void GetSystemTheme_IgnoresTheSystemFlagAndReadsTheAppsFlag()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_personalizePath))
        {
            key?.SetValue("SystemUsesLightTheme", 0, RegistryValueKind.DWord);
            key?.SetValue(ThemeService.AppsUseLightThemeValue, 1, RegistryValueKind.DWord);
        }

        Assert.Equal(AppTheme.Light, _service.GetSystemTheme());
    }

    /// <summary>
    /// Nothing stops another tweaking tool writing a REG_SZ where Windows writes a REG_DWORD.
    /// Falling back to light beats throwing on startup over a cosmetic setting.
    /// </summary>
    [Fact]
    public void GetSystemTheme_WithAValueOfTheWrongType_IsLight()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_personalizePath))
        {
            key?.SetValue(ThemeService.AppsUseLightThemeValue, "0", RegistryValueKind.String);
        }

        Assert.Equal(AppTheme.Light, _service.GetSystemTheme());
    }

    // -------------------------------------------------------------- preference --

    [Fact]
    public void GetPreference_WhenNeverSet_FollowsTheSystem()
    {
        Assert.Equal(ThemePreference.System, _service.GetPreference());
    }

    [Theory]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.System)]
    public void SetPreference_RoundTrips(ThemePreference preference)
    {
        Assert.True(_service.SetPreference(preference).Success);

        Assert.Equal(preference, _service.GetPreference());
    }

    /// <summary>Stored as text so the value is legible in regedit and survives enum reordering.</summary>
    [Fact]
    public void SetPreference_StoresReadableText()
    {
        _service.SetPreference(ThemePreference.Dark);

        using var key = Registry.CurrentUser.OpenSubKey(_preferencePath);

        Assert.Equal("Dark", key?.GetValue(ThemeService.PreferenceValueName));
        Assert.Equal(RegistryValueKind.String, key?.GetValueKind(ThemeService.PreferenceValueName));
    }

    [Fact]
    public void SetPreference_CreatesTheKeyWhenItIsMissing()
    {
        Assert.Null(Registry.CurrentUser.OpenSubKey(_preferencePath));

        Assert.True(_service.SetPreference(ThemePreference.Light).Success);
        Assert.Equal(ThemePreference.Light, _service.GetPreference());
    }

    /// <summary>
    /// A value written by a newer build, or by hand, must not take down an older one. Anything
    /// unrecognised degrades to following the system rather than to whichever theme happened to
    /// sit at enum value zero.
    /// </summary>
    [Fact]
    public void GetPreference_WithAnUnrecognisedValue_FollowsTheSystem()
    {
        SeedPreference("HighContrastNeon");

        Assert.Equal(ThemePreference.System, _service.GetPreference());
    }

    [Fact]
    public void GetPreference_IsCaseInsensitive()
    {
        SeedPreference("dark");

        Assert.Equal(ThemePreference.Dark, _service.GetPreference());
    }

    /// <summary>
    /// An empty string is a distinct case from a missing value: the value exists, so the read
    /// succeeds and it is the parse that has to reject it.
    /// </summary>
    [Fact]
    public void GetPreference_WithAnEmptyValue_FollowsTheSystem()
    {
        SeedPreference(string.Empty);

        Assert.Equal(ThemePreference.System, _service.GetPreference());
    }

    // ----------------------------------------------------------------- resolve --

    [Theory]
    [InlineData(ThemePreference.Light, AppTheme.Light)]
    [InlineData(ThemePreference.Dark, AppTheme.Dark)]
    public void Resolve_WithAnExplicitChoice_IgnoresWindows(ThemePreference preference, AppTheme expected)
    {
        // Windows set to the opposite of whatever was asked for.
        SeedAppsUseLightTheme(expected == AppTheme.Light ? 0 : 1);

        Assert.Equal(expected, _service.Resolve(preference));
    }

    [Theory]
    [InlineData(1, AppTheme.Light)]
    [InlineData(0, AppTheme.Dark)]
    public void Resolve_WithSystem_FollowsWindows(int flag, AppTheme expected)
    {
        SeedAppsUseLightTheme(flag);

        Assert.Equal(expected, _service.Resolve(ThemePreference.System));
    }

    [Fact]
    public void GetEffective_CombinesThePreferenceAndTheSystemFlag()
    {
        SeedAppsUseLightTheme(0);

        // Following the system: dark.
        Assert.Equal(AppTheme.Dark, _service.GetEffective());

        // Overridden: light, despite Windows being dark.
        _service.SetPreference(ThemePreference.Light);
        Assert.Equal(AppTheme.Light, _service.GetEffective());
    }

    /// <summary>
    /// Setting the app's preference must never write the Windows theme. An app that flipped
    /// the whole desktop to dark because someone changed its own appearance would be doing
    /// something nobody asked for.
    /// </summary>
    [Fact]
    public void SetPreference_DoesNotTouchTheWindowsFlag()
    {
        SeedAppsUseLightTheme(1);

        _service.SetPreference(ThemePreference.Dark);

        using var key = Registry.CurrentUser.OpenSubKey(_personalizePath);
        Assert.Equal(1, key?.GetValue(ThemeService.AppsUseLightThemeValue));
    }

    [Fact]
    public void PersonalizePath_IsExposedForChangeWatching()
    {
        Assert.Equal(_personalizePath, _service.PersonalizePath);
    }

    // ------------------------------------------------------------------ helpers --

    private void SeedAppsUseLightTheme(int flag)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_personalizePath);
        key?.SetValue(ThemeService.AppsUseLightThemeValue, flag, RegistryValueKind.DWord);
    }

    private void SeedPreference(string text)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_preferencePath);
        key?.SetValue(ThemeService.PreferenceValueName, text, RegistryValueKind.String);
    }
}
