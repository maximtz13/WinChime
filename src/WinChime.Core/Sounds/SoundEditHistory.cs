namespace WinChime.Core.Sounds;

/// <summary>
/// One reversible change, stored as the assignments before and after it.
/// Keys are "AppKey\EventKey"; values are unexpanded registry strings, empty meaning silent.
/// </summary>
public sealed record SoundEdit(
    string Description,
    IReadOnlyDictionary<string, string> Before,
    IReadOnlyDictionary<string, string> After);

/// <summary>
/// In-session undo/redo for sound assignments.
///
/// Backups already cover bulk operations, but they are a heavyweight recovery path: pick a
/// timestamp from a list, confirm, restore everything. That is the wrong shape for "I just
/// clicked the wrong event, put it back".
///
/// Entries are diffs rather than full snapshots, so a single assignment stores one key while
/// a scheme apply stores the events it touched. That keeps single edits cheap and lets bulk
/// operations use the same mechanism instead of needing a parallel one.
/// </summary>
public sealed class SoundEditHistory
{
    /// <summary>
    /// Deep enough that nobody reaches the end in a session, bounded so a long editing run
    /// cannot grow without limit.
    /// </summary>
    public const int MaxEntries = 100;

    private readonly List<SoundEdit> _undo = new();
    private readonly List<SoundEdit> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? NextUndoDescription => CanUndo ? _undo[^1].Description : null;
    public string? NextRedoDescription => CanRedo ? _redo[^1].Description : null;

    public int Count => _undo.Count;

    /// <summary>
    /// Records a change. A new edit invalidates the redo stack, which is the standard
    /// contract: redo only makes sense along the branch you undid from.
    /// </summary>
    public void Record(SoundEdit edit)
    {
        if (edit.Before.Count == 0 && edit.After.Count == 0) return;

        _undo.Add(edit);
        _redo.Clear();

        if (_undo.Count > MaxEntries) _undo.RemoveRange(0, _undo.Count - MaxEntries);
    }

    /// <summary>
    /// Convenience for the common case of one event changing.
    /// Does nothing when the value did not actually change.
    /// </summary>
    public void RecordSingle(string appKey, string eventKey, string? before, string? after, string description)
    {
        var beforeValue = before ?? string.Empty;
        var afterValue = after ?? string.Empty;

        if (string.Equals(beforeValue, afterValue, StringComparison.OrdinalIgnoreCase)) return;

        var key = $@"{appKey}\{eventKey}";

        Record(new SoundEdit(
            description,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = beforeValue },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = afterValue }));
    }

    /// <summary>
    /// Builds an entry from two full snapshots, keeping only the keys that actually differ.
    /// Used for bulk operations, where most of the snapshot is unchanged.
    /// </summary>
    public static SoundEdit? DiffSnapshots(
        string description,
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var changedBefore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changedAfter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in after)
        {
            before.TryGetValue(pair.Key, out var previous);
            previous ??= string.Empty;

            if (string.Equals(previous, pair.Value, StringComparison.OrdinalIgnoreCase)) continue;

            changedBefore[pair.Key] = previous;
            changedAfter[pair.Key] = pair.Value;
        }

        return changedAfter.Count == 0
            ? null
            : new SoundEdit(description, changedBefore, changedAfter);
    }

    /// <summary>Pops the most recent edit and moves it to the redo stack. Caller applies Before.</summary>
    public SoundEdit? Undo()
    {
        if (!CanUndo) return null;

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(edit);

        return edit;
    }

    /// <summary>Pops the most recent undone edit and moves it back. Caller applies After.</summary>
    public SoundEdit? Redo()
    {
        if (!CanRedo) return null;

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(edit);

        return edit;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
