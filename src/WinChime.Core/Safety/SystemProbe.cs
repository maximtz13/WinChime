using System.Security.Principal;
using Microsoft.Win32;
using WinChime.Core.Interop;
using WinChime.Core.Model;

namespace WinChime.Core.Safety;

/// <summary>
/// Read-only snapshot of the machine, shown on the System tab.
///
/// Nothing in this app acts on these values; they are informational. Firmware type and
/// Secure Boot state are read because they are free (one API call, one registry value) and
/// useful context, not because anything here gates on them. Every check degrades to Unknown
/// rather than guessing.
/// </summary>
public static class SystemProbe
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    /// <summary>First build number of Windows 11.</summary>
    private const int Windows11FirstBuild = 22000;

    public static SystemInfo Capture()
    {
        var elevated = IsElevated();
        var buildNumber = ReadString(CurrentVersionKey, "CurrentBuild") ?? "";

        return new SystemInfo
        {
            ProductName = NormaliseProductName(ReadString(CurrentVersionKey, "ProductName"), buildNumber),
            DisplayVersion = ReadString(CurrentVersionKey, "DisplayVersion") ?? "",
            BuildNumber = buildNumber,
            UpdateBuildRevision = ReadDword(CurrentVersionKey, "UBR") ?? 0,
            Is64BitOs = Environment.Is64BitOperatingSystem,

            FirmwareType = DetectFirmwareType(),
            SecureBootEnabled = DetectSecureBoot(),

            IsElevated = elevated,
            SystemRestoreEnabled = DetectSystemRestore(),
        };
    }

    /// <summary>
    /// Windows 11 never updated the ProductName registry value, so it still reads
    /// "Windows 10 ..." on every Win11 build. Correct it from the build number rather than
    /// displaying something the user knows is wrong.
    /// </summary>
    private static string NormaliseProductName(string? productName, string buildNumber)
    {
        if (string.IsNullOrWhiteSpace(productName)) return "Windows";

        if (int.TryParse(buildNumber, out var build)
            && build >= Windows11FirstBuild
            && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            return productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        return productName;
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string DetectFirmwareType()
    {
        try
        {
            return NativeMethods.GetFirmwareType(out var type)
                ? type.ToString()
                : "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Read from the kernel-published SecureBoot\State key rather than the UEFI variable
    /// API, because the registry route works for standard users and needs no privileges.
    /// The key is absent entirely on legacy BIOS systems.
    /// </summary>
    private static TriState DetectSecureBoot()
    {
        var value = ReadDword(SecureBootStateKey, "UEFISecureBootEnabled", RegistryHive.LocalMachine);

        if (value is null)
        {
            // Absent on BIOS machines, which genuinely means "no Secure Boot".
            return DetectFirmwareType() == "Bios" ? TriState.No : TriState.Unknown;
        }

        return value.Value == 1 ? TriState.Yes : TriState.No;
    }

    /// <summary>
    /// Best-effort only. The definitive answer comes from actually calling
    /// SRSetRestorePoint, which <see cref="RestorePointService"/> reports precisely.
    /// </summary>
    private static TriState DetectSystemRestore()
    {
        var disabled = ReadDword(SystemRestoreKey, "DisableSR");
        if (disabled is not null) return disabled.Value == 1 ? TriState.No : TriState.Yes;
        return TriState.Unknown;
    }

    // ----------------------------------------------------------------- helpers --

    private static string? ReadString(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadDword(string subKey, string valueName, RegistryHive hive = RegistryHive.LocalMachine)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(subKey);
            var raw = key?.GetValue(valueName);
            return raw is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }
}
