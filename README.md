# WinFlow

WinFlow is a native Windows desktop utility for speech-to-text dictation and in-place AI text editing. It captures audio using WASAPI, collects contextual signals from active Windows applications, and integrates with speech and language models for accurate transcription and formatting.

## Key Features

- **Push-to-Talk and Lock Mode**: Use CapsLock to control dictation. Hold down to talk, double-tap to lock recording, or press Shift+CapsLock to toggle standard capitalization.
- **Context-Aware Formatting**: Inspects active window titles and process details to guide transcription accuracy.
- **In-Place Text Editing**: Detects selected text in foreground applications to apply transformations based on spoken instructions.
- **Non-Activating Floating Overlay**: Displays live audio waveform feedback without taking window focus.
- **Secure Key Storage**: Protects sensitive configuration like API keys using Windows DPAPI.
- **Safe Clipboard Handling**: Backs up and restores existing clipboard content after text insertion.

## Keyboard Shortcuts

- **Hold CapsLock**: Record while pressed; release to transcribe and paste.
- **Double-tap CapsLock**: Start continuous hands-free recording.
- **Tap CapsLock (while recording)**: Stop and paste transcription.
- **Shift + CapsLock**: Standard Windows CapsLock toggle.
- **Escape**: Cancel active recording.

## Getting Started

### Prerequisites

- Windows 10 (1809+) or Windows 11
- .NET 10 SDK

### Building and Running

```powershell
./run.ps1
```

This builds once and launches `WinFlow.exe` detached (no console window). `run.cmd` does the same for cmd.exe. `dotnet run` also works for development.

Run unit tests:

```powershell
dotnet test tests/WinFlow.Tests/WinFlow.Tests.csproj
```

Publish a self-contained executable:

```powershell
./scripts/publish.ps1
```

## The app

WinFlow's main window is your voice-writing space:

- **Home** — today's dictations and your lifetime stats (words, wpm, streak), counted on this PC only.
- **History** — recent dictations as a reading timeline; copy any of them again.
- **Snippets** — say a phrase, get the text ("my email" → you@example.com). Instant, no AI, works offline.
- **Words** — your names and jargon; cleanup treats them as the correct spelling.
- **Notes** — record a meeting (microphone + system audio, only while you record), get minutes and a full transcript, saved locally.
- **Settings** (separate dialog) — General, System, AI & models, Privacy, Prompts. Saved changes apply immediately, no restart needed.

WinFlow runs in the Windows system tray. Closing the main window hides it; dictation keeps working. Use **Quit WinFlow** in the tray menu to exit. Only one instance can run at a time; a second launch exits quietly. While a fullscreen game owns the screen, CapsLock passes through untouched (toggle in Settings → System).
