using WinChime.Core.Cursors;
using WinChime.Core.Model;

namespace WinChime.Core.Tests;

/// <summary>
/// A list row carries no automation name of its own, so UI Automation falls back to whatever
/// ToString returns — which means these overrides are what a screen reader actually reads out
/// on the Sounds and Cursors tabs. Nothing about that is visible on screen, so it is pinned
/// here rather than left to be noticed.
/// </summary>
public sealed class ListItemNameTests
{
    private static SoundEvent Event(string app = "Windows", string name = "Alarm 1") => new()
    {
        AppKey = ".Default",
        AppDisplayName = app,
        EventKey = "Alarm1",
        EventDisplayName = name,
    };

    private static CursorEntry Cursor(string display = "Normal Select", string role = "Arrow") => new()
    {
        RoleKey = role,
        DisplayName = display,
    };

    // ------------------------------------------------------------------ the bug --

    /// <summary>
    /// The failure this exists to prevent: seventy-two rows all announcing
    /// "WinChime.Core.Model.SoundEvent".
    /// </summary>
    [Fact]
    public void SoundEvent_DoesNotFallBackToTheTypeName()
    {
        var text = Event().ToString();

        Assert.DoesNotContain("WinChime", text);
        Assert.DoesNotContain("SoundEvent", text);
    }

    [Fact]
    public void CursorEntry_DoesNotFallBackToTheTypeName()
    {
        var text = Cursor().ToString();

        Assert.DoesNotContain("WinChime", text);
        Assert.DoesNotContain("CursorEntry", text);
    }

    // ---------------------------------------------------------------- the content --

    [Fact]
    public void SoundEvent_NamesTheSourceAndTheEvent()
    {
        Assert.Equal("Windows, Alarm 1", Event().ToString());
    }

    /// <summary>
    /// The source matters: event names are not unique across applications, so "Notification"
    /// on its own would produce several rows that sound identical.
    /// </summary>
    [Fact]
    public void SoundEvent_DistinguishesTheSameEventFromDifferentSources()
    {
        var windows = Event("Windows", "Notification").ToString();
        var explorer = Event("File Explorer", "Notification").ToString();

        Assert.NotEqual(windows, explorer);
    }

    [Fact]
    public void CursorEntry_UsesTheDisplayName()
    {
        Assert.Equal("Normal Select", Cursor().ToString());
    }

    /// <summary>
    /// Every role has a distinct display name, so the identity alone is enough to tell the
    /// seventeen rows apart without repeating the cells beside them.
    /// </summary>
    [Fact]
    public void CursorEntry_IsDistinctForEveryRole()
    {
        var names = CursorRoles.All
            .Select(role => new CursorEntry { RoleKey = role.Key, DisplayName = role.DisplayName }.ToString())
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ------------------------------------------------------------------ the noise --

    /// <summary>
    /// DisplayLabel separates with a bullet, which a screen reader reads aloud. "Windows
    /// bullet Alarm 1" is noise in a list being scanned a row at a time, which is the same
    /// reason the CLI does not use DisplayLabel either.
    /// </summary>
    [Fact]
    public void SoundEvent_AvoidsTheBulletThatDisplayLabelUses()
    {
        var soundEvent = Event();

        Assert.Contains("•", soundEvent.DisplayLabel);
        Assert.DoesNotContain("•", soundEvent.ToString());
    }

    /// <summary>
    /// Nothing else reads these, but a row name that is blank is as unhelpful as one that is
    /// a type name, and an entry can legitimately be constructed with sparse data.
    /// </summary>
    [Fact]
    public void NeitherIsEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(Event().ToString()));
        Assert.False(string.IsNullOrWhiteSpace(Cursor().ToString()));
    }
}
