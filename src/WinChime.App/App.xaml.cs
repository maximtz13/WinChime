using System.Windows;
using WinChime.Core.Cli;
using WinChime.Core.Elevation;
using WinChime.Core.Sounds;

namespace WinChime.App;

public partial class App : Application
{
    public const string PlayChimeSwitch = "--play-chime";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless mode 1: invoked by the logon scheduled task. Play synchronously,
        // because PlaySound is fire-and-forget and the sound would be cut off the moment
        // the process exits. No window is ever created.
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], PlayChimeSwitch, StringComparison.OrdinalIgnoreCase))
        {
            SoundPreview.Play(e.Args[1], synchronous: true);
            Shutdown(0);
            return;
        }

        // Headless mode 2: the elevated child spawned by ElevationHelper.
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], ElevationHelper.ElevatedOpSwitch, StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(ElevationHelper.RunElevatedChild(e.Args[1]));
            return;
        }

        // Headless mode 3: command line. Borrows the parent terminal, because a WinExe has
        // no console of its own and output would otherwise vanish silently.
        if (CliRunner.IsCliInvocation(e.Args))
        {
            using var console = new ConsoleSession();

            var exitCode = new CliRunner(Console.Out).Run(e.Args);

            if (console.OwnsWindow)
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }

            Shutdown(exitCode);
            return;
        }

        // A personalisation tool is not worth a crash dialog; surface and carry on.
        //
        // The one place still using a Win32 message box, and deliberately. Every other dialog
        // in the app is now MessageDialog, but this handler is the last resort and each thing
        // that makes MessageBox unthemeable is also what makes it safe here:
        //
        // - It needs no owner. This is registered before the main window is created, so an
        //   exception from ThemeManager.Initialise or from the MainWindow constructor arrives
        //   with no window to own a dialog.
        // - It needs no resources. If the fault were a missing theme key, a WPF dialog would
        //   throw while parsing the very error it was opened to report.
        // - It is not a Window, so it cannot become the app's last window and shut the process
        //   down when dismissed, and it never joins Application.Windows.
        // - It pumps no dispatcher loop of its own, so a visual tree that throws on every
        //   render cannot re-enter this handler through the error dialog.
        //
        // The cost is that a crash message appears in the system theme rather than the app's.
        // That is the correct trade for the one dialog that has to work when nothing else does.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.Message,
                "WinChime hit an unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
        };

        // Only on the path that actually shows a window. The headless branches above return
        // before this, so none of them pays to load theme dictionaries it cannot use.
        ThemeManager.Initialise();

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Shutdown();
        base.OnExit(e);
    }
}
