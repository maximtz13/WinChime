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
