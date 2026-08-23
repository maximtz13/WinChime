using System.Diagnostics;
using System.Text;

namespace WinChime.Core.Interop;

public sealed record ProcessOutcome(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>Best available human-readable text, preferring stderr when the call failed.</summary>
    public string BestMessage
    {
        get
        {
            var err = StdErr.Trim();
            var outp = StdOut.Trim();
            if (!Success && err.Length > 0) return err;
            if (outp.Length > 0) return outp;
            return err.Length > 0 ? err : $"Exit code {ExitCode}.";
        }
    }
}

/// <summary>
/// Runs console tools with no visible window and full output capture. Its only caller is
/// LogonChimeService driving schtasks.exe.
///
/// Preferred over the TaskScheduler COM API because a one-off task registration does not
/// justify a dependency, and because the XML handed to schtasks is inspectable by anyone
/// wondering what runs at their logon.
/// </summary>
public static class ProcessRunner
{
    public static ProcessOutcome Run(string fileName, string arguments, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new ProcessOutcome(-1, "", $"Could not start {fileName}.");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            // Read both streams asynchronously; reading one to completion first can
            // deadlock when the other fills its pipe buffer.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ProcessOutcome(-1, stdout.ToString(), $"{fileName} timed out after {timeoutMs} ms.");
            }

            // Ensures the async handlers have flushed before we read the builders.
            process.WaitForExit();

            return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            return new ProcessOutcome(-1, "", ex.Message);
        }
    }
}
