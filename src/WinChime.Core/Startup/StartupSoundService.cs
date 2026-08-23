using Microsoft.Win32;
using WinChime.Core.Model;

namespace WinChime.Core.Startup;

/// <summary>
/// Controls the Windows logon chime, the sound that plays at the end of boot.
///
/// Two things are worth understanding here.
///
/// 1. The on/off switch is a plain HKLM DWORD (needs administrator). This is the same
///    checkbox as Sound control panel -> Sounds -> "Play Windows Startup sound".
///
/// 2. The audio itself is NOT a wav on disk and NOT a registry path. Since Windows 8 it is
///    a WAVE resource embedded in %SystemRoot%\System32\imageres.dll. Replacing it means
///    taking ownership from TrustedInstaller and rewriting a signed system binary, which
///    Windows Resource Protection reverts on `sfc /scannow` and which every cumulative
///    update overwrites.
///
///    This app deliberately does not do that. Instead <see cref="LogonChimeService"/>
///    disables the built-in chime and plays a user-supplied wav from a logon task. The
///    trade-off is a second or two of extra latency; the gain is never touching a system
///    binary, so nothing here can be undone by a Windows update or break SFC.
/// </summary>
public sealed class StartupSoundService
{
    private const string BootAnimationKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation";

    private const string PolicyKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private const string ValueName = "DisableStartupSound";

    /// <summary>
    /// True when Windows will play its own logon chime. Absent value means enabled;
    /// Windows 8 and later ship with it set to 1 (disabled) on most consumer SKUs.
    /// </summary>
    public bool IsBuiltInChimeEnabled()
    {
        // A machine policy, if present, wins over the LogonUI setting.
        var policy = ReadDword(PolicyKey, ValueName);
        if (policy is not null) return policy.Value == 0;

        var value = ReadDword(BootAnimationKey, ValueName);
        return value is null || value.Value == 0;
    }

    /// <summary>True when an administrator-set policy is forcing the chime off.</summary>
    public bool IsControlledByPolicy() => ReadDword(PolicyKey, ValueName) is not null;

    /// <summary>Requires elevation. Route through ElevationHelper when not already admin.</summary>
    public OperationResult SetBuiltInChimeEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(BootAnimationKey, writable: true);
            if (key is null) return OperationResult.Fail($@"Could not open HKLM\{BootAnimationKey}.");

            key.SetValue(ValueName, enabled ? 0 : 1, RegistryValueKind.DWord);

            var note = enabled
                ? "Windows will play its built-in logon chime at the next boot."
                : "The built-in logon chime is now off.";

            if (enabled && IsControlledByPolicy())
                note += " Note: a machine policy also sets this value and may override it.";

            return OperationResult.Ok(note);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.RequiresElevation(
                "Changing the startup sound writes to HKLM and needs administrator rights.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Where the built-in chime actually lives, surfaced in the UI so the behaviour is not
    /// mysterious when someone wonders why they cannot simply point it at a wav file.
    /// </summary>
    public static string BuiltInChimeSourceDescription =>
        @"WAVE resource inside %SystemRoot%\System32\imageres.dll (not a replaceable file).";

    private static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            var raw = key?.GetValue(valueName);
            return raw is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }
}
