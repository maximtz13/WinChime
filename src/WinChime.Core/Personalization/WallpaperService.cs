using Microsoft.Win32;
using WinChime.Core.Interop;
using WinChime.Core.Model;

namespace WinChime.Core.Personalization;

public enum WallpaperStyle
{
    Fill,
    Fit,
    Stretch,
    Tile,
    Centre,
    Span,
}

/// <summary>
/// Desktop wallpaper. The genuinely easy one: per-user, no elevation, no policy, instant
/// effect, and fully reversible through the normal Settings UI afterwards.
///
/// The style is stored in HKCU\Control Panel\Desktop as a WallpaperStyle/TileWallpaper
/// pair and must be written *before* SystemParametersInfo, which is what actually reloads
/// the desktop.
/// </summary>
public sealed class WallpaperService
{
    private const string DesktopKey = @"Control Panel\Desktop";

    public OperationResult Set(string imagePath, WallpaperStyle style)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return OperationResult.Fail("Choose an existing image file first.");

        try
        {
            WriteStyle(style);

            var ok = NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETDESKWALLPAPER,
                0,
                imagePath,
                NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);

            return ok
                ? OperationResult.Ok($"Wallpaper set to {Path.GetFileName(imagePath)}.")
                : OperationResult.Fail("Windows rejected the image. Check that it is a readable BMP, JPG or PNG.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not set wallpaper: {ex.Message}");
        }
    }

    public string? GetCurrent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
            var path = key?.GetValue("WallPaper") as string;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStyle(WallpaperStyle style)
    {
        // WallpaperStyle and TileWallpaper are a pair; tiling is the odd one out because it
        // is expressed by TileWallpaper=1 rather than by a distinct WallpaperStyle value.
        var (wallpaperStyle, tile) = style switch
        {
            WallpaperStyle.Fill => ("10", "0"),
            WallpaperStyle.Fit => ("6", "0"),
            WallpaperStyle.Stretch => ("2", "0"),
            WallpaperStyle.Tile => ("0", "1"),
            WallpaperStyle.Centre => ("0", "0"),
            WallpaperStyle.Span => ("22", "0"),
            _ => ("10", "0"),
        };

        using var key = Registry.CurrentUser.CreateSubKey(DesktopKey, writable: true);
        if (key is null) return;

        key.SetValue("WallpaperStyle", wallpaperStyle, RegistryValueKind.String);
        key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
    }
}
