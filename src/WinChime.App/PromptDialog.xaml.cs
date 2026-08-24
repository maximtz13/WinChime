using System.Windows;
using System.Windows.Input;

namespace WinChime.App;

/// <summary>
/// Minimal single-line text prompt. WPF ships no input box, and pulling in
/// Microsoft.VisualBasic.Interaction just for one dialog is not worth the reference.
/// </summary>
public partial class PromptDialog : Window
{
    public string Value => InputBox.Text.Trim();

    public PromptDialog(string prompt, string title, string initialValue = "")
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;

        // Dialogs get their own DWM title bar, so each one has to be asked separately.
        ThemeManager.Track(this);

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Shows the dialog and returns the trimmed text, or null when cancelled or empty.</summary>
    public static string? Ask(Window owner, string prompt, string title, string initialValue = "")
    {
        var dialog = new PromptDialog(prompt, title, initialValue) { Owner = owner };

        return dialog.ShowDialog() == true && dialog.Value.Length > 0
            ? dialog.Value
            : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
