using System.Media;
using System.Windows;
using System.Windows.Input;

namespace WinChime.App;

/// <summary>Which of the four standard shapes a message takes.</summary>
public enum DialogIcon
{
    None,
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>
/// The in-app replacement for <see cref="MessageBox"/>.
///
/// A Win32 message box cannot be themed. It is drawn by the shell, it is a classic dialog
/// rather than a modern one, and it stays light however the app and Windows are set — so in
/// the dark theme every error and every confirmation arrived as a white rectangle.
///
/// Replacing it means taking on the things it did for free, and most of them are invisible
/// until they are missing:
///
/// - Escape dismisses. Message boxes let Escape close anything that has a cancel-equivalent
///   answer, which for an OK-only box is OK itself.
/// - Enter activates the default button, and that button holds focus when the dialog opens.
///   Without the focus call there is nothing for Enter or Space to act on.
/// - The system sound tied to the icon plays. Dropping it would be a strange regression in an
///   app whose subject is system sounds.
/// - Ctrl+C copies the dialog's text. It is how people capture an error to report it, and it
///   matters most for the messages here that carry a real Windows failure string.
///
/// One difference is deliberate. A Yes/No message box refuses to close on Escape, on the
/// grounds that a two-way question has no third answer. Here Escape declines, which is safe
/// because every caller treats anything other than the affirmative as a decline, and it avoids
/// a dialog that appears stuck. Any future caller that needs to tell "No" apart from "closed"
/// must not use this.
/// </summary>
public partial class MessageDialog : Window
{
    private bool _affirmed;

    private MessageDialog(string? message, string title, DialogIcon icon)
    {
        InitializeComponent();

        // Dialogs get their own DWM title bar, so each one has to be asked separately.
        ThemeManager.Track(this);

        Title = title;

        // Null is reachable: CursorInfo.Error is nullable and one call site passes it straight
        // through. A Win32 message box tolerates a null string; a TextBlock shows nothing.
        MessageText.Text = string.IsNullOrWhiteSpace(message) ? "No further detail is available." : message;

        ApplyIcon(icon);

        // Focus() before the window is shown does nothing, so it waits for Loaded. Without
        // this nothing has focus and neither Enter nor Space does anything.
        Loaded += (_, _) => DefaultButton().Focus();
    }

    // ------------------------------------------------------------------ entry points --

    /// <summary>A notice with a single button. The caller has nothing to decide.</summary>
    public static void Show(Window owner, string? message, string title, DialogIcon icon)
    {
        var dialog = new MessageDialog(message, title, icon) { Owner = owner };

        dialog.AffirmativeButton.Content = "OK";
        dialog.NegativeButton.Visibility = Visibility.Collapsed;

        dialog.ShowDialog();
    }

    /// <summary>
    /// A question with two answers. True only for the affirmative: Escape, Alt+F4 and the close
    /// box all decline.
    /// </summary>
    /// <param name="defaultIsNegative">
    /// Puts focus on the declining button, so Enter does not confirm. For anything
    /// irreversible that is the right default, and it is a deliberate departure from a message
    /// box, which always focuses the affirmative.
    /// </param>
    public static bool Confirm(
        Window owner,
        string message,
        string title,
        DialogIcon icon,
        string affirmative = "Yes",
        string negative = "No",
        bool defaultIsNegative = false)
    {
        var dialog = new MessageDialog(message, title, icon) { Owner = owner };

        dialog.AffirmativeButton.Content = affirmative;
        dialog.NegativeButton.Content = negative;
        dialog.NegativeButton.Visibility = Visibility.Visible;
        dialog._defaultIsNegative = defaultIsNegative;

        // The filled button and the focused button have to be the same one, or the dialog
        // says two different things about what Enter will do. When the safe answer is the
        // default, it takes the accent and the destructive one drops to a plain button.
        if (defaultIsNegative)
        {
            // The implicit style has to be named, not cleared. Style = null in WPF means "no
            // style", not "fall back to the one keyed by type", so clearing it drops the
            // control back to raw WPF chrome: a grey 1990s button in the middle of a themed
            // dialog, at the wrong size and overlapping its neighbour.
            dialog.AffirmativeButton.Style = (Style)dialog.FindResource(typeof(System.Windows.Controls.Button));
            dialog.NegativeButton.Style = (Style)dialog.FindResource("PrimaryButton");
        }

        dialog.ShowDialog();

        return dialog._affirmed;
    }

