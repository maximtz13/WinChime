## WinChime {{VERSION}}

Two downloads, pick one:

| File | Size | Requires |
|------|------|----------|
| `WinChime-{{VERSION}}-win-x64.zip` | ~0.3 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `WinChime-{{VERSION}}-win-x64-self-contained.zip` | ~155 MB | nothing, fully standalone |

### If Windows blocks it

The binaries are unsigned, so you may hit one of two things:

**SmartScreen** shows "Windows protected your PC" — dismissable via *More info* → *Run
anyway*.

**Smart App Control** may block it outright rather than warn. If so, **wait a little and try
again**: SAC asks a cloud reputation service about binaries it has not seen before, and the
block clears once that check completes.

**Do not disable Smart App Control to run this.** Turning it off is irreversible — switching
it back on requires resetting Windows. Permanently weakening your machine's security to run a
sound-settings utility is not a good trade, and it is not one this project asks you to make.

Signing would not reliably fix this either: SAC weighs reputation ahead of signature and
blocks unknown binaries even when correctly signed. The README explains the reasoning in
full.

### What it does

System event sounds and sound schemes, the Windows logon chime (including previewing the
built-in one without enabling it), custom logon chimes, wallpaper and the lock screen.

### What it deliberately does not do

WinChime never modifies a system file, a boot file, or a firmware setting. Every change is
either a per-user setting or a single documented registry value, and every change is
reversible from inside the app. Replacing the UEFI boot logo and patching `imageres.dll`
were both considered and rejected — the reasoning is in the README.
