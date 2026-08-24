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
}
