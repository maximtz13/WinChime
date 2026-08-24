namespace WinChime.Core.Model;

/// <summary>
/// Whether a registry value points at a file that ships with Windows.
///
/// Packs use this to decide what not to bundle. Windows' own sounds and cursors are present on
/// every install, so copying them into a pack bloats it and redistributes Microsoft's assets
/// for no benefit. Keeping the unexpanded form instead means the reference resolves wherever
/// the pack lands, including on a machine whose Windows folder is not on C:.
///
/// Shared by the sound and cursor pack services rather than written twice: it decides what
/// crosses a machine boundary, and two copies of that rule would eventually disagree.
/// </summary>
public static class WindowsShippedFile
{
    /// <summary>
    /// True when the value resolves to something inside the Windows folder.
    ///
    /// The unexpanded prefixes are checked first so the common case is settled without
    /// touching the filesystem, and so a pack authored on a machine that is offline or has an
    /// unusual Windows location still classifies correctly.
    /// </summary>
    public static bool Is(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return false;

        var trimmed = rawValue.TrimStart();

        if (trimmed.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("%windir%", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawValue));

            return full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A malformed path is not a Windows path. Deciding "no" here means the file gets
            // bundled, which is wasteful at worst; deciding "yes" would leave a pack
            // referencing a file the receiving machine does not have.
            return false;
        }
    }

    /// <summary>
    /// Rewrites a path inside the Windows folder back to its %SystemRoot% form, and leaves
    /// anything else exactly as it was.
    ///
    /// Necessary because the registry is not consistent about which form it stores. Sound
    /// assignments generally arrive already unexpanded, but the cursor values on a stock
    /// Windows 11 install are fully expanded: reading Control Panel\Cursors on the machine
    /// this was written on gives C:\WINDOWS\cursors\aero_arrow.cur, not the %SystemRoot%
    /// version. Putting that literal path in a pack produces a file that works perfectly on
    /// the machine that made it and breaks on any machine whose Windows is not on C:, which
    /// is the exact failure a pack exists to avoid.
    /// </summary>
    public static string Collapse(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return rawValue;

        var trimmed = rawValue.TrimStart();

        if (trimmed.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("%windir%", StringComparison.OrdinalIgnoreCase))
        {
            return rawValue;
        }

        try
        {
            var windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawValue));

            if (!full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return rawValue;

            return "%SystemRoot%" + full[windows.Length..];
        }
        catch
        {
            return rawValue;
        }
    }
}
