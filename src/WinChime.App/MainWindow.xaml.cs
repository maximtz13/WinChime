using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;   // not implicit in WPF projects: System.IO.Path would clash with Shapes.Path
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WinChime.Core.Elevation;
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

    private ICollectionView? _eventView;
    private SystemInfo _systemInfo = new();

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

        foreach (var style in Enum.GetValues<WallpaperStyle>())
            WallpaperStyleCombo.Items.Add(style.ToString());
        WallpaperStyleCombo.SelectedIndex = 0;

        ChimeDelaySlider.ValueChanged += (_, _) => UpdateChimeDelayText();
        UpdateChimeDelayText();

        Loaded += (_, _) => InitialLoad();
    }

    private void InitialLoad()
    {
        ReloadSchemes();
        ReloadEvents();
        RefreshStartupTab();
        RefreshDesktopTab();
        RefreshSystemTab();
        RefreshBackups();

        SetStatus($"Loaded {_events.Count} sound events.");
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
    private const string SchemeFilter = "WinChime scheme (*.winchime.json)|*.winchime.json|JSON (*.json)|*.json";

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

        var convert = MessageBox.Show(
            $"{Path.GetFileName(path)} is {described}. Windows only plays uncompressed PCM for event " +
            "sounds, so assigning it directly would leave the event silent with no error.\n\n" +
            "Convert it to PCM WAV and use the converted copy?\n\n" +
            $"The copy is saved in:\n{AudioTranscoder.ConvertedFolder}",
            "Conversion needed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (convert != MessageBoxResult.Yes) return null;

        var result = AudioTranscoder.ConvertIntoLibrary(path);
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

        Report(_sounds.SetSound(soundEvent.AppKey, soundEvent.EventKey, path));
        ReloadEvents();
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

        Report(_sounds.SetSound(soundEvent.AppKey, soundEvent.EventKey, null));
        ReloadEvents();
    }

    private void RestoreSound_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not SoundEvent soundEvent) return;

        Report(_sounds.RestoreDefault(soundEvent.AppKey, soundEvent.EventKey));
        ReloadEvents();
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

        Report(_sounds.ApplyScheme(scheme.Key));
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
            Filter = SchemeFilter,
            FileName = $"{name}.winchime.json",
        };

        if (dialog.ShowDialog() != true) return;

        Report(_sounds.ExportToFile(dialog.FileName, _sounds.BuildExport(name, Environment.UserName)));
    }

    private void ImportScheme_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Import sound scheme", SchemeFilter);
        if (path is null) return;

        var (export, error) = _sounds.ImportFromFile(path);
        if (export is null)
        {
            Report(OperationResult.Fail(error ?? "Could not read that file."));
            return;
        }

        var (backupResult, _) = _backups.CreateSoundBackup($"Before importing: {export.Name}");
        if (!backupResult.Success) SetStatus($"Warning: backup failed ({backupResult.Message}).");

        var (result, missing) = _sounds.ApplyExport(export);
        Report(result);

        if (missing.Count > 0)
        {
            MessageBox.Show(
                "These entries were skipped because the audio file does not exist on this PC:\n\n"
                + string.Join(Environment.NewLine, missing.Take(20))
                + (missing.Count > 20 ? $"{Environment.NewLine}… and {missing.Count - 20} more." : ""),
                "Some sounds were skipped",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        ReloadEvents();
        ReloadSchemes();
        RefreshBackups();
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

        Report(_backups.RestoreSounds(manifest));
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
