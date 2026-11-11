# Clicky - Agent Instructions

<!-- This is the single source of truth for all AI coding agents. CLAUDE.md is a symlink to this file. -->
<!-- AGENTS.md spec: https://github.com/agentsmd/agents.md — supported by Claude Code, Cursor, Copilot, Gemini CLI, and others. -->

## Overview

macOS menu bar companion app. Lives entirely in the macOS status bar (no dock icon, no main window). Clicking the menu bar icon opens a custom floating panel with companion voice controls. Uses push-to-talk (ctrl+option) to capture voice input, transcribes it via AssemblyAI streaming, and sends the transcript + a screenshot of the user's screen to Claude. Claude responds with text (streamed via SSE) and voice (ElevenLabs TTS). A blue cursor overlay can fly to and point at UI elements Claude references on any connected monitor.

All API keys live on a Cloudflare Worker proxy — nothing sensitive ships in the app.

A native Windows port (C#, .NET 9, WPF, notification-area app) lives in `windows/` and talks to the same Worker. See [Windows Port](#windows-port) below and `windows/README.md`.

## Architecture

- **App Type**: Menu bar-only (`LSUIElement=true`), no dock icon or main window
- **Framework**: SwiftUI (macOS native) with AppKit bridging for menu bar panel and cursor overlay
- **Pattern**: MVVM with `@StateObject` / `@Published` state management
- **AI Chat**: Claude (Sonnet 4.6 default, Opus 4.6 optional) via Cloudflare Worker proxy with SSE streaming
- **Speech-to-Text**: AssemblyAI real-time streaming (`u3-rt-pro` model) via websocket, with OpenAI and Apple Speech as fallbacks
- **Text-to-Speech**: ElevenLabs (`eleven_flash_v2_5` model) via Cloudflare Worker proxy
- **Screen Capture**: ScreenCaptureKit (macOS 14.2+), multi-monitor support
- **Voice Input**: Push-to-talk via `AVAudioEngine` + pluggable transcription-provider layer. System-wide keyboard shortcut via listen-only CGEvent tap.
- **Element Pointing**: Claude embeds `[POINT:x,y:label:screenN]` tags in responses. The overlay parses these, maps coordinates to the correct monitor, and animates the blue cursor along a bezier arc to the target.
- **Concurrency**: `@MainActor` isolation, async/await throughout
- **Analytics**: PostHog via `ClickyAnalytics.swift`

### API Proxy (Cloudflare Worker)

The app never calls external APIs directly. All requests go through a Cloudflare Worker (`worker/src/index.ts`) that holds the real API keys as secrets.

| Route | Upstream | Purpose |
|-------|----------|---------|
| `POST /chat` | `api.anthropic.com/v1/messages` | Claude vision + streaming chat |
| `POST /tts` | `api.elevenlabs.io/v1/text-to-speech/{voiceId}` | ElevenLabs TTS audio |
| `POST /transcribe-token` | `streaming.assemblyai.com/v3/token` | Fetches a short-lived (480s) AssemblyAI websocket token |

Worker secrets: `ANTHROPIC_API_KEY`, `ASSEMBLYAI_API_KEY`, `ELEVENLABS_API_KEY`
Worker vars: `ELEVENLABS_VOICE_ID`

### Key Architecture Decisions

**Menu Bar Panel Pattern**: The companion panel uses `NSStatusItem` for the menu bar icon and a custom borderless `NSPanel` for the floating control panel. This gives full control over appearance (dark, rounded corners, custom shadow) and avoids the standard macOS menu/popover chrome. The panel is non-activating so it doesn't steal focus. A global event monitor auto-dismisses it on outside clicks.

**Cursor Overlay**: A full-screen transparent `NSPanel` hosts the blue cursor companion. It's non-activating, joins all Spaces, and never steals focus. The cursor position, response text, waveform, and pointing animations all render in this overlay via SwiftUI through `NSHostingView`.

**Global Push-To-Talk Shortcut**: Background push-to-talk uses a listen-only `CGEvent` tap instead of an AppKit global monitor so modifier-based shortcuts like `ctrl + option` are detected more reliably while the app is running in the background.

**Shared URLSession for AssemblyAI**: A single long-lived `URLSession` is shared across all AssemblyAI streaming sessions (owned by the provider, not the session). Creating and invalidating a URLSession per session corrupts the OS connection pool and causes "Socket is not connected" errors after a few rapid reconnections.

**Transient Cursor Mode**: When "Show Clicky" is off, pressing the hotkey fades in the cursor overlay for the duration of the interaction (recording → response → TTS → optional pointing), then fades it out automatically after 1 second of inactivity.

## Key Files

| File | Lines | Purpose |
|------|-------|---------|
| `leanring_buddyApp.swift` | ~89 | Menu bar app entry point. Uses `@NSApplicationDelegateAdaptor` with `CompanionAppDelegate` which creates `MenuBarPanelManager` and starts `CompanionManager`. No main window — the app lives entirely in the status bar. |
| `CompanionManager.swift` | ~1026 | Central state machine. Owns dictation, shortcut monitoring, screen capture, Claude API, ElevenLabs TTS, and overlay management. Tracks voice state (idle/listening/processing/responding), conversation history, model selection, and cursor visibility. Coordinates the full push-to-talk → screenshot → Claude → TTS → pointing pipeline. |
| `MenuBarPanelManager.swift` | ~243 | NSStatusItem + custom NSPanel lifecycle. Creates the menu bar icon, manages the floating companion panel (show/hide/position), installs click-outside-to-dismiss monitor. |
| `CompanionPanelView.swift` | ~761 | SwiftUI panel content for the menu bar dropdown. Shows companion status, push-to-talk instructions, model picker (Sonnet/Opus), permissions UI, DM feedback button, and quit button. Dark aesthetic using `DS` design system. |
| `OverlayWindow.swift` | ~881 | Full-screen transparent overlay hosting the blue cursor, response text, waveform, and spinner. Handles cursor animation, element pointing with bezier arcs, multi-monitor coordinate mapping, and fade-out transitions. |
| `CompanionResponseOverlay.swift` | ~217 | SwiftUI view for the response text bubble and waveform displayed next to the cursor in the overlay. |
| `CompanionScreenCaptureUtility.swift` | ~132 | Multi-monitor screenshot capture using ScreenCaptureKit. Returns labeled image data for each connected display. |
| `BuddyDictationManager.swift` | ~866 | Push-to-talk voice pipeline. Handles microphone capture via `AVAudioEngine`, provider-aware permission checks, keyboard/button dictation sessions, transcript finalization, shortcut parsing, contextual keyterms, and live audio-level reporting for waveform feedback. |
| `BuddyTranscriptionProvider.swift` | ~100 | Protocol surface and provider factory for voice transcription backends. Resolves provider based on `VoiceTranscriptionProvider` in Info.plist — AssemblyAI, OpenAI, or Apple Speech. |
| `AssemblyAIStreamingTranscriptionProvider.swift` | ~478 | Streaming transcription provider. Fetches temp tokens from the Cloudflare Worker, opens an AssemblyAI v3 websocket, streams PCM16 audio, tracks turn-based transcripts, and delivers finalized text on key-up. Shares a single URLSession across all sessions. |
| `OpenAIAudioTranscriptionProvider.swift` | ~317 | Upload-based transcription provider. Buffers push-to-talk audio locally, uploads as WAV on release, returns finalized transcript. |
| `AppleSpeechTranscriptionProvider.swift` | ~147 | Local fallback transcription provider backed by Apple's Speech framework. |
| `BuddyAudioConversionSupport.swift` | ~108 | Audio conversion helpers. Converts live mic buffers to PCM16 mono audio and builds WAV payloads for upload-based providers. |
| `GlobalPushToTalkShortcutMonitor.swift` | ~132 | System-wide push-to-talk monitor. Owns the listen-only `CGEvent` tap and publishes press/release transitions. |
| `ClaudeAPI.swift` | ~291 | Claude vision API client with streaming (SSE) and non-streaming modes. TLS warmup optimization, image MIME detection, conversation history support. |
| `OpenAIAPI.swift` | ~142 | OpenAI GPT vision API client. |
| `ElevenLabsTTSClient.swift` | ~81 | ElevenLabs TTS client. Sends text to the Worker proxy, plays back audio via `AVAudioPlayer`. Exposes `isPlaying` for transient cursor scheduling. |
| `ElementLocationDetector.swift` | ~335 | Detects UI element locations in screenshots for cursor pointing. |
| `DesignSystem.swift` | ~880 | Design system tokens — colors, corner radii, shared styles. All UI references `DS.Colors`, `DS.CornerRadius`, etc. |
| `ClickyAnalytics.swift` | ~121 | PostHog analytics integration for usage tracking. |
| `WindowPositionManager.swift` | ~262 | Window placement logic, Screen Recording permission flow, and accessibility permission helpers. |
| `AppBundleConfiguration.swift` | ~28 | Runtime configuration reader for keys stored in the app bundle Info.plist. |
| `worker/src/index.ts` | ~142 | Cloudflare Worker proxy. Three routes: `/chat` (Claude), `/tts` (ElevenLabs), `/transcribe-token` (AssemblyAI temp token). |

### Windows port key files (`windows/`)

Paths are relative to `windows/src/` unless they start with `windows/`.

| File | Lines | Purpose |
|------|-------|---------|
| `windows/Clicky.Windows.sln` | ~69 | Solution: Clicky.Core, Clicky.Windows, Clicky.Core.Tests. |
| `windows/Directory.Build.props` | ~10 | Shared C# settings (latest language version, nullable, implicit usings). |
| `windows/appsettings.json` | ~3 | `WorkerUrl` fallback (default `http://localhost:8787`), copied next to the exe. |
| `Clicky.Core/Configuration/ClickyWorkerConfiguration.cs` | ~141 | Worker URL resolution: `CLICKY_WORKER_URL`, then `appsettings.json`, then `http://localhost:8787`. Route URLs for `/chat`, `/tts`, `/transcribe-token`. |
| `Clicky.Core/Claude/ClaudeModelIdentifiers.cs` | ~44 | The only place model ids live: `claude-sonnet-5-5` (default) and `claude-opus-5-5`. |
| `Clicky.Core/Claude/ClaudeServerSentEventParser.cs` | ~220 | Byte-level incremental SSE parser for Anthropic streams (split chunks, split UTF-8, CRLF, error events). |
| `Clicky.Core/Claude/ClaudeStreamingChatClient.cs` | ~290 | Port of `ClaudeAPI.swift`: builds the Messages body, POSTs to `{worker}/chat`, streams text deltas. |
| `Clicky.Core/Prompts/CompanionSystemPrompts.cs` | ~73 | Both system prompts, verbatim from `CompanionManager.swift` (a test compares them with the Swift source). |
| `Clicky.Core/Pointing/PointTagParser.cs` | ~91 | `[POINT:x,y:label:screenN]` and `[POINT:none]` parser with the Swift regex. |
| `Clicky.Core/Pointing/ScreenCoordinateMapper.cs` | ~145 | Screenshot pixels to physical pixels to overlay DIPs; 1280 px screenshot sizing. |
| `Clicky.Core/Pointing/PointTargetResolver.cs` | ~56 | Picks the target screen (`:screenN` or cursor screen) and maps Claude's coordinate. |
| `Clicky.Core/Screen/CapturedScreen.cs`, `ScreenCaptureLabeling.cs` | ~84 | Captured screen record; cursor-first ordering and the Mac's screen label text. |
| `Clicky.Core/Conversation/ConversationHistory.cs` | ~75 | Last ten user/assistant exchanges, POINT tag removed. |
| `Clicky.Core/PushToTalk/PushToTalkShortcut.cs` | ~137 | Shortcut options (Ctrl + Alt default), virtual-key codes, settings values. |
| `Clicky.Core/PushToTalk/PushToTalkShortcutStateMachine.cs` | ~217 | Press/release rules from `BuddyPushToTalkShortcut`, auto-repeat dedupe, stuck-key reconciliation, AltGr note. |
| `Clicky.Core/Transcription/AssemblyAIStreamingConfiguration.cs` | ~114 | v3 websocket URL (never logged, it carries the token), 100 ms frames, keyterms. |
| `Clicky.Core/Transcription/AssemblyAIStreamingTranscriptionSession.cs` | ~491 | One v3 websocket session: Begin, Turn, ForceEndpoint with a 1.4 s grace period, partial-transcript fallback. |
| `Clicky.Core/Transcription/AssemblyAIStreamingTranscriptionProvider.cs` | ~76 | Fetches a token per session and shares one `SocketsHttpHandler` across websockets (the Swift shared URLSession). |
| `Clicky.Core/Transcription/AssemblyAITemporaryTokenClient.cs`, `AssemblyAITurnTranscriptAccumulator.cs` | ~168 | Token fetch from `{worker}/transcribe-token`; turn-ordered transcript bookkeeping. |
| `Clicky.Core/TextToSpeech/ElevenLabsSpeechClient.cs` | ~84 | POSTs to `{worker}/tts` with `eleven_flash_v2_5`, returns MP3 bytes. |
| `Clicky.Core/Audio/AudioPowerLevelMeter.cs`, `Pcm16AudioFrameChunker.cs` | ~128 | Waveform level math from the Mac; regroups PCM16 into websocket frames. |
| `Clicky.Core/Companion/CompanionVoiceState.cs` | ~80 | Idle/listening/processing/responding and the panel status words. |
| `Clicky.Core/Overlay/CursorOverlayAnimationMath.cs` | ~274 | Bezier flight, follow spring, waveform bar heights, pointing phrases, onboarding prompt text. |
| `Clicky.Core/Settings/ClickyUserSettings.cs` | ~117 | `%LOCALAPPDATA%\Clicky\settings.json` load and atomic save. |
| `Clicky.Core/Diagnostics/ClickyDiagnosticLog.cs` | ~32 | Logging hook Core uses without a logging dependency. |
| `Clicky.Windows/App.xaml`, `App.xaml.cs`, `app.manifest` | ~90 | No main window, PerMonitorV2 DPI awareness, unhandled-exception logging. |
| `Clicky.Windows/Orchestration/CompanionManager.cs` | ~794 | Port of `CompanionManager.swift`: push-to-talk, response pipeline, pointing, transient cursor, onboarding demo, panel actions. |
| `Clicky.Windows/Orchestration/BuddyDictationManager.cs` | ~434 | Keyboard push-to-talk dictation: microphone plus one AssemblyAI session per press, finalize on key-up. |
| `Clicky.Windows/Orchestration/ClickyApplicationServices.cs`, `ClickyApplicationStartup.cs` | ~225 | Composition root and startup (tray mode or a debug mode). |
| `Clicky.Windows/Orchestration/WindowsPortTodoConstants.cs` | ~47 | Values the port could not determine (fallback line, video, music, feedback link, analytics). |
| `Clicky.Windows/Companion/CompanionOverlayState.cs`, `CompanionPanelState.cs` | ~298 | Observable state the overlays and the panel render; the panel's copy and visibility rules. |
| `Clicky.Windows/Hotkey/LowLevelKeyboardHookService.cs`, `KeyboardHookNativeMethods.cs` | ~619 | `WH_KEYBOARD_LL` on a dedicated thread, listen-only, 250 ms stuck-key check. |
| `Clicky.Windows/AudioIO/MicrophoneCaptureService.cs`, `CapturedAudioFormatConverter.cs` | ~819 | NAudio WASAPI shared-mode capture, any mix format to 16 kHz mono PCM16, level events. |
| `Clicky.Windows/AudioIO/MicrophoneAccessStatusReader.cs`, `MicrophoneUnavailableException.cs` | ~141 | Best-effort microphone privacy status; opens `ms-settings:privacy-microphone`. |
| `Clicky.Windows/AudioIO/SpeechPlaybackService.cs` | ~409 | MP3 playback with NAudio and the Windows system voice for the error fallback. |
| `Clicky.Windows/Capture/ScreenCaptureService.cs` | ~322 | Every monitor, cursor screen first, 1280 px JPEG, backend selection and fallback. |
| `Clicky.Windows/Capture/WindowsGraphicsCaptureMonitorScreenshotCapturer.cs` | ~436 | Windows.Graphics.Capture per HMONITOR (D3D11 device, borderless, one frame). |
| `Clicky.Windows/Capture/GdiMonitorScreenshotCapturer.cs`, `ScreenshotJpegEncoder.cs`, `CaptureNativeMethods.cs`, `CapturedMonitorPixels.cs`, `ScreenCaptureBackendPreference.cs` | ~480 | GDI BitBlt fallback, scaling and JPEG encoding, interop declarations. |
| `Clicky.Windows/CursorOverlay/OverlayWindowManager.cs`, `OverlayWindow.cs` | ~726 | One topmost, transparent, click-through, non-activating window per monitor, excluded from capture with `WDA_EXCLUDEFROMCAPTURE`. |
| `Clicky.Windows/CursorOverlay/BlueCursorOverlayView.cs` | ~835 | Port of `BlueCursorView`: following, waveform, spinner, flight, pointing, welcome, bubbles. |
| `Clicky.Windows/CursorOverlay/BlueCursorShapeVisuals.cs`, `OverlayBubbleVisuals.cs`, `OverlayAnimationPrimitives.cs`, `OverlayElementUpdater.cs`, `OverlayVisualStyle.cs`, `OverlayNativeMethods.cs` | ~1143 | Triangle, waveform, spinner and bubble visuals, tweens, and overlay interop. |
| `Clicky.Windows/Tray/TrayIconManager.cs` | ~198 | Notification-area icon (blue triangle drawn at runtime); click opens the panel. |
| `Clicky.Windows/Panel/CompanionPanelWindow.xaml`, `.xaml.cs` | ~916 | Dark floating panel mirroring `CompanionPanelView.swift`, non-activating. |
| `Clicky.Windows/Panel/PanelOutsideClickWatcher.cs`, `CompanionPanelPlacementCalculator.cs`, `ClickyDesignTokens.cs`, `PanelNativeMethods.cs` | ~638 | Outside-click dismissal (`WH_MOUSE_LL`), placement by taskbar edge, `DesignSystem.swift` colors. |
| `Clicky.Windows/Display/DisplayMonitorInfo.cs` | ~155 | Monitor enumeration with physical bounds, work area and DPI; cursor position. |
| `Clicky.Windows/Diagnostics/ClickyApplicationLog.cs` | ~104 | `%LOCALAPPDATA%\Clicky\logs\clicky.log` plus the parent console for debug modes. |
| `Clicky.Windows/Debugging/*.cs` | ~569 | `--capture-test`, `--overlay-test`, `--token-test`, `--ask` and their flags. |
| `windows/tests/Clicky.Core.Tests/*.cs` | ~1500 | xUnit tests for every Core component, including a verbatim prompt check against the Swift source. |

## Build & Run

```bash
# Open in Xcode
open leanring-buddy.xcodeproj

# Select the leanring-buddy scheme, set signing team, Cmd+R to build and run

# Known non-blocking warnings: Swift 6 concurrency warnings,
# deprecated onChange warning in OverlayWindow.swift. Do NOT attempt to fix these.
```

**Do NOT run `xcodebuild` from the terminal** — it invalidates TCC (Transparency, Consent, and Control) permissions and the app will need to re-request screen recording, accessibility, etc.

## Cloudflare Worker

```bash
cd worker
npm install

# Add secrets
npx wrangler secret put ANTHROPIC_API_KEY
npx wrangler secret put ASSEMBLYAI_API_KEY
npx wrangler secret put ELEVENLABS_API_KEY

# Deploy
npx wrangler deploy

# Local dev (create worker/.dev.vars with your keys)
npx wrangler dev
```

## Windows Port

`windows/` holds a native Windows port of the same companion (phase 1: open-source parity). It uses the same Worker routes and the same two system prompts, and it never calls any other backend.

- **App type**: WPF on .NET 9 (`net9.0-windows10.0.22621.0`), notification-area icon only, no main window and no taskbar button. WinForms is enabled only for `NotifyIcon`.
- **Split**: `src/Clicky.Core` (net9.0, no UI) holds everything that decides behavior and is unit tested in `tests/Clicky.Core.Tests`. `src/Clicky.Windows` holds the Win32, WinRT and WPF pieces.
- **Push-to-talk**: `WH_KEYBOARD_LL` hook on a dedicated thread, listen-only like the Mac event tap. Default Ctrl + Alt (which equals AltGr on some layouts); configurable in `%LOCALAPPDATA%\Clicky\settings.json`.
- **Speech to text**: NAudio WASAPI capture resampled to 16 kHz mono PCM16, streamed to AssemblyAI v3 with a token from `/transcribe-token`, finalized on key-up.
- **Screens**: Windows.Graphics.Capture per monitor with a GDI `BitBlt` fallback, cursor screen first, 1280 px JPEG, labeled exactly like the Mac.
- **Overlay**: one topmost, transparent, click-through, non-activating window per monitor, excluded from screenshots with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- **Coordinates**: the process is per-monitor DPI aware v2. Everything is physical pixels until the overlay converts to DIPs.
- **Worker URL**: `CLICKY_WORKER_URL`, then `WorkerUrl` in `windows/appsettings.json`, then `http://localhost:8787`. There is no hardcoded deployed URL.

```bash
# Build and test
dotnet build windows/Clicky.Windows.sln -c Release
dotnet test windows/Clicky.Windows.sln

# Run (start the Worker first with npx wrangler dev)
windows/src/Clicky.Windows/bin/Release/net9.0-windows10.0.22621.0/Clicky.Windows.exe

# Debug modes print to the terminal and exit
Clicky.Windows.exe --capture-test      # JPEGs and report in %TEMP%\clicky-capture-test
Clicky.Windows.exe --overlay-test      # every overlay state, click-through and focus checks
Clicky.Windows.exe --token-test        # token length only
Clicky.Windows.exe --ask "what is on my screen"
```

## Code Style & Conventions

### Variable and Method Naming

IMPORTANT: Follow these naming rules strictly. Clarity is the top priority.

- Be as clear and specific with variable and method names as possible
- **Optimize for clarity over concision.** A developer with zero context on the codebase should immediately understand what a variable or method does just from reading its name
- Use longer names when it improves clarity. Do NOT use single-character variable names
- Example: use `originalQuestionLastAnsweredDate` instead of `originalAnswered`
- When passing props or arguments to functions, keep the same names as the original variable. Do not shorten or abbreviate parameter names. If you have `currentCardData`, pass it as `currentCardData`, not `card` or `cardData`

### Code Clarity

- **Clear is better than clever.** Do not write functionality in fewer lines if it makes the code harder to understand
- Write more lines of code if additional lines improve readability and comprehension
- Make things so clear that someone with zero context would completely understand the variable names, method names, what things do, and why they exist
- When a variable or method name alone cannot fully explain something, add a comment explaining what is happening and why

### Swift/SwiftUI Conventions

- Use SwiftUI for all UI unless a feature is only supported in AppKit (e.g., `NSPanel` for floating windows)
- All UI state updates must be on `@MainActor`
- Use async/await for all asynchronous operations
- Comments should explain "why" not just "what", especially for non-obvious AppKit bridging
- AppKit `NSPanel`/`NSWindow` bridged into SwiftUI via `NSHostingView`
- All buttons must show a pointer cursor on hover
- For any interactive element, explicitly think through its hover behavior (cursor, visual feedback, and whether hover should communicate clickability)

### C# / Windows Conventions (`windows/`)

- The naming and clarity rules above apply unchanged, including no single-letter names for loop variables and lambda parameters.
- Logic that decides behavior goes in `Clicky.Core` with a unit test. `Clicky.Windows` only adapts it to Win32, WinRT and WPF.
- UI state changes happen on the WPF dispatcher. Hook, audio and websocket callbacks marshal with `BeginInvoke`, never `Invoke`.
- Native callbacks (hooks, enumeration callbacks) keep their delegate in a field and always call `CallNextHookEx` for hooks.
- Coordinates are physical pixels everywhere; convert to DIPs only inside the overlay windows.
- Model ids live only in `ClaudeModelIdentifiers.cs`. The prompts in `CompanionSystemPrompts.cs` must stay verbatim with the Swift source (a test enforces this).
- Source files under `windows/` stay ASCII. Characters the Mac strings need, such as the em dash in the prompts and screen labels, are built from their code point at runtime.
- Never log API keys, the AssemblyAI token, or the websocket URL that carries it. Log lengths instead.
- `System.IO` is not an implicit using in the WPF project, and WinForms implicit usings are removed to avoid type-name collisions; import explicitly.
- The only third-party package in the app is NAudio. Add another only with a one-line reason in the PR.

### Do NOT

- Do not add features, refactor code, or make "improvements" beyond what was asked
- Do not add docstrings, comments, or type annotations to code you did not change
- Do not try to fix the known non-blocking warnings (Swift 6 concurrency, deprecated onChange)
- Do not rename the project directory or scheme (the "leanring" typo is intentional/legacy)
- Do not run `xcodebuild` from the terminal — it invalidates TCC permissions

## Git Workflow

- Branch naming: `feature/description` or `fix/description`
- Commit messages: imperative mood, concise, explain the "why" not the "what"
- Do not force-push to main

## Self-Update Instructions

<!-- AI agents: follow these instructions to keep this file accurate. -->

When you make changes to this project that affect the information in this file, update this file to reflect those changes. Specifically:

1. **New files**: Add new source files to the "Key Files" table with their purpose and approximate line count
2. **Deleted files**: Remove entries for files that no longer exist
3. **Architecture changes**: Update the architecture section if you introduce new patterns, frameworks, or significant structural changes
4. **Build changes**: Update build commands if the build process changes
5. **New conventions**: If the user establishes a new coding convention during a session, add it to the appropriate conventions section
6. **Line count drift**: If a file's line count changes significantly (>50 lines), update the approximate count in the Key Files table

Do NOT update this file for minor edits, bug fixes, or changes that don't affect the documented architecture or conventions.
