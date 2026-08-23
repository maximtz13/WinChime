using Microsoft.Win32;
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

        // Remove the shared parent once the last concurrent test has finished with it.
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(ParentPath);
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKey(ParentPath, throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // Racing with another test disposing at the same moment is fine.
        }
    }

    /// <summary>Mirrors what Windows stores: REG_EXPAND_SZ only when the path needs expansion.</summary>
    private static RegistryValueKind KindFor(string value) =>
        value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
}
