using WinChime.Core.Interop;

namespace WinChime.Core.Sounds;

/// <summary>
/// Thin wrapper over winmm PlaySound. Preferred over System.Media.SoundPlayer because
/// it needs no extra package and gives us a real Stop().
/// </summary>
public static class SoundPreview
{
    public static bool Play(string path, bool synchronous = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        var flags = NativeMethods.SND_FILENAME | NativeMethods.SND_NODEFAULT
                    | (synchronous ? NativeMethods.SND_SYNC : NativeMethods.SND_ASYNC);

        return NativeMethods.PlaySound(path, IntPtr.Zero, flags);
    }

    public static void Stop() =>
        NativeMethods.PlaySound(null, IntPtr.Zero, NativeMethods.SND_PURGE);
}
