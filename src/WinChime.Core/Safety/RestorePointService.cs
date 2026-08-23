using WinChime.Core.Interop;
using WinChime.Core.Model;

namespace WinChime.Core.Safety;

/// <summary>
/// Creates a System Restore checkpoint before anything risky.
///
/// Three real-world caveats, all surfaced to the user rather than swallowed:
///   - it requires elevation;
///   - System Restore must be turned on for the system drive, which it is not by default
///     on many OEM Windows 11 installs;
///   - Windows throttles creation to one restore point per 24 hours unless the
///     SystemRestorePointCreationFrequency policy is relaxed, so a second call in the same
///     day silently succeeds without creating anything.
///
/// A restore point is a backstop, not the primary undo path. Everything this app changes
/// is also reverted by <see cref="BackupService"/>, which does not depend on any of the above.
/// </summary>
public sealed class RestorePointService
{
    public OperationResult Create(string description)
    {
        if (!SystemProbe.IsElevated())
            return OperationResult.RequiresElevation("Creating a restore point needs administrator rights.");

        // The struct field is a fixed 256-char buffer; overrunning it corrupts the call.
        if (description.Length > 250) description = description[..250];

        var info = new NativeMethods.RestorePointInfo
        {
            dwEventType = NativeMethods.BEGIN_SYSTEM_CHANGE,
            dwRestorePtType = NativeMethods.MODIFY_SETTINGS,
            llSequenceNumber = 0,
            szDescription = description,
        };

        try
        {
            if (!NativeMethods.SRSetRestorePointW(ref info, out var status))
            {
                return OperationResult.Fail(
                    $"Windows declined to create a restore point (status {status.nStatus}). " +
                    "The most common causes are System Restore being turned off for this drive, " +
                    "or one having already been created in the last 24 hours.");
            }

            // Close the change window straight away; we are not wrapping an installer.
            var end = new NativeMethods.RestorePointInfo
            {
                dwEventType = NativeMethods.END_SYSTEM_CHANGE,
                dwRestorePtType = NativeMethods.MODIFY_SETTINGS,
                llSequenceNumber = status.llSequenceNumber,
                szDescription = description,
            };

            NativeMethods.SRSetRestorePointW(ref end, out _);

            return OperationResult.Ok($"Restore point created (sequence {status.llSequenceNumber}).");
        }
        catch (DllNotFoundException)
        {
            return OperationResult.Fail("System Restore is not available on this edition of Windows.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Restore point failed: {ex.Message}");
        }
    }
}
