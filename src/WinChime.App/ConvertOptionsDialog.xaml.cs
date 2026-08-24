using System.Globalization;
using System.IO;
using System.Windows;
using WinChime.Core.Model;
using WinChime.Core.Sounds;

namespace WinChime.App;

/// <summary>
/// Asks how to convert a file, rather than deciding for the user.
///
/// Trimming and normalising both alter someone's audio, so neither is applied silently.
/// Trim is pre-ticked only when the source is actually too long to work well as an event
/// sound, which is the case where the suggestion is worth making.
/// </summary>
public partial class ConvertOptionsDialog : Window
{
    private ConvertOptionsDialog(string sourcePath, WaveInfo info, bool conversionRequired)
    {
        InitializeComponent();

        // Dialogs get their own DWM title bar, so each one has to be asked separately.
        ThemeManager.Track(this);

        var name = Path.GetFileName(sourcePath);

        SourceText.Text = name;

        var described = info.IsValid
            ? info.Summary
            : $"{Path.GetExtension(sourcePath).TrimStart('.').ToUpperInvariant()} audio";

        ExplanationText.Text = conversionRequired
            ? $"{described}.\n\nWindows only plays uncompressed PCM for event sounds, so this file " +
              "has to be converted or the event would stay silent with no error."
            : $"{described}.\n\nThis file already works as an event sound. Converting is optional.";

        var tooLong = info.IsValid && info.Duration > TranscodeOptions.SuggestedMaxEventDuration;
        TrimCheck.IsChecked = tooLong;

        if (tooLong)
        {
            TrimCheck.Content = $"Trim to (it is currently {info.Duration.TotalSeconds:0.#}s)";
            TrimSecondsBox.Text = TranscodeOptions.SuggestedMaxEventDuration.TotalSeconds.ToString("0", CultureInfo.CurrentCulture);
        }

        OutputText.Text = $"Output: uncompressed PCM WAV, saved in {AudioTranscoder.ConvertedFolder}";

        Loaded += (_, _) => TrimSecondsBox.Focus();
    }

    /// <summary>The chosen options, or null when the user cancels.</summary>
    public TranscodeOptions? Options { get; private set; }

    public static TranscodeOptions? Ask(Window owner, string sourcePath, WaveInfo info, bool conversionRequired)
    {
        var dialog = new ConvertOptionsDialog(sourcePath, info, conversionRequired) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Options : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        TimeSpan? maxDuration = null;

        if (TrimCheck.IsChecked == true)
        {
            if (!double.TryParse(TrimSecondsBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds)
                || seconds <= 0)
            {
                MessageBox.Show(
                    "Enter a trim length greater than zero, in seconds.",
                    "Trim length",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                TrimSecondsBox.Focus();
                TrimSecondsBox.SelectAll();
                return;
            }

            maxDuration = TimeSpan.FromSeconds(seconds);
        }

        Options = new TranscodeOptions
        {
            MaxDuration = maxDuration,
            Normalise = NormaliseCheck.IsChecked == true,
        };

        DialogResult = true;
    }
}
