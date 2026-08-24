namespace WinChime.Core.Cursors;

/// <summary>
/// One assignable cursor slot: the registry value name under HKCU\Control Panel\Cursors,
/// and the label Windows itself uses for it in the Mouse control panel.
/// </summary>
public sealed record CursorRole(string Key, string DisplayName);

/// <summary>
/// The cursor roles, in the exact order a scheme string uses.
///
/// That order is not documented anywhere useful, and it matters: a scheme is stored as a
/// single comma-separated list where meaning comes entirely from position. It was derived
/// by reading the shipped Windows Aero scheme and cross-referencing each entry against the
/// live HKCU values, so index 2 is AppStarting and index 3 is Wait because
/// aero_working.ani and aero_busy.ani sit in exactly those slots.
/// </summary>
public static class CursorRoles
{
    /// <summary>Ordered exactly as a scheme string expects. Do not reorder.</summary>
    public static IReadOnlyList<CursorRole> All { get; } =
    [
        new("Arrow", "Normal Select"),
        new("Help", "Help Select"),
        new("AppStarting", "Working In Background"),
        new("Wait", "Busy"),
        new("Crosshair", "Precision Select"),
        new("IBeam", "Text Select"),
        new("NWPen", "Handwriting"),
        new("No", "Unavailable"),
        new("SizeNS", "Vertical Resize"),
        new("SizeWE", "Horizontal Resize"),
        new("SizeNWSE", "Diagonal Resize 1"),
        new("SizeNESW", "Diagonal Resize 2"),
        new("SizeAll", "Move"),
        new("UpArrow", "Alternate Select"),
        new("Hand", "Link Select"),
        new("Pin", "Location Select"),
        new("Person", "Person Select"),
    ];

    /// <summary>
    /// Values that live in the same key but are not cursors. Treating CursorBaseSize or
    /// Scheme Source as an assignable cursor would corrupt the mouse settings, so the role
    /// list is a fixed allow-list rather than an enumeration of whatever is present.
    /// </summary>
    public static IReadOnlySet<string> NonCursorValues { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Scheme Source",
        "CursorBaseSize",
        "ContactVisualization",
        "GestureVisualization",
        "",              // the (Default) value holds the active scheme name
    };

    public static CursorRole? Find(string key) =>
        All.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Position in a scheme string, or -1 when the key is not a cursor role.</summary>
    public static int IndexOf(string key)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }
}
