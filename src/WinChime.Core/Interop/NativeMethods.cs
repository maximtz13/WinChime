using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace WinChime.Core.Interop;

internal static class NativeMethods
{
    // ---- winmm: sound preview -------------------------------------------------
    public const uint SND_SYNC = 0x0000;
    public const uint SND_ASYNC = 0x0001;
    public const uint SND_NODEFAULT = 0x0002;
    public const uint SND_PURGE = 0x0040;
    public const uint SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    // ---- user32: wallpaper ----------------------------------------------------
    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    // ---- kernel32: firmware type ---------------------------------------------
    public enum FirmwareType
    {
        Unknown = 0,
        Bios = 1,
        Uefi = 2,
        Max = 3,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFirmwareType(out FirmwareType firmwareType);

    // ---- srclient: System Restore --------------------------------------------
    // SRSetRestorePoint requires elevation AND System Restore enabled on the system
    // drive. Windows also throttles creation to once per 24h unless the
    // SystemRestorePointCreationFrequency policy is relaxed.
    public const int BEGIN_SYSTEM_CHANGE = 100;
    public const int END_SYSTEM_CHANGE = 101;
    public const int APPLICATION_INSTALL = 0;
    public const int MODIFY_SETTINGS = 12;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RestorePointInfo
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StateMgrStatus
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SRSetRestorePointW(ref RestorePointInfo pRestorePtSpec, out StateMgrStatus pSMgrStatus);

    // ---- kernel32: resource reading -------------------------------------------
    // Used only to READ the logon chime out of imageres.dll so it can be previewed.
    // The module is mapped as a datafile, so nothing is executed and nothing is written.
    public const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    public const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

    /// <summary>Resource type name for embedded audio in imageres.dll.</summary>
    public const string ResourceTypeWave = "WAVE";

    public delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumResourceNamesW(
        IntPtr hModule, string lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, string lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    // ---- advapi32: registry change notification --------------------------------
    // Used to notice when something else changes the sound settings while the app is open,
    // so the list does not silently drift out of date.
    public const int REG_NOTIFY_CHANGE_NAME = 0x00000001;
    public const int REG_NOTIFY_CHANGE_ATTRIBUTES = 0x00000002;
    public const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    /// <summary>
    /// Without this flag the notification is bound to the calling thread and is cancelled
    /// when that thread exits. Requires Windows 8 or later.
    /// </summary>
    public const int REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
        int dwNotifyFilter,
        SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    // ---- user32: cursors -------------------------------------------------------
    // Writing the registry values alone changes nothing on screen. SPI_SETCURSORS makes
    // Windows reload them, which is what actually swaps the pointer.
    public const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoNoParam(
        uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    // ---- user32: broadcasting a settings change ---------------------------------
    // Writing the accent registry values changes nothing on screen until running
    // applications are told to re-read them.
    public static readonly IntPtr HwndBroadcast = new(0xFFFF);

    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    /// <summary>Do not wait on a window that has stopped responding.</summary>
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string? lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    // ---- dwmapi: dark title bar -------------------------------------------------
    // A window painted dark with a light title bar looks broken, and WPF cannot style the
    // non-client area at all. DWM can, through an attribute that went undocumented for years.
    //
    // The attribute number moved. Windows 10 20H1 (build 19041) settled on 20; the builds
    // between 18362 and 19041 used 19, and on those the two numbers mean different things, so
    // sending the wrong one silently does nothing or toggles an unrelated flag. Rather than
    // gate on a build number, the caller tries 20 and falls back to 19 only when DWM rejects
    // it, which is the same order of preference with none of the version guessing.
    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- user32: forcing the frame to repaint -----------------------------------
    // Setting the dark-mode attribute on a window that is already on screen does not always
    // repaint the title bar; on Windows 10 it frequently does not. Telling the window its
    // frame changed forces the non-client area to be redrawn without moving or resizing it.
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // ---- user32 + gdi32: rasterising a cursor ------------------------------------
    // Used to draw a .cur or .ani into a bitmap for the preview. There is no managed way to
    // do this: WPF cannot decode either format, and the animated one has no single image to
    // decode anyway. DrawIconEx is the only API that will render a chosen animation step.

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        /// <summary>Non-zero for an icon, zero for a cursor. Declared as int to stay blittable.</summary>
        public int fIcon;

        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapHeader
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;

        /// <summary>Negative for a top-down bitmap, which avoids flipping the rows by hand.</summary>
        public int biHeight;

        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    public const uint DI_MASK = 0x0001;
    public const uint DI_IMAGE = 0x0002;
    public const uint DI_NORMAL = DI_MASK | DI_IMAGE;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadCursorFromFileW")]
    public static extern IntPtr LoadCursorFromFile(string lpFileName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    /// <param name="istepIfAniCur">
    /// Animation step to draw. Windows resolves the seq chunk itself, so this indexes steps
    /// rather than frames.
    /// </param>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfoHeader pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "GetObjectW")]
    public static extern int GetObject(IntPtr h, int c, ref BitmapHeader pv);
}
