using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;   // not implicit in WPF projects: System.IO.Path would clash with Shapes.Path
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinChime.Core.Cursors;
using WinChime.Core.Elevation;
using WinChime.Core.Interop;
using WinChime.Core.Model;
using WinChime.Core.Personalization;
using WinChime.Core.Safety;
using WinChime.Core.Sounds;
using WinChime.Core.Startup;

namespace WinChime.App;

public partial class MainWindow : Window
{
    private readonly SoundSchemeService _sounds = new();
    private readonly StartupSoundService _startupSound = new();
    private readonly LogonChimeService _logonChime = new();
    private readonly WallpaperService _wallpaper = new();
    private readonly LockScreenService _lockScreen = new();
    private readonly BackupService _backups;

    // Note: RestorePointService is not held here. It always runs inside the elevated
    // child process via ElevationHelper, never in the UI process.

    private readonly ObservableCollection<SoundEvent> _events = new();
    private readonly ObservableCollection<BackupManifest> _backupItems = new();

    private readonly AccentColorService _accent = new();

    /// <summary>Single-step undo for the accent, same shape as the cursor undo.</summary>
    private Dictionary<string, string>? _accentUndo;

    private readonly CursorSchemeService _cursors = new();

    private readonly ObservableCollection<CursorEntry> _cursorEntries = new();

    /// <summary>Single-step undo for cursors: every change rewrites the same set of values.</summary>
    private Dictionary<string, string>? _cursorUndo;
    private string? _cursorUndoDescription;

    private readonly SoundEditHistory _history = new();

    private ICollectionView? _eventView;
    private SystemInfo _systemInfo = new();

    /// <summary>
    /// Watches HKCU\AppEvents so the list does not silently drift when the Sound control
    /// panel, another tool, or a second copy of this app changes something.
    /// </summary>
    private RegistryWatcher? _watcher;
    private DispatcherTimer? _refreshTimer;

    /// <summary>
    /// When we last wrote to the registry ourselves, so a refresh triggered by our own edit
    /// is not announced to the user as an external change.
    /// </summary>
    private DateTime _lastSelfWriteUtc = DateTime.MinValue;

    /// <summary>The built-in chime, extracted from imageres.dll on first use. Read-only.</summary>
    private ExtractedChime? _systemChime;

    /// <summary>Guards the built-in chime checkbox while we set it programmatically.</summary>
    private bool _suppressChimeToggle;

    public MainWindow()
    {
        InitializeComponent();

        _backups = new BackupService(_sounds);

        EventList.ItemsSource = _events;
        _eventView = CollectionViewSource.GetDefaultView(_events);
        _eventView.Filter = FilterEvent;

        BackupList.ItemsSource = _backupItems;
        CursorList.ItemsSource = _cursorEntries;

        foreach (var style in Enum.GetValues<WallpaperStyle>())
            WallpaperStyleCombo.Items.Add(style.ToString());
        WallpaperStyleCombo.SelectedIndex = 0;

        BuildAccentPresets();

        ChimeDelaySlider.ValueChanged += (_, _) => UpdateChimeDelayText();
        UpdateChimeDelayText();

        // The title bar is drawn by DWM, not WPF, so it needs asking separately. Track waits
        // for the window handle, which does not exist yet at this point.
        ThemeManager.Track(this);
        ShowCurrentThemePreference();

        Loaded += (_, _) => InitialLoad();
    }

    // =================================================================== theme ==

    /// <summary>
    /// Guards the appearance switch while it is being set to match the stored preference.
    /// Checked fires when the segment is set programmatically as well as when it is clicked.
    /// </summary>
    private bool _suppressThemeToggle;

    private void ShowCurrentThemePreference()
    {
        _suppressThemeToggle = true;

        var selected = ThemeManager.Preference switch
        {
            ThemePreference.Light => ThemeLightOption,
            ThemePreference.Dark => ThemeDarkOption,
            _ => ThemeSystemOption,
        };

        selected.IsChecked = true;

        _suppressThemeToggle = false;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeToggle) return;

        if (sender is not RadioButton { Tag: string name }) return;
        if (!Enum.TryParse<ThemePreference>(name, out var preference)) return;

        var result = ThemeManager.SetPreference(preference);

