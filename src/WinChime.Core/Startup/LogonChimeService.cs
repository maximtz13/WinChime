using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using WinChime.Core.Interop;
using WinChime.Core.Model;
using WinChime.Core.Sounds;

namespace WinChime.Core.Startup;

public sealed record LogonChimeConfig(bool Installed, string? WavPath, int DelaySeconds);

/// <summary>
/// The safe route to a custom startup sound: a per-user logon scheduled task that runs
/// this same executable with --play-chime.
///
/// Why a scheduled task rather than a Run key or a Startup-folder shortcut:
///   - it supports an explicit delay, which matters because the desktop is still
///     initialising at logon and audio devices may not be ready yet;
///   - it runs hidden with no console flash;
///   - it is trivially inspectable and removable by the user in Task Scheduler, which
///     matters for something that runs at every logon.
///
/// Registered through schtasks.exe with an XML definition rather than the Task Scheduler
/// COM API, to keep this assembly free of NuGet dependencies. Note that schtasks requires
/// the XML file to be UTF-16 encoded; UTF-8 is rejected with an unhelpful parse error.
/// </summary>
public sealed class LogonChimeService
{
    public const string TaskName = "WinChime Logon Chime";
    public const int MaxDelaySeconds = 120;

    private static string SchTasks => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public LogonChimeConfig GetConfig()
    {
        var outcome = ProcessRunner.Run(SchTasks, $"/Query /TN \"{TaskName}\" /XML ONE");
        if (!outcome.Success) return new LogonChimeConfig(false, null, 0);

        try
        {
            // schtasks writes the XML with a BOM that XDocument.Parse will not accept.
            var xml = outcome.StdOut.TrimStart('\uFEFF', '\r', '\n', ' ');
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            var arguments = doc.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
            var wav = ExtractQuotedArgument(arguments);

            var delayText = doc.Descendants(ns + "LogonTrigger")
                               .Elements(ns + "Delay")
                               .FirstOrDefault()?.Value;

            return new LogonChimeConfig(true, wav, ParseIso8601Seconds(delayText));
        }
        catch
        {
            // The task exists but we cannot read it back. Report installed with unknown
            // settings rather than claiming it is absent, so Remove stays available.
            return new LogonChimeConfig(true, null, 0);
        }
    }

    public OperationResult Install(string wavPath, int delaySeconds)
    {
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            return OperationResult.Fail("Choose an existing .wav file first.");

        var info = WaveFile.Inspect(wavPath);
        if (!info.IsValid)
            return OperationResult.Fail(info.Error ?? "That file is not a readable WAV.");

        if (!info.IsPlayableByWindows)
            return OperationResult.Fail(
                $"That file is {info.FormatName}, not uncompressed PCM. It would play silently. " +
                "Re-encode it as a 16-bit PCM WAV first.");

        delaySeconds = Math.Clamp(delaySeconds, 0, MaxDelaySeconds);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return OperationResult.Fail("Could not determine this application's own path.");

        var xmlPath = Path.Combine(Path.GetTempPath(), $"winchime-logon-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exePath, wavPath, delaySeconds), Encoding.Unicode);

            var outcome = ProcessRunner.Run(SchTasks, $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (!outcome.Success)
                return OperationResult.Fail($"Task registration failed. {outcome.BestMessage}");

            return OperationResult.Ok(
                $"Custom chime installed. It will play {delaySeconds}s after logon. " +
                "Remember to turn the built-in Windows chime off so they do not overlap.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Task registration failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* temp file */ }
        }
    }

    public OperationResult Uninstall()
    {
        var outcome = ProcessRunner.Run(SchTasks, $"/Delete /TN \"{TaskName}\" /F");

        // schtasks returns non-zero when the task does not exist. That is the desired
        // end state, so treat it as success rather than surfacing a confusing error.
        if (!outcome.Success && !GetConfig().Installed)
            return OperationResult.Ok("No custom chime task was installed.");

        return outcome.Success
            ? OperationResult.Ok("Custom chime removed.")
            : OperationResult.Fail($"Could not remove the task. {outcome.BestMessage}");
    }

    /// <summary>Fires the task now, exactly as logon would, so the user can hear the real thing.</summary>
    public OperationResult TestNow()
    {
        var outcome = ProcessRunner.Run(SchTasks, $"/Run /TN \"{TaskName}\"");
        return outcome.Success
            ? OperationResult.Ok("Triggered the logon chime task.")
            : OperationResult.Fail($"Could not run the task. {outcome.BestMessage}");
    }

    private static string BuildTaskXml(string exePath, string wavPath, int delaySeconds)
    {
        var userId = WindowsIdentity.GetCurrent().Name;
        var delay = delaySeconds > 0 ? $"      <Delay>PT{delaySeconds}S</Delay>\r\n" : "";

        // Built as text rather than via XDocument so the exact element order the Task
        // Scheduler schema requires is obvious and reviewable.
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>{Escape(userId)}</Author>
    <Description>Plays a user-selected sound shortly after logon. Created by WinChime.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{Escape(userId)}</UserId>
{delay}    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{Escape(userId)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Escape(exePath)}</Command>
      <Arguments>--play-chime "{Escape(wavPath)}"</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Pulls the wav path back out of: --play-chime "C:\path\file.wav"</summary>
    private static string? ExtractQuotedArgument(string arguments)
    {
        var first = arguments.IndexOf('"');
        if (first < 0) return null;

        var last = arguments.LastIndexOf('"');
        if (last <= first) return null;

        return arguments.Substring(first + 1, last - first - 1);
    }

    /// <summary>Handles the PT#S / PT#M forms the Task Scheduler emits for short delays.</summary>
    private static int ParseIso8601Seconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;

        try
        {
            return (int)System.Xml.XmlConvert.ToTimeSpan(value).TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }
}
