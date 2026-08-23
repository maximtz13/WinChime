using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

public sealed class SoundEditHistoryTests
{
    private static Dictionary<string, string> Map(params (string Key, string Value)[] entries)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) map[entry.Key] = entry.Value;
        return map;
    }

    [Fact]
    public void NewHistory_HasNothingToUndoOrRedo()
    {
        var history = new SoundEditHistory();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.NextUndoDescription);
    }

    [Fact]
    public void RecordSingle_MakesTheEditUndoable()
    {
        var history = new SoundEditHistory();

        history.RecordSingle(".Default", "SystemHand", @"C:\old.wav", @"C:\new.wav", "Set Critical Stop");

        Assert.True(history.CanUndo);
        Assert.Equal("Set Critical Stop", history.NextUndoDescription);
    }

    /// <summary>
    /// Reassigning the same file is not a change, and cluttering the undo stack with
    /// no-ops means the user presses Undo and apparently nothing happens.
    /// </summary>
    [Fact]
    public void RecordSingle_IgnoresAChangeThatChangesNothing()
    {
        var history = new SoundEditHistory();

        history.RecordSingle(".Default", "SystemHand", @"C:\same.wav", @"C:\same.wav", "No-op");

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void RecordSingle_TreatsNullAndEmptyAsTheSameSilence()
    {
        var history = new SoundEditHistory();

        history.RecordSingle(".Default", "SystemHand", null, "", "Still silent");

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Undo_ReturnsTheEditAndMovesItToRedo()
    {
        var history = new SoundEditHistory();
        history.RecordSingle(".Default", "SystemHand", @"C:\old.wav", @"C:\new.wav", "Set");

        var edit = history.Undo();

        Assert.NotNull(edit);
        Assert.Equal(@"C:\old.wav", edit!.Before[@".Default\SystemHand"]);
        Assert.Equal(@"C:\new.wav", edit.After[@".Default\SystemHand"]);

        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_MovesTheEditBackToUndo()
    {
        var history = new SoundEditHistory();
        history.RecordSingle(".Default", "SystemHand", @"C:\old.wav", @"C:\new.wav", "Set");
        history.Undo();

        var edit = history.Redo();

        Assert.NotNull(edit);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    /// <summary>
    /// Standard contract: redo only makes sense along the branch you undid from. Keeping a
    /// stale redo stack would let the user redo a change that no longer follows from the
    /// current state.
    /// </summary>
    [Fact]
    public void RecordingANewEdit_DiscardsTheRedoStack()
    {
        var history = new SoundEditHistory();
        history.RecordSingle(".Default", "SystemHand", "a.wav", "b.wav", "First");
        history.Undo();
        Assert.True(history.CanRedo);

        history.RecordSingle(".Default", "SystemAsterisk", "c.wav", "d.wav", "Second");

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void History_IsBoundedSoALongSessionCannotGrowWithoutLimit()
    {
        var history = new SoundEditHistory();

        for (var i = 0; i < SoundEditHistory.MaxEntries + 25; i++)
            history.RecordSingle(".Default", $"Event{i}", "a.wav", "b.wav", $"Edit {i}");

        Assert.Equal(SoundEditHistory.MaxEntries, history.Count);

        // The most recent edit survives; the oldest are the ones dropped.
        Assert.Equal($"Edit {SoundEditHistory.MaxEntries + 24}", history.NextUndoDescription);
    }

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var history = new SoundEditHistory();
        history.RecordSingle(".Default", "SystemHand", "a.wav", "b.wav", "Set");
        history.Undo();

        history.Clear();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    // ------------------------------------------------------------ bulk diffs --

    /// <summary>
    /// A scheme apply touches every event, but most values are unchanged. Storing the whole
    /// snapshot per entry would make the bounded history far heavier than it needs to be.
    /// </summary>
    [Fact]
    public void DiffSnapshots_KeepsOnlyTheKeysThatActuallyChanged()
    {
        var before = Map((@".Default\A", "a.wav"), (@".Default\B", "b.wav"), (@".Default\C", "c.wav"));
        var after = Map((@".Default\A", "a.wav"), (@".Default\B", "CHANGED.wav"), (@".Default\C", "c.wav"));

        var edit = SoundEditHistory.DiffSnapshots("Apply scheme", before, after);

        Assert.NotNull(edit);
        Assert.Single(edit!.Before);
        Assert.Equal("b.wav", edit.Before[@".Default\B"]);
        Assert.Equal("CHANGED.wav", edit.After[@".Default\B"]);
    }

    [Fact]
    public void DiffSnapshots_ReturnsNullWhenNothingChanged()
    {
        var snapshot = Map((@".Default\A", "a.wav"), (@".Default\B", "b.wav"));

        Assert.Null(SoundEditHistory.DiffSnapshots("No change", snapshot, snapshot));
    }

    [Fact]
    public void DiffSnapshots_TreatsAKeyAbsentBeforeAsPreviouslySilent()
    {
        var before = Map();
        var after = Map((@".Default\New", "new.wav"));

        var edit = SoundEditHistory.DiffSnapshots("Added", before, after);

        Assert.NotNull(edit);
        Assert.Equal(string.Empty, edit!.Before[@".Default\New"]);
    }

    [Fact]
    public void RecordedBulkEdit_UndoesAndRedoesAsOneStep()
    {
        var history = new SoundEditHistory();
        var before = Map((@".Default\A", "a.wav"), (@".Default\B", "b.wav"));
        var after = Map((@".Default\A", "x.wav"), (@".Default\B", "y.wav"));

        history.Record(SoundEditHistory.DiffSnapshots("Apply scheme", before, after)!);

        var edit = history.Undo();

        Assert.NotNull(edit);
        Assert.Equal(2, edit!.Before.Count);
        Assert.False(history.CanUndo);   // one step, not two
    }
}