        // The theme is applied either way. Only the fact that it will not be remembered is
        // worth telling the user about.
        SetStatus(result.Success
            ? preference == ThemePreference.System
                ? $"Appearance follows Windows, currently {ThemeManager.Current.ToString().ToLowerInvariant()}."
                : $"Appearance set to {name.ToLowerInvariant()}."
            : $"Appearance changed, but it could not be saved: {result.Message}");
    }

    private void InitialLoad()
    {
        ReloadSchemes();
        ReloadEvents();
        ReloadCursorSchemes();
        ReloadCursors();
        RefreshStartupTab();
        RefreshDesktopTab();
        RefreshAccent();
        RefreshSystemTab();
        RefreshBackups();
        StartRegistryWatcher();

        SetStatus($"Loaded {_events.Count} sound events.");
    }

    // ============================================================ live refresh ==

    private void StartRegistryWatcher()
    {
        // A single user action produces several registry writes, so coalesce them rather
        // than rebuilding the list once per key.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += RefreshTimer_Tick;

        try
        {
            _watcher = new RegistryWatcher(Microsoft.Win32.RegistryHive.CurrentUser, "AppEvents");

            // Raised on the watcher thread; hop to the UI thread before touching anything.
            _watcher.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            }));

            _watcher.Start();

            // Start only creates the thread. If it never actually arms, the list is not
            // live and the user should be told rather than left trusting a stale view.
            var watcher = _watcher;
            Task.Run(() =>
            {
                if (watcher.WaitUntilArmed(TimeSpan.FromSeconds(5))) return;

                Dispatcher.BeginInvoke(new Action(() => SetStatus(
                    $"Live refresh is not active ({watcher.FailureReason ?? "the watcher did not start"}). " +
                    "Use Refresh on the System tab after changing sounds elsewhere.")));
            });
        }
        catch (Exception ex)
        {
            // Live refresh is a convenience. Losing it must not cost the user the app.
            _watcher = null;
            SetStatus($"Live refresh unavailable ({ex.Message}). Use Refresh on the System tab.");
        }

        Closed += (_, _) =>
        {
            _refreshTimer?.Stop();
            _watcher?.Dispose();
        };
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();

        var external = DateTime.UtcNow - _lastSelfWriteUtc > TimeSpan.FromSeconds(2);

        ReloadEvents();
        ReloadSchemes();

        if (external)
            SetStatus("Sound settings changed outside WinChime. The list has been refreshed.");
    }

    /// <summary>Call immediately before or after writing to the registry ourselves.</summary>
    private void MarkSelfWrite() => _lastSelfWriteUtc = DateTime.UtcNow;

    // =================================================================== undo ==

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;

        UndoButton.ToolTip = _history.NextUndoDescription is { } undo ? $"Undo: {undo}" : null;
        RedoButton.ToolTip = _history.NextRedoDescription is { } redo ? $"Redo: {redo}" : null;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var edit = _history.Undo();
        if (edit is null) return;

        MarkSelfWrite();
        var result = _sounds.RestoreAssignments(edit.Before);

        SetStatus(result.Success ? $"Undone: {edit.Description}" : result.Message);
        if (!result.Success) Report(result);

        ReloadEvents();
        UpdateUndoButtons();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        var edit = _history.Redo();
        if (edit is null) return;

        MarkSelfWrite();
        var result = _sounds.RestoreAssignments(edit.After);

        SetStatus(result.Success ? $"Redone: {edit.Description}" : result.Message);
        if (!result.Success) Report(result);

        ReloadEvents();
        UpdateUndoButtons();
    }

    /// <summary>Assigns a sound to one event and records it as a single undoable step.</summary>
    private void ApplyAndRecord(SoundEvent soundEvent, string? newValue, string description)
    {
        var before = soundEvent.CurrentPathRaw;

        MarkSelfWrite();
        var result = _sounds.SetSound(soundEvent.AppKey, soundEvent.EventKey, newValue);

        if (result.Success)
            _history.RecordSingle(soundEvent.AppKey, soundEvent.EventKey, before, newValue, description);

        Report(result);
        ReloadEvents();
        UpdateUndoButtons();
    }

    /// <summary>Records a bulk change by diffing snapshots taken either side of it.</summary>
    private void RecordBulk(
        string description,
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var edit = SoundEditHistory.DiffSnapshots(description, before, after);
        if (edit is not null) _history.Record(edit);

        UpdateUndoButtons();
    }

    // =========================================================== shared helpers ==

    private void SetStatus(string message) => StatusText.Text = message;

    private void Report(OperationResult result)
    {
        SetStatus(result.Message);

        if (!result.Success)
        {
            MessageBox.Show(
                result.Message,
                result.NeedsElevation ? "Administrator rights needed" : "That did not work",
                MessageBoxButton.OK,
                result.NeedsElevation ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private static string? PickFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private const string WavFilter = "Wave audio (*.wav)|*.wav|All files (*.*)|*.*";
    private const string ImageFilter =
        "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*";
    // A pack is listed first because it is almost always the right choice: a bare .json
    // scheme only works on a machine that already has identical files at identical paths.
    private const string SchemeSaveFilter =
        "WinChime sound pack, includes audio (*.winchimepack)|*.winchimepack"
        + "|WinChime scheme, paths only (*.winchime.json)|*.winchime.json";

    private const string SchemeOpenFilter =
        "Schemes and packs (*.winchimepack;*.winchime.json;*.json)|*.winchimepack;*.winchime.json;*.json"
        + "|Sound pack (*.winchimepack)|*.winchimepack"
        + "|Scheme (*.json)|*.json"
        + "|All files (*.*)|*.*";

    /// <summary>Surfaces non-fatal problems without turning them into a modal for every item.</summary>
    private void ShowWarnings(string title, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;

        MessageBox.Show(
            string.Join(Environment.NewLine, warnings.Take(20))
            + (warnings.Count > 20 ? $"{Environment.NewLine}… and {warnings.Count - 20} more." : ""),
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ================================================================== sounds ==

    private void ReloadSchemes()
    {
        var active = _sounds.GetActiveSchemeKey();

        SchemeCombo.Items.Clear();
        foreach (var scheme in _sounds.ListSchemes())
        {
            SchemeCombo.Items.Add(scheme);
            if (scheme.Key == active) SchemeCombo.SelectedItem = scheme;
        }

        if (SchemeCombo.SelectedItem is null && SchemeCombo.Items.Count > 0)
            SchemeCombo.SelectedIndex = 0;
    }

    private void ReloadEvents()
    {
        // Selection is restored by key because the list is rebuilt from the registry
        // after every change rather than mutated in place.
        var previousKey = EventList.SelectedItem is SoundEvent selected
            ? $@"{selected.AppKey}\{selected.EventKey}"
            : null;

        _events.Clear();
        foreach (var soundEvent in _sounds.LoadEvents()) _events.Add(soundEvent);

        _eventView?.Refresh();

        if (previousKey is not null)
        {
            EventList.SelectedItem = _events.FirstOrDefault(
                e => string.Equals($@"{e.AppKey}\{e.EventKey}", previousKey, StringComparison.OrdinalIgnoreCase));
        }

        UpdateSelectionDetails();
    }

    private bool FilterEvent(object item)
    {
        if (item is not SoundEvent soundEvent) return false;

        if (OnlyCustomised.IsChecked == true && !soundEvent.IsCustomised) return false;
        if (OnlyBroken.IsChecked == true && !soundEvent.IsBroken) return false;

        var text = FilterBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        return soundEvent.EventDisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || soundEvent.AppDisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || soundEvent.EventKey.Contains(text, StringComparison.OrdinalIgnoreCase)
            || soundEvent.SoundFileName.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => _eventView?.Refresh();

    private void Filter_Changed(object sender, RoutedEventArgs e) => _eventView?.Refresh();

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionDetails();

    private void EventList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PreviewSound_Click(sender, e);

    private void UpdateSelectionDetails()
    {
        if (EventList.SelectedItem is not SoundEvent soundEvent)
        {
            SelectedTitle.Text = "Nothing selected";
            SelectedPath.Text = "";
            WaveInfoText.Text = "Select an event to inspect its audio file.";
            WaveWarnings.Text = "";
            return;
        }

        SelectedTitle.Text = soundEvent.DisplayLabel;
        SelectedPath.Text = soundEvent.CurrentPath ?? "(no sound assigned)";

        if (!soundEvent.HasSound)
        {
            WaveInfoText.Text = "This event is silent.";
            WaveWarnings.Text = "";
            return;
        }

        if (soundEvent.IsBroken)
        {
            WaveInfoText.Text = "The assigned file no longer exists.";
            WaveWarnings.Text =
                "Windows gives no error for this: the event simply makes no sound. " +
                "Pick a new file or restore the default.";
            return;
        }

        var info = WaveFile.Inspect(soundEvent.CurrentPath!);
        WaveInfoText.Text = info.Summary;
        WaveWarnings.Text = string.Join(Environment.NewLine + Environment.NewLine, info.Warnings);
    }

    /// <summary>
    /// Turns whatever the user picked into something Windows can actually play, converting
    /// when necessary. Returns null when the user backs out or conversion fails.
    ///
    /// This is the single choke point for assigning audio, so an MP3 gets the same
    /// treatment whether it is destined for a system event or the logon chime.
    /// </summary>
    private string? ResolveAssignableSound(string path)
    {
        if (!AudioTranscoder.NeedsConversion(path)) return path;

        var info = WaveFile.Inspect(path);
        var described = info.IsValid
            ? info.FormatName
            : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        // Without Media Foundation there is nothing to offer but the old warning.
        if (!AudioTranscoder.IsAvailable)
        {
            var proceed = MessageBox.Show(
                $"{Path.GetFileName(path)} is {described}, not uncompressed PCM, and audio conversion " +
                "is unavailable on this Windows installation.\n\n" +
                "Windows will accept the assignment but the event will play silently.\n\n" +
                "Assign it anyway?",
                "Cannot convert on this PC",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return proceed == MessageBoxResult.Yes ? path : null;
        }

        var options = ConvertOptionsDialog.Ask(this, path, info, conversionRequired: true);
        if (options is null) return null;

        return RunConversion(path, options);
    }

    private string? RunConversion(string path, TranscodeOptions options)
    {
        var result = AudioTranscoder.ConvertIntoLibrary(path, destinationFolder: null, options);

        if (!result.Success)
        {
            Report(OperationResult.Fail(result.Message));
            return null;
        }

        SetStatus(result.Message);
        return result.OutputPath;
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not SoundEvent soundEvent)
        {
            SetStatus("Select an event first.");
            return;
        }

        var picked = PickFile(
            $"Choose a sound for {soundEvent.EventDisplayName}",
            AudioTranscoder.OpenFileFilter);

        if (picked is null) return;

        var path = ResolveAssignableSound(picked);
        if (path is null) return;

        ApplyAndRecord(soundEvent, path, $"Set {soundEvent.EventDisplayName}");
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not SoundEvent soundEvent) return;

        if (!soundEvent.HasSound)
        {
            SetStatus("That event is silent.");
            return;
        }

        if (soundEvent.IsBroken)
        {
            SetStatus("The assigned file is missing.");
            return;
        }

        SetStatus(SoundPreview.Play(soundEvent.CurrentPath!)
            ? $"Playing {soundEvent.SoundFileName}."
            : "Windows could not play that file.");
    }

    private void StopSound_Click(object sender, RoutedEventArgs e)
    {
        SoundPreview.Stop();
        SetStatus("Stopped.");
    }

    private void SilenceSound_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not SoundEvent soundEvent) return;

        ApplyAndRecord(soundEvent, null, $"Silence {soundEvent.EventDisplayName}");
    }

    private void RestoreSound_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not SoundEvent soundEvent) return;

        // Goes through RestoreDefault rather than ApplyAndRecord so the "no recorded
        // default" case keeps its own specific error, but records the same undo step.
        var before = soundEvent.CurrentPathRaw;

        MarkSelfWrite();
        var result = _sounds.RestoreDefault(soundEvent.AppKey, soundEvent.EventKey);

        if (result.Success)
        {
            _history.RecordSingle(
                soundEvent.AppKey, soundEvent.EventKey,
                before, soundEvent.DefaultPathRaw,
                $"Restore default for {soundEvent.EventDisplayName}");
        }

        Report(result);
        ReloadEvents();
        UpdateUndoButtons();
    }

    /// <summary>
    /// Trim or normalise a sound that already works. The result is written as a new file
    /// rather than edited in place, because the source may be a Windows default or a file
    /// another event is also pointing at.
    /// </summary>
    private void AdjustSound_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not SoundEvent soundEvent)
        {
            SetStatus("Select an event first.");
            return;
        }

        var current = soundEvent.CurrentPath;

        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
        {
            SetStatus("That event has no playable sound to adjust.");
            return;
        }

        if (!AudioTranscoder.IsAvailable)
        {
            Report(OperationResult.Fail(
                "Audio processing is unavailable on this Windows installation, so sounds cannot be " +
                "trimmed or normalised here."));
            return;
        }

        var options = ConvertOptionsDialog.Ask(this, current, WaveFile.Inspect(current), conversionRequired: false);
        if (options is null) return;

        var path = RunConversion(current, options);
        if (path is null) return;

        ApplyAndRecord(soundEvent, path, $"Adjust {soundEvent.EventDisplayName}");
    }

    private void ApplyScheme_Click(object sender, RoutedEventArgs e)
    {
        if (SchemeCombo.SelectedItem is not SchemeListItem scheme) return;

        // Automatic backup before any bulk change; this is the whole reason the
        // backup path exists and it must never be conditional on a checkbox.
        var (backupResult, _) = _backups.CreateSoundBackup($"Before applying scheme: {scheme.DisplayName}");
        if (!backupResult.Success)
        {
            var proceed = MessageBox.Show(
                $"Could not create a backup first:\n\n{backupResult.Message}\n\nApply the scheme anyway?",
                "Backup failed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (proceed != MessageBoxResult.Yes) return;
        }

        var before = _sounds.CaptureAssignments();

        MarkSelfWrite();
        var result = _sounds.ApplyScheme(scheme.Key);
        Report(result);

        if (result.Success)
            RecordBulk($"Apply scheme {scheme.DisplayName}", before, _sounds.CaptureAssignments());

        ReloadEvents();
        RefreshBackups();
    }

    private void SaveScheme_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(this, "Name for this sound scheme:", "Save scheme", "My scheme");
        if (name is null) return;

        Report(_sounds.SaveCurrentAsScheme(name));
        ReloadSchemes();
    }

    private void DeleteScheme_Click(object sender, RoutedEventArgs e)
    {
        if (SchemeCombo.SelectedItem is not SchemeListItem scheme) return;

        if (scheme.IsBuiltIn)
        {
            SetStatus("Built-in Windows schemes cannot be deleted.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete the scheme \"{scheme.DisplayName}\"?\n\nCurrent sound assignments are not changed.",
            "Delete scheme",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        Report(_sounds.DeleteScheme(scheme.Key));
        ReloadSchemes();
    }

    private void ExportScheme_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(this, "Name to record inside the exported file:", "Export scheme", "My scheme");
        if (name is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export sound scheme",
            Filter = SchemeSaveFilter,
            FileName = name + SoundPackService.PackExtension,
            DefaultExt = SoundPackService.PackExtension,
        };

        if (dialog.ShowDialog() != true) return;

        var export = _sounds.BuildExport(name, Environment.UserName);

        if (!dialog.FileName.EndsWith(SoundPackService.PackExtension, StringComparison.OrdinalIgnoreCase))
        {
            Report(_sounds.ExportToFile(dialog.FileName, export));
            return;
        }

        var pack = SoundPackService.Create(dialog.FileName, export);

        if (!pack.Success)
        {
            Report(OperationResult.Fail(pack.Message));
            return;
        }

        SetStatus(pack.Message);
        ShowWarnings("Pack created, with some entries left out", pack.Warnings);
    }

    private void ImportScheme_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Import sound scheme or pack", SchemeOpenFilter);
        if (path is null) return;

        SchemeExport? export;
        string? error;
        IReadOnlyList<string> warnings = Array.Empty<string>();

        if (path.EndsWith(SoundPackService.PackExtension, StringComparison.OrdinalIgnoreCase))
        {
            var (scheme, result) = SoundPackService.Install(path);
            export = scheme;
            error = result.Success ? null : result.Message;
            warnings = result.Warnings;

            if (result.Success) SetStatus(result.Message);
        }
        else
        {
            (export, error) = _sounds.ImportFromFile(path);
        }

        if (export is null)
        {
            Report(OperationResult.Fail(error ?? "Could not read that file."));
            return;
        }

        var (backupResult, _) = _backups.CreateSoundBackup($"Before importing: {export.Name}");
        if (!backupResult.Success) SetStatus($"Warning: backup failed ({backupResult.Message}).");

        var before = _sounds.CaptureAssignments();

        MarkSelfWrite();
        var (applyResult, missing) = _sounds.ApplyExport(export);
        Report(applyResult);

        if (applyResult.Success)
            RecordBulk($"Import {export.Name}", before, _sounds.CaptureAssignments());

        ShowWarnings("Some entries were skipped", warnings.Concat(missing).ToList());

        ReloadEvents();
        ReloadSchemes();
        RefreshBackups();
    }

    // ================================================================= cursors ==

    private const string CursorFilter =
        "Cursor files (*.cur;*.ani)|*.cur;*.ani|Static cursor (*.cur)|*.cur|Animated cursor (*.ani)|*.ani";

    // No paths-only variant here, unlike sounds. A cursor scheme is a list of absolute paths
    // into wherever the author downloaded a cursor set, with none of the %SystemRoot%
    // fallbacks sounds have, so a scheme without its files is seventeen broken pointers.
    private const string CursorPackFilter =
        "WinChime cursor pack (*.winchimecursorpack)|*.winchimecursorpack|All files (*.*)|*.*";

    private void ReloadCursors()
    {
        var previous = (CursorList.SelectedItem as CursorEntry)?.RoleKey;

        _cursorEntries.Clear();
        foreach (var entry in _cursors.LoadCursors()) _cursorEntries.Add(entry);

        if (previous is not null)
        {
            CursorList.SelectedItem = _cursorEntries.FirstOrDefault(
                c => c.RoleKey.Equals(previous, StringComparison.OrdinalIgnoreCase));
        }

        CursorUndoButton.IsEnabled = _cursorUndo is not null;
        UpdateCursorDetails();
    }

    private void ReloadCursorSchemes()
    {
        var active = _cursors.GetActiveSchemeName();

        var schemes = _cursors.ListSchemes().ToList();

        // "Windows Default" is a name Windows records as active without storing a scheme
        // string for it, so it matches nothing in the list. Falling back to index 0 would
        // then display a scheme that is not applied, which is worse than showing nothing.
        // Surface the active name instead, even when there is no stored definition behind it.
        if (!schemes.Any(s => s.Name.Equals(active, StringComparison.OrdinalIgnoreCase)))
            schemes.Insert(0, new CursorSchemeItem(active, IsSystemScheme: true));

        CursorSchemeCombo.Items.Clear();
        foreach (var scheme in schemes)
        {
            CursorSchemeCombo.Items.Add(scheme);
            if (scheme.Name.Equals(active, StringComparison.OrdinalIgnoreCase))
                CursorSchemeCombo.SelectedItem = scheme;
        }
    }

    private void CursorList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCursorDetails();

    private void UpdateCursorDetails()
    {
        if (CursorList.SelectedItem is not CursorEntry entry)
        {
            CursorSelectedTitle.Text = "Nothing selected";
            CursorSelectedPath.Text = "";
            CursorInfoText.Text = "Select a cursor to inspect its file.";
            CursorWarnings.Text = "";
            return;
        }

        CursorSelectedTitle.Text = $"{entry.DisplayName}  ({entry.RoleKey})";
        CursorSelectedPath.Text = entry.CurrentPath ?? "(drawn by Windows)";
        CursorWarnings.Text = "";

        if (entry.IsSystemDrawn)
        {
            CursorInfoText.Text = "Windows draws this cursor itself. That is a normal setting, not a missing file.";
            return;
        }

        if (entry.IsBroken)
        {
            CursorInfoText.Text = "The assigned file no longer exists.";
            CursorWarnings.Text = "Windows falls back to the default pointer without reporting anything, so this "
                                  + "looks like nothing happened. Pick a new file or switch it to system.";
            return;
        }

        var info = CursorFile.Inspect(entry.CurrentPath!);
        CursorInfoText.Text = info.Summary;
        CursorWarnings.Text = string.Join(Environment.NewLine + Environment.NewLine, info.Warnings);
    }

    /// <summary>
    /// Cursors get a single-step undo rather than the full history the sounds have. Every
    /// change here rewrites the same seventeen values, so a snapshot before each change is
    /// both simpler and sufficient.
    /// </summary>
    private void RememberCursorsForUndo(string description)
    {
        _cursorUndo = _cursors.CaptureAssignments();
        _cursorUndoDescription = description;
        CursorUndoButton.IsEnabled = true;
        CursorUndoButton.ToolTip = $"Undo: {description}";
    }

    private void UndoCursor_Click(object sender, RoutedEventArgs e)
    {
        if (_cursorUndo is null) return;

        var result = _cursors.RestoreAssignments(_cursorUndo);
        SetStatus(result.Success ? $"Undone: {_cursorUndoDescription}" : result.Message);

        _cursorUndo = null;
        _cursorUndoDescription = null;
        CursorUndoButton.ToolTip = null;

        ReloadCursors();
        ReloadCursorSchemes();
    }

    private void BrowseCursor_Click(object sender, RoutedEventArgs e)
    {
        if (CursorList.SelectedItem is not CursorEntry entry)
        {
            SetStatus("Select a cursor first.");
            return;
        }

        var path = PickFile($"Choose a cursor for {entry.DisplayName}", CursorFilter);
        if (path is null) return;

        var info = CursorFile.Inspect(path);

        // Refused rather than warned-and-allowed: unlike a non-PCM sound, there is nothing
        // to convert here, and assigning it would just silently do nothing.
        if (!info.IsValid)
        {
            MessageBox.Show(info.Error, "Not a usable cursor file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RememberCursorsForUndo($"Set {entry.DisplayName}");
        Report(_cursors.SetCursor(entry.RoleKey, path));

        ReloadCursors();
        ReloadCursorSchemes();
    }

    private void SystemCursor_Click(object sender, RoutedEventArgs e)
    {
        if (CursorList.SelectedItem is not CursorEntry entry) return;

        RememberCursorsForUndo($"System default for {entry.DisplayName}");
        Report(_cursors.SetCursor(entry.RoleKey, null));

        ReloadCursors();
        ReloadCursorSchemes();
    }

    private void ApplyCursorScheme_Click(object sender, RoutedEventArgs e)
    {
        if (CursorSchemeCombo.SelectedItem is not CursorSchemeItem scheme) return;

        RememberCursorsForUndo($"Apply cursor scheme {scheme.Name}");
        Report(_cursors.ApplyScheme(scheme.Name));

        ReloadCursors();
        ReloadCursorSchemes();
    }

    private void SaveCursorScheme_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(this, "Name for this cursor scheme:", "Save cursor scheme", "My cursors");
        if (name is null) return;

        Report(_cursors.SaveCurrentAsScheme(name));
        ReloadCursorSchemes();
    }

    private void DeleteCursorScheme_Click(object sender, RoutedEventArgs e)
    {
        if (CursorSchemeCombo.SelectedItem is not CursorSchemeItem scheme) return;

        if (scheme.IsSystemScheme)
        {
            SetStatus($"{scheme.Name} ships with Windows and cannot be deleted.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete the cursor scheme \"{scheme.Name}\"?\n\nThe cursors currently in use are not changed.",
            "Delete cursor scheme",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        Report(_cursors.DeleteScheme(scheme.Name));
        ReloadCursorSchemes();
    }

    // ---------------------------------------------------------- cursor packs ==

    private void ExportCursorPack_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(this, "Name to record inside the pack:", "Export cursor pack", "My cursors");
        if (name is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export cursor pack",
            Filter = CursorPackFilter,
            FileName = name + CursorPackService.PackExtension,
            DefaultExt = CursorPackService.PackExtension,
        };

        if (dialog.ShowDialog() != true) return;

        var scheme = new CursorSchemeExport { Name = name, Author = Environment.UserName };

        foreach (var pair in _cursors.CaptureAssignments())
            scheme.Assignments[pair.Key] = pair.Value;

        var pack = CursorPackService.Create(dialog.FileName, scheme);

        if (!pack.Success)
        {
            Report(OperationResult.Fail(pack.Message));
            return;
        }

        SetStatus(pack.Message);
        ShowWarnings("Pack created, with some cursors left out", pack.Warnings);
    }

    private void ImportCursorPack_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Import a cursor pack", CursorPackFilter);
        if (path is null) return;

        var (scheme, result) = CursorPackService.Install(path);

        if (scheme is null || !result.Success)
        {
            Report(OperationResult.Fail(result.Message));
            return;
        }

        SetStatus(result.Message);

        // Taken before the first write. Cursors have no backup path at all, unlike sounds,
        // so this snapshot is the entire safety net for what is the largest destructive
        // cursor operation in the app.
        RememberCursorsForUndo($"Install cursor pack {scheme.Name}");

        // Registered as a named scheme and then applied by name, rather than writing the
        // seventeen values directly. That is what sets the active scheme name and Scheme
        // Source the way Windows expects, and it leaves the pack in the scheme list so it can
        // be switched back to later.
        var saved = _cursors.SaveScheme(scheme.Name, CursorPackService.ToSchemeValues(scheme));

        if (!saved.Success)
        {
            Report(saved);
            ShowWarnings("Some cursors were skipped", result.Warnings);
            return;
        }

        Report(_cursors.ApplyScheme(scheme.Name));
        ShowWarnings("Some cursors were skipped", result.Warnings);

        ReloadCursors();
        ReloadCursorSchemes();
    }

    // ================================================================= startup ==

    private void RefreshStartupTab()
    {
        _suppressChimeToggle = true;
        BuiltInChimeCheck.IsChecked = _startupSound.IsBuiltInChimeEnabled();
        _suppressChimeToggle = false;

        BuiltInChimeNote.Text = _startupSound.IsControlledByPolicy()
            ? "A machine policy is also setting this value and may override your choice."
            : $"Source: {StartupSoundService.BuiltInChimeSourceDescription}";

        var config = _logonChime.GetConfig();

        if (config.Installed)
        {
            ChimeStatusText.Text = config.WavPath is null
                ? $"A logon chime task named \"{LogonChimeService.TaskName}\" is installed, but its settings could not be read back."
                : $"Installed: {config.WavPath} ({config.DelaySeconds}s after logon).";

            if (config.WavPath is not null)
            {
                ChimePathBox.Text = config.WavPath;
                ChimeDelaySlider.Value = Math.Clamp(config.DelaySeconds, 0, ChimeDelaySlider.Maximum);
            }
        }
        else
        {
            ChimeStatusText.Text = "No custom logon chime is installed.";
        }

        UpdateChimeDelayText();
    }

    private void UpdateChimeDelayText()
    {
        if (ChimeDelayText is null) return;

        var seconds = (int)ChimeDelaySlider.Value;
        ChimeDelayText.Text = seconds == 0
            ? "immediately (often inaudible)"
            : $"{seconds} second{(seconds == 1 ? "" : "s")} after logon";
    }

    private void BuiltInChime_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressChimeToggle) return;

        var wanted = BuiltInChimeCheck.IsChecked == true;

        var result = ElevationHelper.Execute(new ElevatedRequest
        {
            Op = ElevatedOp.SetStartupSound,
            BoolArg = wanted,
        });

        Report(result);
        RefreshStartupTab();   // re-reads the registry, so a refused UAC prompt reverts the tick
    }

    /// <summary>
    /// Extraction is cached for the session: it maps a 30 MB system DLL and writes a ~600 KB
    /// wav, which is not worth repeating every time the user taps Preview.
    /// </summary>
    private ExtractedChime? GetSystemChime()
    {
        if (_systemChime is not null) return _systemChime;

        var (chimes, error) = SystemChimeResource.Extract();

        if (chimes.Count == 0)
        {
            SystemChimeInfo.Text = error ?? "No embedded startup sound was found.";
            return null;
        }

        _systemChime = chimes[0];

        SystemChimeInfo.Text =
            $"Found resource {_systemChime.ResourceName} in imageres.dll: {_systemChime.Info.Summary}.";

        if (chimes.Count > 1)
            SystemChimeInfo.Text += $" ({chimes.Count} embedded sounds found; previewing the first.)";

        return _systemChime;
    }

    private void PreviewSystemChime_Click(object sender, RoutedEventArgs e)
    {
        var chime = GetSystemChime();
        if (chime is null)
        {
            SetStatus("Could not read the built-in chime.");
            return;
        }

        SetStatus(SoundPreview.Play(chime.FilePath)
            ? $"Playing the built-in Windows chime ({chime.Info.Duration.TotalSeconds:0.0}s)."
            : "Windows could not play the extracted file.");
    }

    private void SaveSystemChime_Click(object sender, RoutedEventArgs e)
    {
        var chime = GetSystemChime();
        if (chime is null)
        {
            SetStatus("Could not read the built-in chime.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the built-in Windows startup sound",
            Filter = WavFilter,
            FileName = "Windows startup sound.wav",
        };

        if (dialog.ShowDialog() != true) return;

        Report(SystemChimeResource.SaveCopy(chime, dialog.FileName));
    }

    private void BrowseChime_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFile("Choose a logon chime", AudioTranscoder.OpenFileFilter);
        if (picked is null) return;

        // Same conversion path as event sounds. LogonChimeService refuses non-PCM outright,
        // so without this an MP3 would be rejected at Install time with nothing offered.
        var path = ResolveAssignableSound(picked);
        if (path is null) return;

        ChimePathBox.Text = path;

        var info = WaveFile.Inspect(path);
        SetStatus(info.IsValid ? info.Summary : info.Error ?? "Unreadable file.");
    }

    private void InstallChime_Click(object sender, RoutedEventArgs e)
    {
        var result = _logonChime.Install(ChimePathBox.Text, (int)ChimeDelaySlider.Value);
        Report(result);

        if (result.Success && _startupSound.IsBuiltInChimeEnabled())
        {
            var turnOff = MessageBox.Show(
                "The built-in Windows startup sound is still enabled, so you will hear both.\n\n" +
                "Turn the built-in one off now?",
                "Two chimes will play",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (turnOff == MessageBoxResult.Yes)
            {
                Report(ElevationHelper.Execute(new ElevatedRequest
                {
                    Op = ElevatedOp.SetStartupSound,
                    BoolArg = false,
                }));
            }
        }

        RefreshStartupTab();
    }

    private void RemoveChime_Click(object sender, RoutedEventArgs e)
    {
        Report(_logonChime.Uninstall());
        RefreshStartupTab();
    }

    private void TestChime_Click(object sender, RoutedEventArgs e)
    {
        // Prefer the real task so the user tests what actually happens at logon,
        // falling back to a direct preview when nothing is installed yet.
        if (_logonChime.GetConfig().Installed)
        {
            Report(_logonChime.TestNow());
            return;
        }

        if (string.IsNullOrWhiteSpace(ChimePathBox.Text))
        {
            SetStatus("Choose a sound file first.");
            return;
        }

        SetStatus(SoundPreview.Play(ChimePathBox.Text)
            ? "Previewing the selected file (the task is not installed yet)."
            : "Windows could not play that file.");
    }

    // ================================================================= desktop ==

    // ================================================================== accent ==

    private void BuildAccentPresets()
    {
        foreach (var preset in AccentColorService.Presets)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),

                // The fill is data, not theme: it is the colour being offered, and a theme
                // change must never repaint it.
                Background = ToBrush(preset),

                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = preset.Hex,
            };

            // The border, by contrast, is chrome. Assigning a Brush here would capture a value
            // and keep the old theme's grey after a live theme flip; SetResourceReference keeps
            // it bound to the key, which is what DynamicResource does in XAML.
            swatch.SetResourceReference(BorderBrushProperty, "Swatch.Border");

            // Selecting a swatch only fills the box. Applying stays an explicit action, so a
            // stray click cannot repaint the desktop.
            swatch.MouseLeftButtonUp += (_, _) => AccentHexBox.Text = preset.Hex;

            AccentPresets.Children.Add(swatch);
        }
    }

    private static System.Windows.Media.Brush ToBrush(AccentRgb colour) =>
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(colour.R, colour.G, colour.B));

    private void AccentHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AccentPreview is null) return;

        if (AccentRgb.TryParse(AccentHexBox.Text, out var colour))
        {
            AccentPreview.Background = ToBrush(colour);
            AccentPreview.SetResourceReference(BorderBrushProperty, "Swatch.Border");
        }
        else
        {
            // Flag it in the preview rather than popping a dialog on every keystroke.
            AccentPreview.SetResourceReference(BorderBrushProperty, "Input.BorderError");
        }
    }

    private void RefreshAccent()
    {
        var state = _accent.GetState();

        AccentCurrentText.Text = state.Accent is { } current
            ? $"currently {current.Hex}"
            : "Windows has not recorded an accent colour";

        if (state.Accent is { } accent) AccentHexBox.Text = accent.Hex;

        AccentPrevalenceCheck.IsChecked = state.ColorPrevalence;
        AccentUndoButton.IsEnabled = _accentUndo is not null;
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e)
    {
        if (!AccentRgb.TryParse(AccentHexBox.Text, out var colour))
        {
            MessageBox.Show(
                "Enter a colour as #RRGGBB, for example #0078D7, or click one of the swatches.",
                "That is not a colour",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        _accentUndo = _accent.CaptureAssignments();

        Report(_accent.Apply(colour, AccentPrevalenceCheck.IsChecked == true));
        RefreshAccent();
    }

    private void UndoAccent_Click(object sender, RoutedEventArgs e)
    {
        if (_accentUndo is null) return;

        var result = _accent.RestoreAssignments(_accentUndo);
        SetStatus(result.Success ? "Accent colour restored." : result.Message);

        _accentUndo = null;
        RefreshAccent();
    }

    private void RefreshDesktopTab()
    {
        WallpaperPathBox.Text = _wallpaper.GetCurrent() ?? "";

        var lockScreen = _lockScreen.GetCurrent();
        LockScreenStatusText.Text = lockScreen is null
            ? "No lock screen override is applied. Windows is using your normal Settings choice."
            : $"Override active, using: {lockScreen}";
    }

    private void BrowseWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Choose a wallpaper", ImageFilter);
        if (path is not null) WallpaperPathBox.Text = path;
    }

    private void ApplyWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var style = Enum.TryParse<WallpaperStyle>(WallpaperStyleCombo.SelectedItem as string, out var parsed)
            ? parsed
            : WallpaperStyle.Fill;

        Report(_wallpaper.Set(WallpaperPathBox.Text, style));
        RefreshDesktopTab();
    }

    private void BrowseLockScreen_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Choose a lock screen image", ImageFilter);
        if (path is not null) LockScreenPathBox.Text = path;
    }

    private void ApplyLockScreen_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "While this override is applied, the lock screen section of Settings will be greyed out.\n\n" +
            "Clear override removes it completely and restores normal behaviour.\n\nContinue?",
            "Lock screen override",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (confirm != MessageBoxResult.OK) return;

        Report(_lockScreen.Apply(LockScreenPathBox.Text));
        RefreshDesktopTab();
    }

    private void ClearLockScreen_Click(object sender, RoutedEventArgs e)
    {
        Report(_lockScreen.Clear());
        RefreshDesktopTab();
    }

    // ========================================================= system & safety ==

    private void RefreshSystemTab()
    {
        _systemInfo = SystemProbe.Capture();

        SystemInfoText.Text = string.Join(Environment.NewLine, new[]
        {
            $"Windows          {_systemInfo.FullVersionString}",
            $"Architecture     {(_systemInfo.Is64BitOs ? "64-bit" : "32-bit")}",
            $"Firmware         {_systemInfo.FirmwareType}",
            $"Secure Boot      {_systemInfo.SecureBootEnabled}",
            $"System Restore   {_systemInfo.SystemRestoreEnabled}",
            $"Running as admin {(_systemInfo.IsElevated ? "yes" : "no")}",
        });

        ElevationText.Text = _systemInfo.IsElevated ? "Running elevated" : "Running as standard user";
    }

    private void RefreshBackups()
    {
        _backupItems.Clear();
        foreach (var manifest in _backups.List()) _backupItems.Add(manifest);
    }

    private void RefreshSystem_Click(object sender, RoutedEventArgs e)
    {
        RefreshSystemTab();
        SetStatus("System information refreshed.");
    }

    private void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        var (result, _) = _backups.CreateSoundBackup("Manual backup");
        Report(result);
        RefreshBackups();
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupManifest manifest)
        {
            SetStatus("Select a backup first.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Restore {manifest.SoundAssignments.Count} sound assignment(s) from {manifest.CreatedLocalText}?\n\n" +
            "Your current assignments will be overwritten.",
            "Restore backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var before = _sounds.CaptureAssignments();

        MarkSelfWrite();
        var result = _backups.RestoreSounds(manifest);
        Report(result);

        // Restoring a backup is itself undoable, so a mis-clicked restore is recoverable
        // without hunting for the backup taken just before it.
        if (result.Success)
            RecordBulk($"Restore backup from {manifest.CreatedLocalText}", before, _sounds.CaptureAssignments());

        ReloadEvents();
        ReloadSchemes();
    }

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupManifest manifest)
        {
            SetStatus("Select a backup first.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Permanently delete the backup from {manifest.CreatedLocalText}?",
            "Delete backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        Report(_backups.Delete(manifest));
        RefreshBackups();
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BackupService.BackupRoot);
            Process.Start(new ProcessStartInfo(BackupService.BackupRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Report(OperationResult.Fail($"Could not open the backup folder: {ex.Message}"));
        }
    }

    private void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Asking Windows for a restore point; this can take a minute…");

        var result = ElevationHelper.Execute(new ElevatedRequest
        {
            Op = ElevatedOp.CreateRestorePoint,
            StringArg = "WinChime checkpoint",
        });

        Report(result);
        RefreshSystemTab();
    }
}
