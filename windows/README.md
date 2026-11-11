# Clicky for Windows (phase 1)

A native Windows port of the Clicky companion: hold a hotkey, ask a question out loud, and Clicky looks at your screens, answers with Claude, speaks the answer, and flies a small blue cursor to whatever it is talking about. It runs against the same Cloudflare Worker as the Mac app (`worker/`), so no API keys ship in the app.

Phase 1 is feature parity with the open-source Mac app: push-to-talk, multi-monitor screenshots, streamed Claude replies, ElevenLabs speech, and `[POINT:x,y:label:screenN]` pointing.

## Prerequisites

- Windows 10 version 2004 (build 19041) or later, or Windows 11. Overlay capture exclusion (`WDA_EXCLUDEFROMCAPTURE`) needs 2004 or later.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Node.js, for running the worker locally with Wrangler
- API keys for Anthropic, AssemblyAI and ElevenLabs, kept only in `worker/.dev.vars` (never in this folder)

## Run the worker locally

```bash
cd worker
npm install
```

Create `worker/.dev.vars` (it is git-ignored) with one `NAME=value` line for each of `ANTHROPIC_API_KEY`, `ASSEMBLYAI_API_KEY` and `ELEVENLABS_API_KEY`. Then start the local worker:

```bash
npx wrangler dev
```

It listens on `http://localhost:8787`, which is the app's default worker URL.

## Build and run

```bash
dotnet build windows/Clicky.Windows.sln -c Release
```

```bash
windows/src/Clicky.Windows/bin/Release/net9.0-windows10.0.22621.0/Clicky.Windows.exe
```

Clicky has no main window. Look for the blue triangle in the notification area (Windows 11 may put new icons in the overflow menu behind the `^` arrow). Click it to open the panel. On first run, press Start to see the welcome bubble and a pointing demo. After that, hold **Ctrl + Alt**, speak, and let go.

Run the unit tests:

```bash
dotnet test windows/Clicky.Windows.sln
```

## Configuration

**Worker URL**, first match wins:

1. The `CLICKY_WORKER_URL` environment variable
2. `"WorkerUrl"` in `appsettings.json` next to the executable (copied from `windows/appsettings.json` at build time)
3. `http://localhost:8787`

There is no built-in deployed worker URL. To use a deployed worker, set one of the first two.

**User settings** live in `%LOCALAPPDATA%\Clicky\settings.json`:

| Key | Values | Default |
|-----|--------|---------|
| `selectedModel` | `claude-sonnet-5-5`, `claude-opus-5-5` (also set from the panel) | `claude-sonnet-5-5` |
| `pushToTalkShortcut` | `CtrlAlt`, `ShiftCtrl`, `CtrlAltSpace`, `ShiftCtrlSpace` | `CtrlAlt` |
| `hasCompletedOnboarding` | `true` or `false` | `false` |
| `isClickyCursorEnabled` | `false` shows the cursor only during an interaction | `true` |

Edit the file while Clicky is closed. Model identifiers live in one file: `src/Clicky.Core/Claude/ClaudeModelIdentifiers.cs`.

**Logs** go to `%LOCALAPPDATA%\Clicky\logs\clicky.log`. They never contain API keys or the AssemblyAI token.

## Debug modes

Each mode prints to the terminal that launched it and exits.

| Flag | What it does |
|------|--------------|
| `--capture-test` | Opens the overlay with a magenta test square on every monitor, captures every screen, writes `screen1.jpg` to `screenN.jpg` plus `report.txt` to `%TEMP%\clicky-capture-test`, and checks that the overlay is absent from every image. In automatic mode it also captures with each backend separately into `gdi\` and `wgc\`. |
| `--overlay-test` | Runs the overlay through idle, listening, processing, a pointing flight on each monitor, and responding. Checks that the overlay never takes focus and never blocks clicks. No worker needed. |
| `--token-test` | Fetches an AssemblyAI token from the worker and prints its length only. |
| `--ask "what is on my screen"` | One full turn with typed text instead of speech: screenshots, streamed reply printed live, pointing flight, spoken answer. |
| `--show-in-captures` | Turns off capture exclusion so screenshots can include the overlay (for documentation only). |
| `--open-panel` | Opens the panel at startup. |
| `--capture-backend=auto\|wgc\|gdi` | Forces a screen capture backend. |

## How it is built

- `src/Clicky.Core` (net9.0, no UI): worker config, the Anthropic SSE parser and streaming client, the POINT tag parser (same regex as the Mac), screenshot-to-monitor coordinate mapping, conversation history, the push-to-talk state machine, the AssemblyAI v3 streaming session, settings, and the cursor flight math. Unit tested in `tests/Clicky.Core.Tests`.
- `src/Clicky.Windows` (WPF, tray only):
  - `Hotkey/`: low-level keyboard hook (`WH_KEYBOARD_LL`) on its own thread, listen-only.
  - `AudioIO/`: WASAPI microphone capture with NAudio, resampled to 16 kHz mono PCM16, MP3 playback, and the Windows system voice for the error fallback.
  - `Capture/`: Windows.Graphics.Capture per monitor, with a GDI `BitBlt` fallback, scaled to a 1280 px longest edge and encoded as JPEG.
  - `CursorOverlay/`: one topmost, transparent, click-through, non-activating window per monitor, excluded from screen capture with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
  - `Tray/` and `Panel/`: the notification-area icon and the dark floating panel.
  - `Orchestration/`: `CompanionManager` (the state machine ported from `CompanionManager.swift`) and `BuddyDictationManager`.
  - `Debugging/`: the debug modes above.

The process is per-monitor DPI aware (v2). Every Win32 coordinate is in physical pixels, and conversion to WPF device-independent pixels happens only inside the overlay windows.

The only third-party package is NAudio (WASAPI capture and MP3 playback). Tests use xUnit.

## Known limits

- **AltGr layouts.** On keyboards with an AltGr key, Windows reports AltGr as Ctrl + Alt, so AltGr alone (and typing characters such as @ on some layouts) starts push-to-talk. Switch `pushToTalkShortcut` to `CtrlAltSpace` if that gets in the way.
- **Keys pass through.** The hook only listens, like the Mac's event tap, so the shortcut also reaches the focused app.
- **Elevated windows.** When an app running as administrator has focus, Windows may not deliver its key events to Clicky's hook, so push-to-talk may not start there.
- **Onboarding.** The Mac's intro video and background music are not ported (see `WindowsPortTodoConstants.cs`). Windows goes from the welcome bubble straight to the pointing demo.
- **Not ported.** Usage analytics, the onboarding email form, and the feedback button point at the upstream author's accounts and are left out.
- **Topmost order.** The overlay is a topmost window, so the taskbar, the Start menu, and full-screen exclusive games can draw above it.
- **No single-instance guard.** Starting Clicky twice installs two hooks and two overlays.
- **Microphone status.** The panel reads the Windows privacy toggles from the registry as a best-effort hint; the error from actually opening the microphone is the authoritative signal.