    private bool _defaultIsNegative;

    private System.Windows.Controls.Button DefaultButton() =>
        _defaultIsNegative && NegativeButton.Visibility == Visibility.Visible
            ? NegativeButton
            : AffirmativeButton;

    // ----------------------------------------------------------------------- chrome --

    private void ApplyIcon(DialogIcon icon)
    {
        if (icon == DialogIcon.None)
        {
            IconArea.Visibility = Visibility.Collapsed;
            return;
        }

        // The triangle is the one shape that is not a disc, so the two swap rather than being
        // redrawn.
        var triangle = icon == DialogIcon.Warning;

        IconDisc.Visibility = triangle ? Visibility.Collapsed : Visibility.Visible;
        IconTriangle.Visibility = triangle ? Visibility.Visible : Visibility.Collapsed;

        IconGlyph.Text = icon switch
        {
            DialogIcon.Warning => "!",
            DialogIcon.Error => "×",     // multiplication sign: a rounder cross than "x"
            DialogIcon.Question => "?",
            _ => "i",
        };

        // The warning glyph sits on the triangle's fill, so it needs the page colour behind it
        // rather than the accent foreground.
        IconGlyph.Foreground = triangle
            ? (System.Windows.Media.Brush)FindResource("Surface.Card")
            : (System.Windows.Media.Brush)FindResource("Accent.Text");

        if (!triangle && icon == DialogIcon.Error)
            IconDisc.Fill = (System.Windows.Media.Brush)FindResource("Status.Danger");

        // The warning glyph is optically low inside a triangle; nudge it down off centre.
        IconGlyph.Margin = triangle ? new Thickness(0, 6, 0, 0) : default;

        PlaySoundFor(icon);
    }

    /// <summary>
    /// The sound a message box would have played. Worth keeping in an app about system sounds,
    /// where a silent error dialog would look like the app had broken them.
    /// </summary>
    private static void PlaySoundFor(DialogIcon icon)
    {
        switch (icon)
        {
            case DialogIcon.Warning: SystemSounds.Exclamation.Play(); break;
            case DialogIcon.Error: SystemSounds.Hand.Play(); break;
            case DialogIcon.Question: SystemSounds.Question.Play(); break;
            case DialogIcon.Information: SystemSounds.Asterisk.Play(); break;
        }
    }

    // -------------------------------------------------------------------- behaviour --

    private void Affirmative_Click(object sender, RoutedEventArgs e)
    {
        _affirmed = true;
        Close();
    }

    private void Negative_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape declines, and for a one-button notice that is simply dismissal. Handled here
        // rather than with IsCancel, which would also force a DialogResult this dialog does
        // not use.
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Enter on a focused button is already handled by the button itself; this only covers
        // focus being somewhere else, which is what IsDefault would normally do.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is not System.Windows.Controls.Button)
        {
            DefaultButton().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            e.Handled = true;
            return;
        }

        // How a user captures an error message to report it. A message box does this and
        // nothing about a custom window does it for free.
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TryCopyToClipboard();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void TryCopyToClipboard()
    {
        try
        {
            Clipboard.SetText($"{Title}{Environment.NewLine}{Environment.NewLine}{MessageText.Text}");
        }
        catch
        {
            // The clipboard is a shared resource and another process can be holding it. Failing
            // to copy an error message is not worth raising a second error dialog over.
        }
    }
}
