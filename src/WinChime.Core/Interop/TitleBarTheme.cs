namespace WinChime.Core.Interop;

/// <summary>
/// Switches a window's title bar between the light and dark system chrome.
///
/// WPF can restyle everything inside a window and nothing outside it. The title bar, the
/// minimise/maximise/close buttons and the border are drawn by DWM, so a dark WPF window on a
/// default Windows install ends up with a bright white cap across the top. Fixing that means
/// asking DWM directly.
///
/// The control this gives is one-directional, which is worth knowing before trying to "fix" the
/// asymmetry. Setting the attribute TRUE forces a dark caption. Setting it FALSE does not force
/// a light one: it restores the system default, and on Windows 11 that default already follows
/// the system theme. Verified on build 26200 with the system in dark mode, where the call
/// returns S_OK for both values and the caption stays dark for FALSE. So a dark app on a light
/// system looks right, and a light app on a dark system keeps a dark caption. Forcing light
/// would mean drawing the caption ourselves, which is a much larger change than it looks.
///
/// Every failure here is cosmetic by definition, so nothing in this file throws. On a machine
/// too old to know the attribute the call simply reports false and the caption is left alone,
/// which is exactly the behaviour that shipped before this existed.
/// </summary>
public static class TitleBarTheme
{
    /// <summary>
    /// Applies dark or light chrome to a top-level window.
    /// </summary>
    /// <param name="hwnd">The window handle. <see cref="IntPtr.Zero"/> is ignored.</param>
    /// <param name="dark">True for the dark title bar.</param>
    /// <param name="repaint">
    /// Forces the frame to redraw. Needed when the window is already on screen; unnecessary,
    /// and a wasted call, when applying the attribute before the window is first shown.
    /// </param>
    /// <returns>True when DWM accepted the attribute.</returns>
    public static bool Apply(IntPtr hwnd, bool dark, bool repaint = true)
    {
        if (hwnd == IntPtr.Zero) return false;

        // The attribute takes a BOOL, which is four bytes, not one.
        var value = dark ? 1 : 0;

        var applied = TrySet(hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref value)
                   || TrySet(hwnd, NativeMethods.DwmwaUseImmersiveDarkModeBefore20H1, ref value);

        if (applied && repaint) ForceFrameRepaint(hwnd);

        return applied;
    }

    private static bool TrySet(IntPtr hwnd, int attribute, ref int value)
    {
        try
        {
            // S_OK is 0. Anything else means DWM did not take it, most often E_INVALIDARG on a
            // build that does not recognise the attribute number.
            return NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll is present on every supported version of Windows, but a missing
            // desktop-composition DLL is not worth crashing a personalisation app over.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void ForceFrameRepaint(IntPtr hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);
    }
}
