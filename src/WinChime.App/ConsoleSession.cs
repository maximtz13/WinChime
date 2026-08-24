using System.IO;
using System.Runtime.InteropServices;

namespace WinChime.App;

/// <summary>
/// Borrows the parent terminal so a GUI-subsystem executable can behave like a CLI.
///
/// WinChime is built as WinExe, which is necessary: the logon task must not flash a console
/// window at sign-in. The cost is that a WinExe has no console at all, so Console.WriteLine
/// goes nowhere when someone runs it from a terminal. AttachConsole borrows the parent's.
///
/// The reattach dance matters. By the time this runs, the CLR has already cached a null
/// stdout handle, so the streams have to be rebuilt after attaching or output still
/// disappears — silently, which is the worst way for it to fail.
///
/// One unavoidable cosmetic quirk: the shell prints its next prompt as soon as it launches a
/// GUI process, so CLI output arrives after that prompt. Every GUI-with-CLI app on Windows
/// has this; there is no fix short of shipping a second console executable.
/// </summary>
internal sealed class ConsoleSession : IDisposable
{
    private const int AttachParentProcess = -1;

    private readonly bool _attached;
    private readonly bool _allocated;

    public ConsoleSession()
    {
        _attached = AttachConsole(AttachParentProcess);

        // No parent console: launched from Explorer or a scheduler. Make one, otherwise the
        // user gets no feedback whatsoever from a command that clearly expected to print.
        if (!_attached)
        {
            _allocated = AllocConsole();
        }

        if (!_attached && !_allocated) return;

        // Without this the console stays on its legacy code page while the writers below
        // emit UTF-8, so any non-ASCII character arrives as mojibake. CLI output is kept
        // ASCII anyway, but a path or a scheme name supplied by the user can be anything.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // Fails when stdout is redirected to something that has no code page. Harmless.
        }

        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(output);

        var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(error);
    }

    /// <summary>
    /// True when a console window had to be created rather than borrowed, meaning it will
    /// vanish the moment this process exits and the caller may want to keep it open.
    /// </summary>
    public bool OwnsWindow => _allocated;

    public void Dispose()
    {
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch (IOException)
        {
            // The parent terminal can go away underneath us; nothing useful to do.
        }

        if (_attached || _allocated) FreeConsole();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
}
