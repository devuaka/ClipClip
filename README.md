# ClipClip

Instant replay clipping for Windows. ClipClip keeps the last few seconds of your game or desktop in memory, and saves them as an MP4 when you press F8, say "clip that", or hit the Clip button.

Everything runs on your PC. No account, no uploads.

## Install

Run `artifacts/release/ClipClip-win-Setup.exe`. It installs for your user only and needs no admin rights. Windows SmartScreen may warn because the installer is not code signed: choose More info, then Run anyway.

## Use

- **Games only** records while a game is running and starts by itself.
- **Games and desktop** records your screen all the time.
- Press **F8** to save the last 30 seconds. Change both in Settings.
- Clips are normal MP4 files in `Videos\ClipClip`.
- Closing the window keeps ClipClip running in the system tray. The tray icon is colored while recording and gray when not. Right-click it to exit.

If a game isn't picked up, add its `.exe` on the Games page.

## Build

Requires the .NET 10 SDK.

```bash
powershell -File scripts/fetch-third-party.ps1
```

```bash
dotnet build src/ClipClip -c Release
```

```bash
powershell -File scripts/package.ps1
```

`package.ps1` publishes a self-contained build and writes the installer to `artifacts/release`.

## Website

`docs/` is a static site, published by GitHub Pages, with no build step. `scripts/package.ps1` copies the installer and portable zip into `docs/downloads`.

Upload the folder to any static host that allows 200 MB files. If yours does not (GitHub Pages stops at 100 MB per file), upload the two files somewhere else, such as a GitHub release, and set `DOWNLOAD_BASE` at the top of `docs/site.js`.

## Test

The self-test runs real capture, encoding and audio on this PC.

```bash
dotnet run --project tests/ClipClip.SelfTest -c Release -- ring
```

| Test | Checks |
|---|---|
| `ring` | Buffer byte integrity through wraps, RAM and disk modes, failed saves leave no file |
| `basic <Nvenc/Amf/Qsv/Software>` | Record and save with one encoder |
| `multiclip` | Clips during recording, duplicate presses, no dropped frames |
| `soak <seconds>` | Memory stays flat after the buffer fills |
| `sync` | Measures video, system and mic offsets from a saved clip. Needs two virtual audio cables. |
| `voice <wav>` | Speech through a virtual cable saves a clip |
| `voicewav <phrase> <wavs>` | Detector accuracy on recordings |
| `customgame` | Custom game recognized on launch, recording stops on exit |
| `autodetect <exe>` | Launches a real game and checks automatic detection |
| `micloss` | Mic disappears and returns during recording |
| `resize` | Window resizing does not restart the encoder |
| `recovery` | Failed sessions restart and fall back to software encoding |
| `fpsimpact` | GPU-bound frame rate with recording off and on |

Test clips go to `%TEMP%\ClipClipSelfTest` and contain whatever was on screen.

## Layout

- `src/ClipClip.Engine`: capture, encoding, audio, detection, voice, library. No UI.
- `src/ClipClip`: the WPF app.
- `tests/ClipClip.SelfTest`: hardware-in-the-loop tests.
- `TECHNICAL_DECISIONS.md`: why it is built this way, measurements and known limits.
