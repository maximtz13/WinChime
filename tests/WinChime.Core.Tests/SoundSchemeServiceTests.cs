using Microsoft.Win32;
using WinChime.Core.Sounds;

namespace WinChime.Core.Tests;

public sealed class SoundSchemeServiceTests : IDisposable
{
    private readonly ScratchRegistry _reg = new();
    private readonly SoundSchemeService _service;

    public SoundSchemeServiceTests()
    {
        _service = _reg.CreateService();

        _reg.SeedApp(".Default", "Windows");
        _reg.SeedApp("Explorer", "File Explorer");

        _reg.SeedEvent(".Default", "Notification.Default",
            current: @"%SystemRoot%\media\Windows Notify.wav",
            defaultValue: @"%SystemRoot%\media\Windows Notify.wav");

        _reg.SeedEvent(".Default", "SystemHand",
            current: @"%SystemRoot%\media\Windows Critical Stop.wav",
            defaultValue: @"%SystemRoot%\media\Windows Critical Stop.wav");

        _reg.SeedEvent("Explorer", "EmptyRecycleBin",
            current: @"%SystemRoot%\media\Windows Recycle.wav",
            defaultValue: @"%SystemRoot%\media\Windows Recycle.wav");

        _reg.SeedLabel("Notification.Default", "Notification");
    }

    public void Dispose() => _reg.Dispose();

    // ------------------------------------------------------------------ reading --

