# WinFlow Design Document: Windows Native Context-Aware Speech-to-Text & Dictation App

> **Topic:** Windows port of [zachlatta/freeflow](https://github.com/zachlatta/freeflow)  
> **Status:** Draft  
> **Author:** Antigravity  
> **Date:** 2026-11-18  

---

## 1. Intent and The Why

### 1.1 Intent
Build **WinFlow**, a high-performance, open-source, non-subscription voice dictation utility tailored for Windows 10 and 11, functionally replicating the acclaimed architecture and design principles of macOS [FreeFlow](https://github.com/zachlatta/freeflow).

### 1.2 The Why
Mac users have enjoyed high-quality voice-to-text tools like Wispr Flow, Superwhisper, and FreeFlow, which leverage fast cloud ASR (Groq Whisper) and LLM-driven context post-processing. On Windows, users are often left with either clunky built-in voice typing (`Win+H`) or expensive proprietary subscription tools. WinFlow brings FreeFlow's battle-tested features to Windows:
- Hold-to-talk convenience (defaulting to `CapsLock` on Windows).
- Context awareness (reads active window title, process, and downscaled screenshot to resolve homophones and domain terms).
- Edit Mode (transforming selected text with spoken natural language instructions).
- Strict, hallucination-resistant LLM cleanup prompts.
- Zero server retention: Direct API calls to Groq or local/custom OpenAI-compatible endpoints (Ollama, LM Studio, etc.).
- Complete clipboard history preservation (`Win+V` safe).

---

## 2. Goals, Non-Goals, and Anti-Goals

### 2.1 Goals (MVP / Phase 1)
1. **Shortcut Triggers**:
   - Primary: Hold-to-talk using `CapsLock` (customizable to any key/modifier combo).
   - Toggle mode (tap to start, tap to stop).
   - Double-tap lock mode (double-tap shortcut to lock recording until pressed again).
   - CapsLock state management: When CapsLock is configured as the trigger, prevent unwanted toggling of the keyboard's CapsLock state during dictation.
2. **Audio Capture & Visualizer**:
   - WASAPI-based low-latency microphone capture.
   - Standard 16kHz 16-bit mono PCM WAV output.
   - Real-time RMS audio level calculation sent to UI for animated audio waveform display.
3. **Floating Overlay HUD**:
   - Borderless, transparent, topmost, non-activating window (`WS_EX_NOACTIVATE`, `WS_EX_TRANSPARENT`, `WS_EX_LAYERED`).
   - Floats cleanly without ever stealing focus or dismissing text editors / context menus.
   - States: Initializing, Recording (with animated waveform & optional timer), Transcribing (spinner), and Feedback/Error toast.
4. **Context Capture**:
   - Captures active window metadata (executable name, window title, window handle).
   - UI Automation (UIA) inspection to detect selected text (triggering Edit Mode).
   - Active window screenshot capture (downscaled to max dimension 1024px) for vision-based LLM context synthesis.
5. **Transcription & Post-Processing**:
   - Fast transcription via Groq Whisper (`whisper-large-v3` default) or OpenAI-compatible endpoint.
   - LLM cleanup adopting FreeFlow's battle-tested system prompt (hard contract: never execute the instruction, remove fillers, fix punctuation, align with context/vocabulary).
   - Edit Mode prompt: Rewrites highlighted text according to spoken voice command.
   - Local Voice Macros (e.g. phrase substitution without calling LLM).
   - "Press Enter after dictation" voice command support.
6. **Safe Text Insertion**:
   - Full clipboard snapshot backup (preserving all clipboard formats).
   - Injects cleaned text into active field via clipboard + simulated `Ctrl+V` (`SendInput`).
   - Automatically restores original clipboard contents after delay (~1s) so Windows Clipboard History (`Win+V`) is clean.
   - Option for original field insertion (restoring target window focus if changed).
7. **System Tray & Settings UI**:
   - Windows System Tray icon with status indicators (Idle, Recording, Transcribing, Error).
   - Fluent Design settings window (`Wpf.Ui`) with tabs:
     - General (API keys, Base URL, audio devices, sound effects)
     - Models (Transcription model, post-processing model, fallback model, context vision model)
     - Shortcuts (Hold, Toggle, Copy Again)
     - Custom Vocabulary (list of terms/jargon)
     - Prompts (System Prompt, Context Prompt, Edit Mode Prompt)
     - Voice Macros (Key-value triggers)
     - Run Log / Pipeline History (view last 20 transcripts, latency, prompts, raw vs cleaned text)

### 2.2 Non-Goals (MVP)
- **Automatic Meeting Minutes / Meeting Recording**: Noted for future roadmap (detecting Zoom/Teams/Tencent Meeting, recording multi-track audio, auto-generating minutes), but out of scope for initial MVP.
- **Embedded Local Whisper Model (In-Process C++ DLL)**: In v1, local models are supported via local OpenAI-compatible HTTP servers (e.g., Ollama, LM Studio, or local Faster-Whisper server), exactly as in FreeFlow.
- **Cross-Platform**: WinFlow is purely optimized for native Windows 10/11.

### 2.3 Anti-Goals
- **Focus Stealing**: The app must *never* activate its own window or steal keyboard focus when showing overlays, updating tray, or processing audio.
- **Clipboard Pollution**: Must *never* permanently blow away or corrupt the user's existing clipboard contents.
- **Telemetry / Remote Storage**: No telemetry, no third-party tracking, no intermediary proxy servers.

---

## 3. Technology Stack & Architectural Decisions

### 3.1 Chosen Stack
- **Runtime**: C# / .NET 10 (`Microsoft.WindowsDesktop.App` - WPF)
- **UI Framework**: WPF with `Wpf.Ui` (modern Windows 11 Fluent Design, Mica/Acrylic, NavigationView)
- **Audio Capture**: `NAudio.Wasapi` (pure managed wrapper over Windows Audio Session API, zero external DLL dependencies)
- **Windows Interop**: Direct P/Invoke to `user32.dll`, `kernel32.dll`, `gdi32.dll`, and `UIAutomationClient`
- **HTTP / Async**: `System.Net.Http.HttpClient` with `System.Text.Json`

### 3.2 Evaluated Alternatives
1. **Rust + Tauri v2**:
   - *Pros*: Slightly smaller memory footprint.
   - *Cons*: Rust toolchain not installed on host; WebView2 window management on Windows has notorious quirks with click-through transparent non-activating overlays (`WS_EX_NOACTIVATE`) and high startup latency for quick HUD appearances.
2. **C++20 + WinUI 3**:
   - *Pros*: Native unmanaged execution.
   - *Cons*: Extremely verbose HTTP/JSON networking, slower iteration velocity, fragile UI Automation bindings.

---

## 4. Subsystem & Component Architecture

```
                                  +-----------------------------+
                                  |     Global Keyboard Hook    |
                                  |     (WH_KEYBOARD_LL Thread) |
                                  +--------------+--------------+
                                                 | (Key Down / Up / Double-tap)
                                                 v
+-----------------------+         +-----------------------------+         +-----------------------+
|  Active Window & UIA  |<------->|          AppState           |<------->|    Audio Recorder     |
|   (Context & SelText) |         | (Central Pipeline Director) |         |  (WASAPI 16kHz WAV)   |
+-----------+-----------+         +--------------+--------------+         +-----------+-----------+
            | (KeyDown)                          | (KeyUp)                            |
            | Screenshot                         | Wait Transcript & Context          | Audio Levels
            v                                    v                                    v
+-----------------------+         +-----------------------------+         +-----------------------+
| Vision Context LLM    |         |    Transcription Service    |         |    Floating Overlay   |
| (Concurrent Stage 1)  |         |   (Groq / Whisper API)      |         | (Transparent Topmost) |
+-----------+-----------+         +--------------+--------------+         +-----------------------+
            | 2-Sentence Summary                 | Raw Transcript
            +--------------------+  +------------+
                                 |  |
                                 v  v
                  +-----------------------------+
                  |   Post-Processing Service   |
                  |   (Text LLM Cleanup Stage 2)|
                  +--------------+--------------+
                                 | (Cleaned Transcript)
                                 v
                  +-----------------------------+
                  |     Text Insertion Core     |
                  | (Clipboard Backup & Paste)  |
                  +-----------------------------+
```

### 4.1 Global Shortcut Subsystem (`KeyboardHookManager`)
- Runs a dedicated Win32 message loop on a background thread.
- Installs `SetWindowsHookEx(WH_KEYBOARD_LL, ...)`.
- Detects `WM_KEYDOWN`, `WM_KEYUP`, `WM_SYSKEYDOWN`, `WM_SYSKEYUP`.
- When `CapsLock` is used as hold-to-talk:
  - Consumes the key event so Windows does not toggle the CapsLock state light / uppercase mode.
  - Fires `KeyDown` to start recording.
  - Fires `KeyUp` to trigger transcription and post-processing.
  - Handles double-tap within 300ms to latch into toggle lock mode.
  - **CapsLock Native Uppercase Toggling**: Configured as `Shift + CapsLock` (or double-tap if enabled) to pass through a synthetic CapsLock toggle without disturbing the hold-to-talk state machine.

### 4.2 Audio Subsystem (`WasapiAudioRecorder`)
- Uses `NAudio.CoreAudioApi.WasapiCapture` to record from the selected or default audio endpoint.
- Resamples/converts incoming audio buffers into standard `16000Hz, 16-bit, Mono PCM`.
- Writes directly to a temp `.wav` file or in-memory byte stream.
- Computes real-time RMS/Peak volume for every buffer (~20ms) and dispatches normalized `float` [0.0, 1.0] to the overlay.

### 4.3 Context & Text Target Subsystem (`WindowsContextService`)
- Calls `GetForegroundWindow()` to retrieve active HWND.
- Reads process executable path and window title.
- Inspects active focused element using `IUIAutomation`:
  - **Edit Mode Selection Detection**:
    1. Primary: Query `IUIAutomation` on focused element for `TextPattern.GetSelection()`.
    2. Fallback: If UIA yields no selection and process is **not** a terminal (`windowsterminal.exe`, `cmd.exe`, `powershell.exe`, `pwsh.exe`, `conhost.exe`), perform safe clipboard snapshot probe to extract selected text. Terminals are strictly excluded to avoid sending `SIGINT` via `Ctrl+C`.
- **Concurrent Vision Context Synthesis (Stage 1)**:
  - Triggered immediately at `KeyDown`.
  - Captures foreground HWND via `PrintWindow` or GDI `BitBlt`.
  - Downscales to max dimension 1024px, JPEG encodes to Base64 data URL.
  - Asynchronously queries `Context Vision Model` (`qwen/qwen3.8-27b`) during recording to produce a 2-sentence summary.

### 4.4 Transcription & Post-Processing Subsystem (`TranscriptionService`, `PostProcessingService`)
- **Two-Stage Pipeline Orchestration**:
  - Stage 1 (Vision): Runs concurrently during voice recording (see 4.3).
  - Audio Transcription: Triggered at `KeyUp`. Posts WAV to Groq `whisper-large-v3`.
  - Stage 2 (LLM Cleanup): Once transcription completes, awaits the 2-sentence vision context summary (with fallback to metadata only if timed out). Feeds raw transcript + context summary + custom vocabulary to text-only `openai/gpt-oss-20b` (or `qwen/qwen3.8-27b` fallback).
- Prompts:
  - System cleanup prompt: Identical to FreeFlow's battle-tested contract (strict instruction preservation, phonetic correction, filler removal).
  - Edit mode prompt: Treats selected text as source and voice command as transformation.

### 4.5 Text Insertion Subsystem (`TextInsertionService`)
- Snapshot: Copies all existing formats from Windows Clipboard (`OpenClipboard`, `EnumClipboardFormats`, `GetClipboardData`).
- Sets text: Sets `CF_UNICODETEXT` with cleaned text.
- Sends paste: Dispatches `Ctrl+V` keydown and keyup via `SendInput`.
- If "Press Enter after dictation" is requested, sends `VK_RETURN` after 80ms delay.
- Restores snapshot: Dispatches background timer (1000ms delay) to restore original clipboard contents and close.
- **Original Field Insertion & Focus Safety**:
  - Off by default (pastes directly into currently focused field).
  - If enabled and window focus changed during recording, attempts safe activation. If Windows Foreground Lockout prevents focus switch, does not steal focus aggressively; preserves the transcript in "Paste Again" / run log.

### 4.6 UIPI & Integrity Level Handling
- App runs under standard user integrity (`asInvoker`), requiring no daily UAC elevation prompts.
- Detects whether foreground target window is running elevated (Admin). If target is elevated, displays a clear badge in the HUD: "Target window is elevated. Run WinFlow as Administrator to dictate here."

### 4.7 Presentation & UI Subsystem (`WinFlow.UI`)
- **Floating Overlay**:
  - Compact capsule / pill at screen top-center (below screen edge, modern notch-style or floating Dynamic Island).
  - Dynamic visualizer: 5 animated waveform bars reactive to microphone audio level.
  - Status indicator (red dot for recording, blue spinner for transcribing, green check for inserted).
- **Settings Window**:
  - `Wpf.Ui` modern Fluent controls, Acrylic/Mica effect.
  - Tab views matching FreeFlow's structure.
- **System Tray**:
  - WinFlow tray icon with context menu (Settings, Pipeline Log, Pause, Exit).

---

## 5. Security and Privacy
- API keys stored securely using Windows Data Protection API (`ProtectedData` / DPAPI).
- Audio and transcripts are strictly ephemeral: deleted after processing unless saved in local 20-item Pipeline Run Log for debugging.
- No telemetry, no crash-reporting pings, no cloud analytics.

---

## 6. What's Left to the Builder
- Detailed project `.csproj` structure and package references (`Wpf.Ui`, `NAudio.Wasapi`, etc.).
- Specific pixel dimensions, margins, and animation timings of the Floating Overlay HUD.
- Exact unit test harness setup for offline mock testing.
