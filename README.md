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
dotnet run
```

Run unit tests:

```powershell
dotnet test tests/WinFlow.Tests/WinFlow.Tests.csproj
```

Publish a self-contained executable:

```powershell
./scripts/publish.ps1
```

## Configuration

WinFlow runs in the Windows system tray. Open the settings window from the tray menu to configure your API endpoint and credentials.
