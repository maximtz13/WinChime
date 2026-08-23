using Microsoft.Win32;

namespace WinChime.Core.Interop;

/// <summary>
/// Raises an event when a registry subtree changes.
///
/// Exists because the Sound control panel, another tweaking tool, or a second copy of this
/// app can all change sound assignments while the window is open. Without this the list
/// silently drifts out of date and the user is looking at a lie.
///
/// There is no managed API for this. RegNotifyChangeKeyValue is one-shot: it signals once
/// and must be re-armed, which is why this runs a dedicated thread that re-registers after
/// every notification rather than using the thread pool.
///
/// Failures here are deliberately non-fatal. A stale list is a far better outcome than a
/// crash, so the watcher stops quietly and the app carries on working manually.
/// </summary>
public sealed class RegistryWatcher : IDisposable
{
    private readonly RegistryHive _hive;
    private readonly string _subKey;
    private readonly bool _watchSubtree;

    private readonly ManualResetEvent _stopRequested = new(false);
    private readonly AutoResetEvent _changeSignalled = new(false);

    /// <summary>
    /// Set once the first notification has actually been registered. Start returns as soon
    /// as the thread is created, so there is a real window in which a change would be
    /// missed. Exposing it beats pretending it does not exist.
    /// </summary>
    private readonly ManualResetEventSlim _armed = new(false);

    private Thread? _thread;
    private bool _disposed;

    /// <summary>
    /// Raised on the watcher thread, not the UI thread. Handlers must marshal themselves.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Set when the watcher gave up, so callers can explain why refresh is manual.</summary>
    public string? FailureReason { get; private set; }

    public RegistryWatcher(RegistryHive hive, string subKey, bool watchSubtree = true)
    {
        _hive = hive;
        _subKey = subKey;
        _watchSubtree = watchSubtree;
    }

    public void Start()
    {
        if (_thread is not null || _disposed) return;

        _thread = new Thread(WatchLoop)
        {
            IsBackground = true,   // must never hold the process open
            Name = "WinChime registry watcher",
        };

        _thread.Start();
    }

    /// <summary>
    /// Blocks until the watcher is actually listening, or the timeout elapses. Returns false
    /// if it never armed, which also covers the case where the key could not be opened.
    /// </summary>
    public bool WaitUntilArmed(TimeSpan timeout) => _armed.Wait(timeout);

    private void WatchLoop()
    {
        const int filter = NativeMethods.REG_NOTIFY_CHANGE_NAME
                           | NativeMethods.REG_NOTIFY_CHANGE_LAST_SET
                           | NativeMethods.REG_NOTIFY_THREAD_AGNOSTIC;

        try
        {
            while (!_stopRequested.WaitOne(0))
            {
                using var root = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
                using var key = root.OpenSubKey(_subKey);

                if (key is null)
                {
                    FailureReason = $"Registry key {_subKey} could not be opened.";
                    return;
                }

                // The key handle has to stay valid until the notification fires, which is
                // why the wait happens inside this scope rather than after it.
                var result = NativeMethods.RegNotifyChangeKeyValue(
                    key.Handle, _watchSubtree, filter, _changeSignalled.SafeWaitHandle, true);

                if (result != 0)
                {
                    FailureReason = $"RegNotifyChangeKeyValue failed with code {result}.";
                    return;
                }

                // Only now can a change actually be observed.
                _armed.Set();

                var signalled = WaitHandle.WaitAny(new WaitHandle[] { _stopRequested, _changeSignalled });

                if (signalled == 0) return;   // stop requested

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            // A watcher that dies must not take the app with it.
            FailureReason = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopRequested.Set();

        // Brief join only. The thread is background, so a stuck wait cannot block exit, and
        // blocking the UI thread on shutdown would be worse than leaking one thread.
        _thread?.Join(TimeSpan.FromSeconds(2));

        _stopRequested.Dispose();
        _changeSignalled.Dispose();
        _armed.Dispose();
    }
}
