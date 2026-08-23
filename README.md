# WinChime

[![CI](https://github.com/maximtz13/WinChime/actions/workflows/ci.yml/badge.svg)](https://github.com/maximtz13/WinChime/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

One application for Windows sound and startup personalisation: system event sounds, sound
schemes, the logon chime, wallpaper and the lock screen.

**Nothing here modifies a system file, a boot file, or the firmware trust chain.** Every
change is either per-user or a single documented registry value, and every change is
reversible from inside the app. See [Scope](#scope) for what was deliberately left out and
why.

---

## Risk tiers

| Tier | What | Admin | Risk | Status |
|------|------|-------|------|--------|
| 1 | Event sounds, sound schemes, wallpaper | No | None. Per-user registry, instantly reversible. | **Implemented** |
| 2a | Windows logon chime on/off | Yes | Minimal. One HKLM DWORD. | **Implemented** |
| 2b | Custom logon chime via scheduled task | No | Minimal. Nothing in Windows is modified. | **Implemented** |
| 2c | Lock screen image | Yes | Low, but *greys out* the Settings page while applied. | **Implemented** |
| — | Preview/extract the built-in chime | No | None. Read-only resource read. | **Implemented** |
| — | Custom chime by patching `imageres.dll` | Yes | Reverted by `sfc` and by every cumulative update. | **Rejected by design** |
| — | UEFI boot logo (BGRT) | Yes | Requires disabling Secure Boot. | **Rejected by design** |

## Scope

Two things this app could technically do and deliberately does not:

**Patching `imageres.dll` to replace the startup chime.** It means taking ownership from
TrustedInstaller and rewriting a signed system binary, which `sfc /scannow` reverts and
every cumulative update overwrites. The scheduled-task approach below achieves the same
user-visible result with none of that.

**Replacing the UEFI boot logo.** It requires the user to disable Secure Boot (or self-sign
an EFI loader and enrol the key), and to suspend BitLocker first or land at a recovery-key
prompt. Talking people into weakening their machine's firmware trust chain for a cosmetic
change is not a trade worth offering, so the feature — and all the gating code that existed
to support it — was removed rather than shipped behind a warning.

---

## What each feature actually does

### Event sounds and schemes — `SoundSchemeService`

The whole Windows event-sound system is `HKCU\AppEvents`:

```
AppEvents\Schemes                              (Default) = active scheme key
AppEvents\Schemes\Names\{scheme}               (Default) = friendly scheme name
AppEvents\Schemes\Apps\{app}\{event}\.Current  (Default) = active wav path
AppEvents\Schemes\Apps\{app}\{event}\.Default  (Default) = the original Windows wav
AppEvents\Schemes\Apps\{app}\{event}\{scheme}  (Default) = that scheme's wav
AppEvents\EventLabels\{event}                  (Default) = friendly event name
```

Per-user, no elevation, and Windows re-reads the value each time an event fires — so
changes apply immediately with no reboot and no `WM_SETTINGCHANGE` broadcast.

Because `.Default` is stored per event, **restore-to-Windows-default is free** and does not
depend on our own backups.

Three things this does that the Sound control panel does not:

- **Validates the WAV.** Windows accepts any file and then plays *nothing* if it is not
  uncompressed PCM — no error, no log entry. `WaveFile` parses the RIFF header and says so.
- **Converts what will not work.** Detecting the problem is only half an answer. Pick an
  MP3, M4A, WMA or FLAC and `AudioTranscoder` offers to convert it to PCM WAV, writing the
  copy to `%LOCALAPPDATA%\WinChime\converted` — a persistent location, because the registry
  points at it and a temp folder would leave every converted sound broken after a reboot.
  Decoding goes through Media Foundation, so the readable formats are whatever the host
  Windows supports.
- **Flags broken assignments.** A sound pointing at a deleted file also fails silently.
  The Status column shows `Missing`.

### Sound packs — `SoundPackService`

Two export formats, because they solve different problems:

| Format | Contents | Use when |
|---|---|---|
| `.winchime.json` | Registry paths only | Backing up or moving between your own machines |
| `.winchimepack` | Scheme **plus the audio**, one zip | Sending a scheme to someone else |

A bare `.json` scheme stores *unexpanded* registry values so `%SystemRoot%` resolves
correctly anywhere — but it only works on a machine that already has identical files at
identical paths, which in practice means it does not travel. A pack is one file you can
hand to someone.

Two things a pack deliberately does **not** contain:

- **Windows' own sounds.** An assignment pointing at `%SystemRoot%\media\...` is kept as
  that literal string. Those files exist on every Windows install, so bundling them would
  bloat the pack and redistribute Microsoft's audio for no benefit.
- **Duplicates.** One file referenced by twelve events is stored once and referenced twelve
  times.

Installing validates before extracting. Entry paths are checked so a hostile entry named
`../../evil.exe` cannot write outside the pack folder — packs are files people receive from
other people, so that is a real attack surface rather than a theoretical one — and entry
count and total uncompressed size are bounded against zip bombs. Missing or dangling
entries are reported rather than silently assigned.

### The logon chime — `StartupSoundService` / `LogonChimeService`

The on/off switch is one HKLM DWORD:

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation
    DisableStartupSound  (REG_DWORD)  0 = play, 1 = silent
```

A machine policy at `...\Policies\System\DisableStartupSound` overrides it, and the UI says
so when one is present.

**The chime audio is not a file.** Since Windows 8 it is a WAVE resource embedded in
`%SystemRoot%\System32\imageres.dll` — resource `#5080` on current builds.

**You can still hear it.** `SystemChimeResource` maps the module with
`LOAD_LIBRARY_AS_DATAFILE` and copies the audio out, so *Preview it* / *Save a copy* work
without elevation, without executing anything from the DLL, and without writing to it.
Rather than hard-coding `5080`, the WAVE resources are enumerated and whatever is actually
there is used, which survives Microsoft renumbering it.

To *replace* it, turn the built-in chime off and register a per-user logon scheduled task
that runs `WinChime.exe --play-chime "<path>"`. Nothing in the Windows install is modified,
so no update can undo it and SFC stays clean. The trade-off is a second or two of latency.

A delay slider matters more than it looks: at logon the shell is still starting and audio
endpoints may not be ready, so a zero-delay sound is frequently inaudible. Default is 4s.

Registered through `schtasks.exe` with an XML definition rather than the Task Scheduler COM
API, to keep the Core assembly dependency-free. Note `schtasks` requires the XML file to be
**UTF-16**; UTF-8 is rejected with an unhelpful parse error.

### Lock screen — `LockScreenService`

There is no clean option here for an unpackaged desktop app:

- `Windows.System.UserProfile.LockScreen.SetImageFileAsync` requires package identity. An
  unpackaged `.exe` cannot call it, and MSIX-packaging the app for that one call is not
  worth it.
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization\LockScreenImage` is ignored on
  Windows Home.

So it uses **PersonalizationCSP**, the MDM route, which works on all editions:

```
HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Personalization
    LockScreenImagePath / LockScreenImageUrl / LockScreenImageStatus
```

The cost is real and the UI states it up front: **while applied, the lock screen section of
Settings is greyed out.** *Clear override* fully removes it.

The chosen image is copied into `%ProgramData%\WinChime\LockScreen` rather than referenced
in place, because a lock screen pointing at a deleted file in someone's Downloads folder
degrades badly.

### Wallpaper — `WallpaperService`

The easy one. `SystemParametersInfo(SPI_SETDESKWALLPAPER)` plus the
`WallpaperStyle`/`TileWallpaper` pair in `HKCU\Control Panel\Desktop`. Per-user, no
elevation, no policy, fully reversible from Settings.

---

## Safety design

**Automatic backups before every bulk change.** `BackupService` snapshots all sound
assignments to `%LOCALAPPDATA%\WinChime\backups\{id}\manifest.json` before applying or
importing a scheme. Not conditional on a checkbox. This is the *primary* undo path and
works on machines where System Restore is disabled — which is most consumer Windows 11
installs.

Backups are registry-only by design. Since no system file is ever modified, a few KB of
JSON is a complete record of everything the app changed; there is no file-copy or hashing
path because there is nothing to copy.

**System Restore as a backstop, not the plan.** `RestorePointService` reports honestly
that it needs elevation, needs System Restore enabled for the system drive, and that
Windows only creates one point per 24 hours unless policy says otherwise — so a second call
the same day silently does nothing.

**Least-privilege elevation.** The UI runs `asInvoker` for the whole session. The handful
of privileged operations run in a short-lived elevated copy of the same exe
(`--elevated-op`), which does exactly one operation from a closed enum and exits. Request
and response travel through temp files, not the command line, so a path containing quotes
cannot be mis-parsed into a different operation. The alternatives were worse: a
`requireAdministrator` manifest runs the entire UI elevated just to flip one DWORD, and a
persistent elevated helper service is far more attack surface than a personalisation tool
deserves.

---

## Install

Grab a build from [Releases](https://github.com/maximtz13/WinChime/releases):

| File | Size | Requires |
|------|------|----------|
| `WinChime-<version>-win-x64.zip` | ~0.3 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `WinChime-<version>-win-x64-self-contained.zip` | ~155 MB | nothing, fully standalone |

The binaries are unsigned, so SmartScreen will show "Windows protected your PC" on first
run — *More info* → *Run anyway*. Building from source (below) avoids that entirely.

## Build and run

Requires the **.NET 8 SDK**:

```bash
winget install Microsoft.DotNet.SDK.8
```

Then, from the repo root:

```bash
dotnet build WinChime.sln -c Release
```

```bash
dotnet run --project src/WinChime.App
```

```bash
dotnet test WinChime.sln -c Release
```

### Tests

`tests/WinChime.Core.Tests` covers the registry layer, WAV validation, and scheme
import/export. Two decisions worth knowing:

**No registry mocking.** `SoundSchemeService` takes an HKCU root, and tests point it at a
throwaway `Software\WinChime.Tests\{guid}` subtree. Registry semantics are exactly where
the bugs live here — REG_SZ versus REG_EXPAND_SZ, default values on subkeys,
delete-subkey-tree behaviour — and a mock would only assert our assumptions about those
rather than the truth. The subtree is removed on dispose; a test run leaves nothing behind
and never touches your real sound settings.

**No binary fixtures.** WAV files are synthesised per test, so a case can state the
property it cares about (2.5 seconds long, MP3-in-a-WAV-container) instead of a reader
having to open a blob to find out what makes it interesting.

To produce a single self-contained exe:

```bash
dotnet publish src/WinChime.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

### Layout

```
src/WinChime.Core/          class library, no UI, zero NuGet dependencies
  Model/                    SoundEvent, SystemInfo, BackupManifest, OperationResult…
  Sounds/                   SoundSchemeService, WaveFile, SoundPreview,
                            AudioTranscoder, SoundPackService
  Startup/                  StartupSoundService, LogonChimeService, SystemChimeResource
  Personalization/          WallpaperService, LockScreenService
  Safety/                   SystemProbe, BackupService, RestorePointService
  Elevation/                ElevationHelper  (the elevated-op protocol)
  Interop/                  NativeMethods, ProcessRunner
src/WinChime.App/           WPF UI (net8.0-windows, asInvoker manifest)
```

`WinChime.Core` targets `net8.0-windows` specifically so `Microsoft.Win32.Registry`,
`WindowsIdentity` and the Win32 P/Invokes resolve from the shared framework without package
references.

The one NuGet dependency is **NAudio 2.2.1**, used to decode audio for conversion. The
alternative was P/Invoking Media Foundation directly to keep a zero-dependency build —
several hundred lines of COM interop for the same result, and interop that is much easier
to get subtly wrong than to review. Pinned to 2.x because NAudio 3.x requires .NET 9.

> **Building on a machine with Smart App Control enabled:** SAC in Enforce mode blocks
> loading freshly built unsigned assemblies, so `dotnet test` can fail with
> `An Application Control policy has blocked this file (0x800711C7)` even though the build
> succeeded. Check with
> `Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy` — a
> `VerifiedAndReputablePolicyState` of `1` means Enforce. CI is unaffected. Note that
> turning SAC off is **irreversible** without reinstalling Windows.

Every mutating call returns an `OperationResult` rather than throwing, so the UI can show a
precise reason (access denied, policy blocked, file missing) instead of a stack trace.

### Command-line modes

| Invocation | Effect |
|---|---|
| *(no args)* | Normal UI |
| `--play-chime "<wav>"` | Plays the file synchronously and exits. Used by the logon task; never creates a window. |
| `--elevated-op "<json>"` | Internal. The elevated child spawned by `ElevationHelper`. |

---

## Known limitations

- **Lock screen greys out Settings** while applied. Inherent to the CSP mechanism.
- **Conversion needs Media Foundation.** Absent on Server SKUs without Desktop Experience
  and on "N" editions without the Media Feature Pack. `AudioTranscoder.IsAvailable` probes
  for it and the UI degrades to a clear message rather than failing obscurely.
- **No trimming or normalisation yet.** Over-long files are flagged but not shortened, and
  quiet files are not levelled. Natural follow-ups now that decoding exists.
- **Scheme apply is not atomic.** A partial scheme leaves unlisted events untouched rather
  than silencing them, which is the safer failure mode but means "apply" is not a clean
  reset. Use *Windows Default* for that.
- **`ProductName` lies on Windows 11.** The registry value still reads "Windows 10 …" on
  every Win11 build; `SystemProbe` corrects it from the build number.

## Distribution notes

- **Code signing.** Not strictly required now that no system binary is touched, but an EV
  certificate still avoids SmartScreen friction on an unknown publisher.
- **Microsoft Store.** Store policy rules out an app that writes the HKLM and
  PersonalizationCSP values used here, so sideload/direct download only.

## License

[MIT](LICENSE) © 2026 Maximo Martinez Jr.
