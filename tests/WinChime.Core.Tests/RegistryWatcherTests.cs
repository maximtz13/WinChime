using Microsoft.Win32;
using WinChime.Core.Interop;

namespace WinChime.Core.Tests;

public sealed class RegistryWatcherTests : IDisposable
{
    private readonly ScratchRegistry _reg = new();

    public void Dispose() => _reg.Dispose();

    /// <summary>
    /// Generous, because this waits on a real kernel notification rather than a mock. It is
    /// an upper bound for a stuck test, not an expected duration; the signal normally
    /// arrives in milliseconds.
    /// </summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Watcher_RaisesChanged_WhenAValueUnderTheKeyIsWritten()
    {
        using var signalled = new ManualResetEventSlim(false);
        using var watcher = new RegistryWatcher(RegistryHive.CurrentUser, _reg.Root);

        watcher.Changed += (_, _) => signalled.Set();
        watcher.Start();

        // Start returns as soon as the thread exists, not once it is listening. Writing
        // before it arms would race and fail intermittently.
        Assert.True(watcher.WaitUntilArmed(SignalTimeout), "Watcher never armed.");

        _reg.SeedApp(".Default", "Windows");

        Assert.True(signalled.Wait(SignalTimeout),
            $"No change notification arrived within {SignalTimeout.TotalSeconds}s. " +
            $"Watcher failure: {watcher.FailureReason ?? "none reported"}");
    }

    [Fact]
    public void Watcher_RaisesChanged_ForChangesNestedInSubkeys()
    {
        _reg.SeedApp(".Default", "Windows");
        _reg.SeedEvent(".Default", "SystemHand", current: @"C:\a.wav", defaultValue: @"C:\a.wav");

        using var signalled = new ManualResetEventSlim(false);
        using var watcher = new RegistryWatcher(RegistryHive.CurrentUser, _reg.Root, watchSubtree: true);

        watcher.Changed += (_, _) => signalled.Set();
        watcher.Start();

        // Start returns as soon as the thread exists, not once it is listening. Writing
        // before it arms would race and fail intermittently.
        Assert.True(watcher.WaitUntilArmed(SignalTimeout), "Watcher never armed.");

        // Deep in the tree, which is where real sound assignments actually live.
        _reg.CreateService().SetSound(".Default", "SystemHand", @"C:\changed.wav");

        Assert.True(signalled.Wait(SignalTimeout),
            $"No notification for a nested change. Watcher failure: {watcher.FailureReason ?? "none reported"}");
    }

    /// <summary>
    /// The notification is one-shot and has to be re-armed. Getting that wrong yields a
    /// watcher that fires exactly once and then goes quiet, which is worse than none at all
    /// because it looks like it is working.
    /// </summary>
    [Fact]
    public void Watcher_KeepsFiring_AfterTheFirstNotification()
    {
        var count = 0;
        using var secondSignal = new ManualResetEventSlim(false);
        using var watcher = new RegistryWatcher(RegistryHive.CurrentUser, _reg.Root);

        watcher.Changed += (_, _) =>
        {
            if (Interlocked.Increment(ref count) >= 2) secondSignal.Set();
        };

        watcher.Start();
        Assert.True(watcher.WaitUntilArmed(SignalTimeout), "Watcher never armed.");

        var service = _reg.CreateService();
        _reg.SeedApp(".Default", "Windows");
        _reg.SeedEvent(".Default", "SystemHand", current: @"C:\a.wav", defaultValue: @"C:\a.wav");

        // Several writes, spaced so they cannot all collapse into one notification.
        for (var i = 0; i < 5 && !secondSignal.IsSet; i++)
        {
            service.SetSound(".Default", "SystemHand", $@"C:\change{i}.wav");
            secondSignal.Wait(TimeSpan.FromMilliseconds(400));
        }

        Assert.True(secondSignal.IsSet,
            $"Watcher fired {count} time(s); it did not re-arm. Failure: {watcher.FailureReason ?? "none reported"}");
    }

    [Fact]
    public void Watcher_OnAKeyThatDoesNotExist_FailsQuietlyWithAReason()
    {
        using var watcher = new RegistryWatcher(
            RegistryHive.CurrentUser, $@"Software\WinChime.Tests\definitely-absent-{Guid.NewGuid():N}");

        watcher.Start();

        // The watcher thread should notice and stop rather than throwing into the app.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (watcher.FailureReason is null && DateTime.UtcNow < deadline) Thread.Sleep(50);

        Assert.NotNull(watcher.FailureReason);
    }

    [Fact]
    public void Dispose_IsSafeToCallWithoutStarting()
    {
        var watcher = new RegistryWatcher(RegistryHive.CurrentUser, _reg.Root);
        watcher.Dispose();
        watcher.Dispose();   // and idempotent
    }

    [Fact]
    public void Dispose_StopsTheWatcher()
    {
        var watcher = new RegistryWatcher(RegistryHive.CurrentUser, _reg.Root);
        var fired = 0;

        watcher.Changed += (_, _) => Interlocked.Increment(ref fired);
        watcher.Start();

        // Arm it first, otherwise disposing before it ever listened would make this pass
        // without proving anything about Dispose.
        Assert.True(watcher.WaitUntilArmed(SignalTimeout), "Watcher never armed.");

        watcher.Dispose();

        var afterDispose = fired;
        _reg.SeedApp(".Default", "Windows");
        Thread.Sleep(500);

        Assert.Equal(afterDispose, fired);
    }
}