    [Fact]
    public void LoadEvents_ReturnsSeededEventsAcrossApps()
    {
        var events = _service.LoadEvents();

        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.AppKey == ".Default" && e.EventKey == "Notification.Default");
        Assert.Contains(events, e => e.AppKey == "Explorer" && e.EventKey == "EmptyRecycleBin");
    }

    [Fact]
    public void LoadEvents_UsesFriendlyNamesFromRegistry()
    {
        var events = _service.LoadEvents();

        var notification = events.Single(e => e.EventKey == "Notification.Default");
        Assert.Equal("Notification", notification.EventDisplayName);
        Assert.Equal("Windows", notification.AppDisplayName);
    }

    [Fact]
    public void LoadEvents_WithoutLabel_FallsBackToPrettifiedKey()
    {
        var events = _service.LoadEvents();

        // No EventLabels entry was seeded for this one.
        var recycle = events.Single(e => e.EventKey == "EmptyRecycleBin");
        Assert.Equal("Empty Recycle Bin", recycle.EventDisplayName);
    }

    [Fact]
    public void LoadEvents_PutsWindowsAppFirst()
    {
        var events = _service.LoadEvents();

        Assert.Equal(".Default", events[0].AppKey);
    }

    [Fact]
    public void LoadEvents_SkipsKeysThatAreNeitherCurrentNorDefault()
    {
        // A scheme-storage subkey with no .Current or .Default is not user-assignable.
        _reg.SeedSchemeValue(".Default", "PhantomEvent", "SomeScheme", "x.wav");

        Assert.DoesNotContain(_service.LoadEvents(), e => e.EventKey == "PhantomEvent");
    }

    // ------------------------------------------------------------------ writing --

    [Fact]
    public void SetSound_ThenLoadEvents_RoundTrips()
    {
        _service.SetSound(".Default", "SystemHand", @"C:\sounds\custom.wav");

        var evt = _service.LoadEvents().Single(e => e.EventKey == "SystemHand");
        Assert.Equal(@"C:\sounds\custom.wav", evt.CurrentPathRaw);
        Assert.True(evt.HasSound);
    }

    [Fact]
    public void SetSound_Null_SilencesTheEvent()
    {
        _service.SetSound(".Default", "SystemHand", null);

        var evt = _service.LoadEvents().Single(e => e.EventKey == "SystemHand");
        Assert.False(evt.HasSound);
        Assert.Equal(string.Empty, _reg.ReadCurrent(".Default", "SystemHand"));
    }

    /// <summary>
    /// Windows stores paths containing environment variables as REG_EXPAND_SZ and plain
    /// paths as REG_SZ. Writing the wrong kind works but leaves values that look foreign
    /// next to the shipped defaults.
    /// </summary>
    [Theory]
    [InlineData(@"%SystemRoot%\media\x.wav", RegistryValueKind.ExpandString)]
    [InlineData(@"C:\sounds\x.wav", RegistryValueKind.String)]
    public void SetSound_UsesCorrectRegistryValueKind(string path, RegistryValueKind expected)
    {
        _service.SetSound(".Default", "SystemHand", path);

        Assert.Equal(expected, _reg.ReadCurrentKind(".Default", "SystemHand"));
    }

    [Fact]
    public void SetSound_CreatesCurrentKeyWhenAbsent()
    {
        _reg.SeedEvent(".Default", "BrandNew", current: null, defaultValue: @"C:\d.wav");

        var result = _service.SetSound(".Default", "BrandNew", @"C:\new.wav");

        Assert.True(result.Success);
        Assert.Equal(@"C:\new.wav", _reg.ReadCurrent(".Default", "BrandNew"));
    }

    [Fact]
    public void RestoreDefault_PutsBackTheShippedValue()
    {
        _service.SetSound(".Default", "SystemHand", @"C:\sounds\custom.wav");

        var result = _service.RestoreDefault(".Default", "SystemHand");

        Assert.True(result.Success);
        Assert.Equal(@"%SystemRoot%\media\Windows Critical Stop.wav",
            _reg.ReadCurrent(".Default", "SystemHand"));
    }

    [Fact]
    public void RestoreDefault_WithoutRecordedDefault_Fails()
    {
        _reg.SeedEvent(".Default", "NoDefault", current: @"C:\a.wav", defaultValue: null);

        var result = _service.RestoreDefault(".Default", "NoDefault");

        Assert.False(result.Success);
    }

    [Fact]
    public void IsCustomised_TracksDivergenceFromDefault()
    {
        var before = _service.LoadEvents().Single(e => e.EventKey == "SystemHand");
        Assert.False(before.IsCustomised);

        _service.SetSound(".Default", "SystemHand", @"C:\sounds\custom.wav");

        var after = _service.LoadEvents().Single(e => e.EventKey == "SystemHand");
        Assert.True(after.IsCustomised);
    }

    [Fact]
    public void IsBroken_FlagsAssignmentsWhoseFileIsGone()
    {
        _service.SetSound(".Default", "SystemHand", @"C:\definitely\not\here.wav");

        var evt = _service.LoadEvents().Single(e => e.EventKey == "SystemHand");
        Assert.True(evt.IsBroken);
        Assert.Equal("Missing", evt.StatusText);
    }

    // ------------------------------------------------------------------ schemes --

    [Fact]
    public void SaveThenApplyScheme_RestoresTheSavedAssignments()
    {
        _service.SetSound(".Default", "SystemHand", @"C:\sounds\saved.wav");
        _service.SaveCurrentAsScheme("My Scheme");

        _service.SetSound(".Default", "SystemHand", @"C:\sounds\changed.wav");
        _service.ApplyScheme("My Scheme");

        Assert.Equal(@"C:\sounds\saved.wav", _reg.ReadCurrent(".Default", "SystemHand"));
    }

    [Fact]
    public void SaveCurrentAsScheme_RegistersTheSchemeName()
    {
        _service.SaveCurrentAsScheme("My Scheme");

        var schemes = _service.ListSchemes();
        Assert.Contains(schemes, s => s.DisplayName == "My Scheme");
        Assert.Equal("My Scheme", _service.GetActiveSchemeKey());
    }

    [Theory]
    [InlineData(".Default")]
    [InlineData(".None")]
    public void SaveCurrentAsScheme_RefusesReservedNames(string reserved)
    {
        var result = _service.SaveCurrentAsScheme(reserved);

        Assert.False(result.Success);
    }

    [Fact]
    public void SaveCurrentAsScheme_RefusesNameWithNoUsableCharacters()
    {
        var result = _service.SaveCurrentAsScheme(@"\/:*?");

        Assert.False(result.Success);
    }

    [Fact]
    public void ApplyScheme_None_SilencesEveryEvent()
    {
        _service.ApplyScheme(SoundSchemeService.NoSoundsScheme);

        Assert.All(_service.LoadEvents(), e => Assert.False(e.HasSound));
    }

    /// <summary>
    /// Documented behaviour: a scheme that does not mention an event leaves it alone rather
    /// than silencing it. Silencing would be a destructive surprise for a partial scheme.
    /// </summary>
    [Fact]
    public void ApplyScheme_LeavesEventsTheSchemeDoesNotMentionUntouched()
    {
        _reg.SeedSchemeName("Partial", "Partial");
        _reg.SeedSchemeValue(".Default", "SystemHand", "Partial", @"C:\sounds\partial.wav");

        _service.ApplyScheme("Partial");

        Assert.Equal(@"C:\sounds\partial.wav", _reg.ReadCurrent(".Default", "SystemHand"));
        Assert.Equal(@"%SystemRoot%\media\Windows Notify.wav",
            _reg.ReadCurrent(".Default", "Notification.Default"));
    }

    [Fact]
    public void ApplyScheme_SetsTheActiveSchemeKey()
    {
        _reg.SeedSchemeName("Partial", "Partial");
        _reg.SeedSchemeValue(".Default", "SystemHand", "Partial", @"C:\x.wav");

        _service.ApplyScheme("Partial");

        Assert.Equal("Partial", _service.GetActiveSchemeKey());
    }

    [Fact]
    public void DeleteScheme_RemovesPerEventKeysAndTheName()
    {
        _service.SaveCurrentAsScheme("Doomed");
        Assert.True(_reg.SchemeKeyExists(".Default", "SystemHand", "Doomed"));

        var result = _service.DeleteScheme("Doomed");

        Assert.True(result.Success);
        Assert.False(_reg.SchemeKeyExists(".Default", "SystemHand", "Doomed"));
        Assert.DoesNotContain(_service.ListSchemes(), s => s.Key == "Doomed");
    }

    [Fact]
    public void DeleteScheme_ResetsActiveSchemeWhenTheActiveOneIsDeleted()
    {
        _service.SaveCurrentAsScheme("Doomed");
        Assert.Equal("Doomed", _service.GetActiveSchemeKey());

        _service.DeleteScheme("Doomed");

        Assert.Equal(SoundSchemeService.WindowsDefaultScheme, _service.GetActiveSchemeKey());
    }

    [Theory]
    [InlineData(".Default")]
    [InlineData(".None")]
    public void DeleteScheme_RefusesBuiltIns(string builtIn)
    {
        Assert.False(_service.DeleteScheme(builtIn).Success);
    }

    [Fact]
    public void GetActiveSchemeKey_DefaultsToWindowsDefaultWhenUnset()
    {
        Assert.Equal(SoundSchemeService.WindowsDefaultScheme, _service.GetActiveSchemeKey());
    }

    // ------------------------------------------------------- snapshot / restore --

    [Fact]
    public void CaptureThenRestoreAssignments_RoundTrips()
    {
        var snapshot = _service.CaptureAssignments();

        _service.SetSound(".Default", "SystemHand", @"C:\sounds\different.wav");
        _service.SetSound("Explorer", "EmptyRecycleBin", null);

        var result = _service.RestoreAssignments(snapshot);

        Assert.True(result.Success);
        Assert.Equal(@"%SystemRoot%\media\Windows Critical Stop.wav",
            _reg.ReadCurrent(".Default", "SystemHand"));
        Assert.Equal(@"%SystemRoot%\media\Windows Recycle.wav",
            _reg.ReadCurrent("Explorer", "EmptyRecycleBin"));
    }

    [Fact]
    public void CaptureAssignments_KeysAreAppBackslashEvent()
    {
        var snapshot = _service.CaptureAssignments();

        Assert.Contains(@".Default\SystemHand", snapshot.Keys);
        Assert.Contains(@"Explorer\EmptyRecycleBin", snapshot.Keys);
    }
}
