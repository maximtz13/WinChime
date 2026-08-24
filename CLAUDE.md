# WinChime — working notes for Claude

Windows sound and personalisation app. WPF + .NET 8. Repo: https://github.com/maximtz13/WinChime

Read this before changing anything. The README documents the product; this documents how to
work on it and the traps that already cost time.

## Commands

```bash
dotnet build WinChime.sln -c Release -warnaserror
dotnet test WinChime.sln -c Release
dotnet run --project src/WinChime.App          # the GUI
```

`dotnet` is at `C:\Program Files\dotnet\dotnet.exe`; `gh` is at `C:\Program Files\GitHub CLI\gh.exe`.
Neither is reliably on PATH in a fresh shell — use the full path.

Cutting a release is `git tag -a v0.x.0 -F - <<'EOF' … EOF` then `git push origin v0.x.0`.
`release.yml` does the rest. Release notes live in `.github/release-notes-template.md`,
so a release never needs CI edited.

## Smart App Control WILL block your build

Freshly built binaries fail to load with `An Application Control policy has blocked this
file (0x800711C7)`. **This is not a bug in the code.** It is intermittent — sometimes
clearing in seconds, sometimes blocking for 15+ minutes.

Symptoms: `dotnet test` fails every test instantly with `FileLoadException`, or the app
exits immediately with code `-532462766`.

Detect it with `-v minimal` (not `-v quiet`, which hides the error) and match on
`Application Control policy`. Retry in a loop, or push and let CI verify. Do not "fix"
anything in response to it. Never suggest disabling SAC: it is irreversible without
reinstalling Windows.

## Conventions

- **Branch first, then commit.** One feature per PR, PR body explains the reasoning, merge
  with `gh pr merge N --squash --delete-branch`. Never commit directly to `main`.
- **Logic goes in `WinChime.Core`; the WPF layer stays thin.** This is why 265 tests exist:
  CI cannot exercise a window, but it can exercise `Core`. `CliRunner` takes an injected
  `TextWriter`; every service takes an injectable registry root. Keep that up.
- **Tests never touch real settings.** `ScratchRegistry` creates a throwaway HKCU subtree.
  `BackupService`, `SoundSchemeService`, `CursorSchemeService`, `AccentColorService` and
  `CliRunner` all accept injectable roots for exactly this reason.
- **No registry mocking.** Registry semantics (REG_SZ vs REG_EXPAND_SZ, default values on
  subkeys) are where the bugs are; a mock would assert our assumptions instead of the truth.
- Comments explain *why*, especially where behaviour looks arbitrary. Much of this code
  encodes undocumented Windows behaviour that was derived experimentally.

## Traps that have already bitten

- **`--` is illegal inside an XML comment.** Broke the build three times in `.csproj` and
  `.xaml` files. Do not write `--play-chime` in a comment.
- **WPF projects drop `System.IO` from implicit usings** (`System.IO.Path` would clash with
  `Shapes.Path`). Add the using explicitly in `WinChime.App`.
- **Scripted `sed`/`perl` edits have silently done the wrong thing three times**, most
  recently prepending table rows above the README title. Always read the file back after a
  scripted edit; do not trust a zero exit code.
- **The Bash tool mangles apostrophes in heredocs.** Use the Write tool for C# files.
- **Verify UI by rendering and looking at it.** The Wi-Fi-shaped icon, an arc spilling
  outside the icon tile, a scheme combo showing the wrong scheme, and cursors mislabelled
  "Custom" were all invisible in code review and obvious in a screenshot. `PrintWindow` with
  `SetProcessDPIAware` first, or captures come out cropped.
- **Check the app against an independent source, not its own output.** The restore-point bug
  (reporting success when Windows had skipped) was only visible in the Windows event log.

## Windows behaviour this app depends on

Each of these was derived experimentally and is documented in the relevant source file:

- Sound events silently play *nothing* if the file is not uncompressed PCM. No error anywhere.
- The logon chime is a WAVE resource in `imageres.dll` (`#5080`), not a file. Read-only
  extraction is safe; patching it is refused by design.
- A cursor scheme is one comma-separated string where meaning comes from *position*. 17 roles
  then 2 metadata entries. Order verified against the shipped Windows Aero scheme.
- The accent lives in `AccentPalette[3]`, **not** `DWM\AccentColor`, which can be stale. The
  shade ladder is a multiplicative scale, verified to 1/255 against a live palette.
- `SRSetRestorePoint` returns success with sequence 0 when Windows *skipped* the request.

## Current state

265 tests, zero warnings at `-warnaserror`, 0 open PRs, releases through v0.5.0.

Never exercised: applying a *new* accent colour (write path is test-covered, but it repaints
the desktop so it was left alone). Everything else has run at least once.

Deliberately rejected, do not implement: **UEFI boot logo replacement** (requires disabling
Secure Boot) and **patching `imageres.dll`** (reverted by `sfc` and every cumulative update).
Both are documented in the README's Scope section with reasoning.

Optional next steps, none required: cursor packs, a live cursor preview in the details panel,
further personalisation surfaces. The real bottleneck is not code — reputation with
SmartScreen and SAC only builds through download volume.
