using Microsoft.Win32;
using WinChime.Core.Elevation;
using WinChime.Core.Model;

namespace WinChime.Core.Personalization;

/// <summary>
/// Lock screen image.
///
/// This one has a genuine trade-off the user has to be told about, because there is no
/// clean API for it in an unpackaged desktop app:
///
///   - The tidy WinRT call, LockScreen.SetImageFileAsync, requires package identity. An
///     unpackaged .exe cannot use it, and MSIX-packaging this app purely for that one call
///     is not worth it (and the Store would not accept the app anyway).
///
///   - So we use the PersonalizationCSP route, which is what MDM uses. It works across
///     Windows editions including Home, unlike the Policies\Personalization key which Home
///     ignores.
///
/// The cost of the CSP route is that it *locks* the setting: the lock screen section of
/// Settings goes grey while it is applied. That is a real, visible side effect, so the UI
/// states it plainly up front and the Clear button fully removes it.
///
/// The chosen image is copied into ProgramData rather than referenced in place, because a
/// lock screen pointing at a deleted file in someone's Downloads folder degrades badly.
/// </summary>
public sealed class LockScreenService
{
    private const string CspKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Personalization";

    private const string PathValue = "LockScreenImagePath";
    private const string UrlValue = "LockScreenImageUrl";
    private const string StatusValue = "LockScreenImageStatus";

    private static string StorageFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinChime", "LockScreen");

    /// <summary>The image currently forced by this mechanism, or null when not applied.</summary>
    public string? GetCurrent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CspKey);
            var path = key?.GetValue(PathValue) as string;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    public bool IsApplied() => GetCurrent() is not null;

    /// <summary>Applies via UAC when not already elevated.</summary>
    public OperationResult Apply(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return OperationResult.Fail("Choose an existing image file first.");

        return ElevationHelper.Execute(new ElevatedRequest
        {
            Op = ElevatedOp.SetLockScreenImage,
            StringArg = imagePath,
        });
    }

    public OperationResult Clear() =>
        ElevationHelper.Execute(new ElevatedRequest { Op = ElevatedOp.ClearLockScreenImage });

    // ------------------------------------------------- elevated implementations --

    internal static OperationResult ApplyElevated(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return OperationResult.Fail($"Image not found: {sourcePath}");

            Directory.CreateDirectory(StorageFolder);

            // Stable filename so repeated applies do not accumulate copies, but keep the
            // original extension because the CSP is picky about unrecognised formats.
            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

            var destination = Path.Combine(StorageFolder, $"lockscreen{extension}");

            // Clean up a previous image with a different extension.
            foreach (var stale in Directory.EnumerateFiles(StorageFolder, "lockscreen.*"))
            {
                if (!string.Equals(stale, destination, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(stale); } catch { /* best effort */ }
                }
            }

            File.Copy(sourcePath, destination, overwrite: true);

            using var key = Registry.LocalMachine.CreateSubKey(CspKey, writable: true);
            if (key is null) return OperationResult.Fail($@"Could not open HKLM\{CspKey}.");

            key.SetValue(PathValue, destination, RegistryValueKind.String);
            key.SetValue(UrlValue, destination, RegistryValueKind.String);
            key.SetValue(StatusValue, 1, RegistryValueKind.DWord);

            return OperationResult.Ok(
                "Lock screen image applied. The lock screen section of Settings will appear " +
                "greyed out until you clear it here, which is expected for this mechanism.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.RequiresElevation("Setting the lock screen image needs administrator rights.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not set the lock screen image: {ex.Message}");
        }
    }

    internal static OperationResult ClearElevated()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CspKey, writable: true);

            if (key is null)
                return OperationResult.Ok("Nothing to clear; the lock screen was never overridden.");

            foreach (var name in new[] { PathValue, UrlValue, StatusValue })
            {
                try { key.DeleteValue(name, throwOnMissingValue: false); } catch { /* already gone */ }
            }

            if (Directory.Exists(StorageFolder))
            {
                foreach (var file in Directory.EnumerateFiles(StorageFolder, "lockscreen.*"))
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }

            return OperationResult.Ok("Lock screen override removed. Settings will be editable again.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.RequiresElevation("Clearing the lock screen image needs administrator rights.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not clear the lock screen image: {ex.Message}");
        }
    }
}
