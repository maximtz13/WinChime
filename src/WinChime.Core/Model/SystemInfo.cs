namespace WinChime.Core.Model;

public enum TriState { Unknown, Yes, No }

/// <summary>
/// Read-only snapshot of the machine, shown on the System tab.
///
/// This used to carry the pre-flight gate for UEFI boot-logo replacement. That feature was
/// dropped deliberately: it requires the user to disable Secure Boot, and shipping a tool
/// that talks people into weakening their own firmware trust chain for a cosmetic change is
/// not a trade worth offering. Firmware type and Secure Boot state are kept purely as
/// information; nothing in this app acts on them.
/// </summary>
public sealed class SystemInfo
{
    public string ProductName { get; init; } = "Unknown";
    public string DisplayVersion { get; init; } = "";
    public string BuildNumber { get; init; } = "";
    public int UpdateBuildRevision { get; init; }
    public bool Is64BitOs { get; init; }

    public string FirmwareType { get; init; } = "Unknown";   // Bios | Uefi | Unknown
    public TriState SecureBootEnabled { get; init; } = TriState.Unknown;

    public bool IsElevated { get; init; }
    public TriState SystemRestoreEnabled { get; init; } = TriState.Unknown;

    public string FullVersionString =>
        $"{ProductName} {DisplayVersion} (build {BuildNumber}.{UpdateBuildRevision})";
}
