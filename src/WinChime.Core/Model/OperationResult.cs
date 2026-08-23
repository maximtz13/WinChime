namespace WinChime.Core.Model;

/// <summary>
/// Every mutating operation returns one of these rather than throwing, so the UI can
/// surface a precise reason (access denied, policy blocked, file missing) instead of a
/// generic crash dialog.
/// </summary>
public sealed record OperationResult(bool Success, string Message, bool NeedsElevation = false)
{
    public static OperationResult Ok(string message = "Done.") => new(true, message);

    public static OperationResult Fail(string message) => new(false, message);

    public static OperationResult RequiresElevation(string message = "This change needs administrator rights.")
        => new(false, message, NeedsElevation: true);
}
