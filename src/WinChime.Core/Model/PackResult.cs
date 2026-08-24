namespace WinChime.Core.Model;

/// <summary>
/// The outcome of creating or installing a pack, for sounds or for cursors.
///
/// Deliberately not <see cref="OperationResult"/>. A pack can succeed and still have left
/// things out: a file that vanished between being assigned and being packed, an entry the
/// archive turned out not to contain, an entry refused because it tried to escape the
/// extraction folder. Those are worth telling the user about individually without turning the
/// whole operation into a failure, which is what <see cref="Warnings"/> carries.
/// </summary>
public sealed record PackResult(bool Success, string Message, string? Path = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static PackResult Fail(string message) => new(false, message);
}
