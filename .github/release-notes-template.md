## WinChime {{VERSION}}

Two downloads, pick one:

| File | Size | Requires |
|------|------|----------|
| `WinChime-{{VERSION}}-win-x64.zip` | ~0.3 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `WinChime-{{VERSION}}-win-x64-self-contained.zip` | ~155 MB | nothing, fully standalone |

The binaries are unsigned, so Windows SmartScreen will show "Windows protected your PC" on
first run. Choose *More info* then *Run anyway*, or build from source to avoid it entirely.

### What it does

System event sounds and sound schemes, the Windows logon chime (including previewing the
built-in one without enabling it), custom logon chimes, wallpaper and the lock screen.

### What it deliberately does not do

WinChime never modifies a system file, a boot file, or a firmware setting. Every change is
either a per-user setting or a single documented registry value, and every change is
reversible from inside the app. Replacing the UEFI boot logo and patching `imageres.dll`
were both considered and rejected — the reasoning is in the README.
