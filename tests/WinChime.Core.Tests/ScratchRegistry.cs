using Microsoft.Win32;
using WinChime.Core.Cursors;
using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

/// <summary>
/// A throwaway HKCU subtree that stands in for AppEvents.
///
/// The point is to exercise the real registry code rather than mock it away. Registry
/// semantics are where the bugs live here — REG_SZ versus REG_EXPAND_SZ, default values on
/// subkeys, delete-subkey-tree behaviour — and a mock would assert our assumptions about
/// those instead of the truth.
///
/// Each instance gets its own GUID-named root so tests are isolated and can run in
/// parallel, and Dispose removes the whole subtree.
/// </summary>
public sealed class ScratchRegistry : IDisposable
{
    private const string ParentPath = @"Software\WinChime.Tests";

    public string Root { get; }

    public ScratchRegistry()
    {
        Root = $@"{ParentPath}\{Guid.NewGuid():N}";
        Registry.CurrentUser.CreateSubKey(Root)?.Dispose();
    }

    public SoundSchemeService CreateService() => new(Root);

    // ------------------------------------------------------------------ seeding --

    public void SeedApp(string appKey, string displayName)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\Schemes\Apps\{appKey}");
        key?.SetValue(string.Empty, displayName, RegistryValueKind.String);
    }

    /// <summary>Creates an event with optional .Current and .Default values.</summary>
    public void SeedEvent(string appKey, string eventKey, string? current, string? defaultValue)
    {
        var basePath = $@"{Root}\Schemes\Apps\{appKey}\{eventKey}";

        if (current is not null)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{basePath}\.Current");
            key?.SetValue(string.Empty, current, KindFor(current));
        }

        if (defaultValue is not null)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{basePath}\.Default");
            key?.SetValue(string.Empty, defaultValue, KindFor(defaultValue));
        }
    }

    public void SeedLabel(string eventKey, string displayName)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\EventLabels\{eventKey}");
        key?.SetValue(string.Empty, displayName, RegistryValueKind.String);
    }

    public void SeedSchemeName(string schemeKey, string displayName)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\Schemes\Names\{schemeKey}");
        key?.SetValue(string.Empty, displayName, RegistryValueKind.String);
    }

    /// <summary>Seeds a per-event value for a named scheme, as the control panel would.</summary>
    public void SeedSchemeValue(string appKey, string eventKey, string schemeKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"{Root}\Schemes\Apps\{appKey}\{eventKey}\{schemeKey}");

        key?.SetValue(string.Empty, value, KindFor(value));
    }

    // ------------------------------------------------------------------ cursors --

    /// <summary>Where the scratch cursor values live, mirroring Control Panel\Cursors.</summary>
    public string CursorsRoot => $@"{Root}\Cursors";

    /// <summary>
    /// A cursor service pointed entirely at scratch keys, including the system scheme
    /// location. System schemes normally live in HKLM, which tests cannot write, so they are
    /// redirected into HKCU here to make scheme behaviour testable at all.
    /// </summary>
    public CursorSchemeService CreateCursorService() => new(
        CursorsRoot,
        userSchemes: new SchemeLocation(RegistryHive.CurrentUser, $@"{CursorsRoot}\Schemes"),
        systemSchemes: new SchemeLocation(RegistryHive.CurrentUser, $@"{Root}\SystemCursorSchemes"));

    public void SeedCursor(string roleKey, string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(CursorsRoot);
        key?.SetValue(roleKey, path, KindFor(path));
    }

    public void SeedActiveCursorScheme(string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey(CursorsRoot);
        key?.SetValue(string.Empty, name, RegistryValueKind.String);
    }

    /// <summary>Seeds a scheme as the comma-separated positional string Windows uses.</summary>
    public void SeedCursorScheme(string name, IEnumerable<string> orderedPaths, bool systemScheme = false)
    {
        var path = systemScheme ? $@"{Root}\SystemCursorSchemes" : $@"{CursorsRoot}\Schemes";
        var value = string.Join(",", orderedPaths);

        using var key = Registry.CurrentUser.CreateSubKey(path);
        key?.SetValue(name, value, KindFor(value));
    }

    public string? ReadCursor(string roleKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(CursorsRoot);
        return key?.GetValue(roleKey, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public object? ReadCursorRawValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(CursorsRoot);
        return key?.GetValue(valueName);
    }

    // ------------------------------------------------------------------ reading --

    /// <summary>Reads .Current straight from the registry, bypassing the service under test.</summary>
    public string? ReadCurrent(string appKey, string eventKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"{Root}\Schemes\Apps\{appKey}\{eventKey}\.Current");

        return key?.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public RegistryValueKind? ReadCurrentKind(string appKey, string eventKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"{Root}\Schemes\Apps\{appKey}\{eventKey}\.Current");

        if (key is null) return null;

        try { return key.GetValueKind(string.Empty); }
        catch (System.IO.IOException) { return null; }   // value not present
    }

    public bool SchemeKeyExists(string appKey, string eventKey, string schemeKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"{Root}\Schemes\Apps\{appKey}\{eventKey}\{schemeKey}");

        return key is not null;
    }

    // ----------------------------------------------------------------- teardown --

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        }
        catch
        {
            // A leaked scratch key is annoying, not a test failure.
        }

        // The shared parent key is deliberately left in place. Deleting it the moment it
        // looks empty races with another test class creating its own subkey underneath,
        // which is exactly the bug that made TestWav flaky. An empty key under
        // HKCU\Software is a far smaller problem than an intermittent suite.
    }

    /// <summary>Mirrors what Windows stores: REG_EXPAND_SZ only when the path needs expansion.</summary>
    private static RegistryValueKind KindFor(string value) =>
        value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
}
