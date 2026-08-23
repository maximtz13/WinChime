using System.Runtime.InteropServices;
using WinChime.Core.Interop;
using WinChime.Core.Model;
using WinChime.Core.Sounds;

namespace WinChime.Core.Startup;

public sealed record ExtractedChime(string ResourceName, string FilePath, int SizeBytes, WaveInfo Info)
{
    public string DisplayName => $"{ResourceName} ({SizeBytes / 1024.0:0.#} KB) — {Info.Summary}";
}

/// <summary>
/// Reads the Windows logon chime out of imageres.dll so it can be previewed and saved.
///
/// This is the read-only counterpart to the thing this app refuses to do. Patching that
/// resource means rewriting a signed system binary; *reading* it is completely safe — the
/// module is mapped with LOAD_LIBRARY_AS_DATAFILE, so no code from it is executed, nothing
/// is written back, and no elevation is needed because Users have read access to
/// System32.
///
/// The resource is commonly cited as ID 5080, but rather than hard-code that we enumerate
/// the WAVE resources and take what is actually there, which survives Microsoft renumbering
/// it in a future build.
/// </summary>
public static class SystemChimeResource
{
    /// <summary>
    /// Candidate modules, in order. The audio is language-neutral so it normally lives in
    /// imageres.dll itself, but the MUI satellite is checked as a fallback in case a future
    /// build moves it.
    /// </summary>
    private static IEnumerable<string> CandidateModules()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        yield return Path.Combine(system32, "imageres.dll");

        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (!string.IsNullOrWhiteSpace(culture))
            yield return Path.Combine(system32, culture, "imageres.dll.mui");

        yield return Path.Combine(system32, "en-US", "imageres.dll.mui");
    }

    private static string ExtractionFolder => Path.Combine(Path.GetTempPath(), "WinChime", "extracted");

    /// <summary>
    /// Extracts every embedded WAVE to a temp folder and inspects each one.
    /// Returns an empty list plus an error string when nothing could be read.
    /// </summary>
    public static (IReadOnlyList<ExtractedChime> Chimes, string? Error) Extract()
    {
        var errors = new List<string>();

        foreach (var module in CandidateModules())
        {
            if (!File.Exists(module)) continue;

            var (chimes, error) = ExtractFrom(module);
            if (chimes.Count > 0) return (chimes, null);
            if (error is not null) errors.Add($"{Path.GetFileName(module)}: {error}");
        }

        return (Array.Empty<ExtractedChime>(),
            errors.Count > 0
                ? string.Join("; ", errors)
                : "No embedded startup sound was found in imageres.dll on this build of Windows.");
    }

    private static (IReadOnlyList<ExtractedChime> Chimes, string? Error) ExtractFrom(string modulePath)
    {
        var handle = NativeMethods.LoadLibraryExW(
            modulePath,
            IntPtr.Zero,
            NativeMethods.LOAD_LIBRARY_AS_DATAFILE | NativeMethods.LOAD_LIBRARY_AS_IMAGE_RESOURCE);

        if (handle == IntPtr.Zero)
            return (Array.Empty<ExtractedChime>(), $"Could not open the module (error {Marshal.GetLastWin32Error()}).");

        try
        {
            var names = new List<(IntPtr Pointer, string Display)>();

            NativeMethods.EnumResNameProc callback = (_, _, namePtr, _) =>
            {
                names.Add((namePtr, DescribeResourceName(namePtr)));
                return true;   // keep enumerating
            };

            // EnumResourceNames fails with ERROR_RESOURCE_TYPE_NOT_FOUND when the module
            // has no WAVE resources at all, which is a normal outcome, not a fault.
            NativeMethods.EnumResourceNamesW(handle, NativeMethods.ResourceTypeWave, callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            if (names.Count == 0) return (Array.Empty<ExtractedChime>(), null);

            Directory.CreateDirectory(ExtractionFolder);

            var results = new List<ExtractedChime>();

            foreach (var (pointer, display) in names)
            {
                var bytes = ReadResourceBytes(handle, pointer);
                if (bytes is null) continue;

                var safeName = display.Replace("#", "id-");
                var destination = Path.Combine(
                    ExtractionFolder,
                    $"Windows startup sound ({safeName}).wav");

                File.WriteAllBytes(destination, bytes);
                results.Add(new ExtractedChime(display, destination, bytes.Length, WaveFile.Inspect(destination)));
            }

            return (results, null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<ExtractedChime>(), ex.Message);
        }
        finally
        {
            NativeMethods.FreeLibrary(handle);
        }
    }

    private static byte[]? ReadResourceBytes(IntPtr module, IntPtr namePtr)
    {
        var resourceInfo = NativeMethods.FindResourceW(module, namePtr, NativeMethods.ResourceTypeWave);
        if (resourceInfo == IntPtr.Zero) return null;

        var size = NativeMethods.SizeofResource(module, resourceInfo);
        if (size == 0) return null;

        var loaded = NativeMethods.LoadResource(module, resourceInfo);
        if (loaded == IntPtr.Zero) return null;

        var data = NativeMethods.LockResource(loaded);
        if (data == IntPtr.Zero) return null;

        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        return bytes;
    }

    /// <summary>
    /// Resource names are either integer IDs packed into the pointer value, or pointers to
    /// a wide string. IS_INTRESOURCE is the documented way to tell them apart.
    /// </summary>
    private static string DescribeResourceName(IntPtr namePtr)
    {
        var value = namePtr.ToInt64();

        if (value >= 0 && value <= ushort.MaxValue) return $"#{value}";

        return Marshal.PtrToStringUni(namePtr) ?? $"#{value}";
    }

    /// <summary>Copies a previously extracted chime somewhere the user chose.</summary>
    public static OperationResult SaveCopy(ExtractedChime chime, string destinationPath)
    {
        try
        {
            File.Copy(chime.FilePath, destinationPath, overwrite: true);
            return OperationResult.Ok($"Saved to {Path.GetFileName(destinationPath)}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not save a copy: {ex.Message}");
        }
    }
}
