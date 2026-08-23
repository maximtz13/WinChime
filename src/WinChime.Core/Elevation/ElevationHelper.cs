using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinChime.Core.Model;
using WinChime.Core.Safety;

namespace WinChime.Core.Elevation;

public enum ElevatedOp
{
    SetStartupSound,
    SetLockScreenImage,
    ClearLockScreenImage,
    CreateRestorePoint,
}

public sealed class ElevatedRequest
{
    [JsonPropertyName("op")]
    public ElevatedOp Op { get; set; }

    [JsonPropertyName("stringArg")]
    public string? StringArg { get; set; }

    [JsonPropertyName("boolArg")]
    public bool BoolArg { get; set; }
}

public sealed class ElevatedResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>
/// Runs the handful of operations that need administrator rights in a short-lived elevated
/// copy of this same executable, then returns the result to the normal-privilege UI.
///
/// The alternative designs were both worse: shipping a requireAdministrator manifest means
/// the entire UI runs elevated for the whole session just to flip one DWORD, and installing
/// a persistent elevated helper service is far more attack surface than a personalisation
/// tool deserves. Here the elevated process exists for a few milliseconds, does exactly one
/// declared operation, and exits.
///
/// The request and response travel through temp files rather than the command line so that
/// file paths containing quotes or spaces cannot be mis-parsed into a different operation.
/// </summary>
public static class ElevationHelper
{
    public const string ElevatedOpSwitch = "--elevated-op";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Executes the request, elevating via UAC if required. When already elevated the
    /// operation runs in-process and no prompt appears.
    /// </summary>
    public static OperationResult Execute(ElevatedRequest request)
    {
        if (SystemProbe.IsElevated()) return Perform(request);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return OperationResult.Fail("Could not determine this application's own path.");

        var requestPath = Path.Combine(Path.GetTempPath(), $"winchime-op-{Guid.NewGuid():N}.json");
        var responsePath = requestPath + ".result";

        try
        {
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{ElevatedOpSwitch} \"{requestPath}\"",
                UseShellExecute = true,   // required for the runas verb
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process is null) return OperationResult.Fail("Could not start the elevated helper.");

            if (!process.WaitForExit(120_000))
                return OperationResult.Fail("The elevated helper did not finish in time.");

            if (!File.Exists(responsePath))
            {
                return OperationResult.Fail(
                    $"The elevated helper exited with code {process.ExitCode} without reporting a result.");
            }

            var response = JsonSerializer.Deserialize<ElevatedResponse>(
                File.ReadAllText(responsePath), JsonOptions);

            return response is null
                ? OperationResult.Fail("The elevated helper returned an unreadable result.")
                : new OperationResult(response.Success, response.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt. Not an error worth alarming about.
            return OperationResult.Fail("Cancelled at the administrator prompt. Nothing was changed.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Elevation failed: {ex.Message}");
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    /// <summary>
    /// Entry point for the elevated child process. Called from App startup when
    /// <see cref="ElevatedOpSwitch"/> is present on the command line.
    /// </summary>
    public static int RunElevatedChild(string requestPath)
    {
        var responsePath = requestPath + ".result";
        ElevatedResponse response;

        try
        {
            var request = JsonSerializer.Deserialize<ElevatedRequest>(
                File.ReadAllText(requestPath), JsonOptions);

            var result = request is null
                ? OperationResult.Fail("Unreadable elevated request.")
                : Perform(request);

            response = new ElevatedResponse { Success = result.Success, Message = result.Message };
        }
        catch (Exception ex)
        {
            response = new ElevatedResponse { Success = false, Message = ex.Message };
        }

        try
        {
            File.WriteAllText(responsePath, JsonSerializer.Serialize(response, JsonOptions), Encoding.UTF8);
        }
        catch
        {
            // Nothing useful left to do; the parent reports the missing result file.
        }

        return response.Success ? 0 : 1;
    }

    /// <summary>
    /// The complete set of privileged operations this app will perform. Deliberately a
    /// closed enum switch: an elevated process must never take a free-form command.
    /// </summary>
    private static OperationResult Perform(ElevatedRequest request) => request.Op switch
    {
        ElevatedOp.SetStartupSound =>
            new Startup.StartupSoundService().SetBuiltInChimeEnabled(request.BoolArg),

        ElevatedOp.SetLockScreenImage =>
            Personalization.LockScreenService.ApplyElevated(request.StringArg ?? ""),

        ElevatedOp.ClearLockScreenImage =>
            Personalization.LockScreenService.ClearElevated(),

        ElevatedOp.CreateRestorePoint =>
            new RestorePointService().Create(request.StringArg ?? "WinChime checkpoint"),

        _ => OperationResult.Fail($"Unknown operation: {request.Op}"),
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file */ }
    }
}
