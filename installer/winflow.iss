; WinFlow Inno Setup Script
; Generates Windows Setup Wizard for WinFlow

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

#ifndef SourceDir
#define SourceDir "..\dist-single"
#endif

#ifndef OutputDir
#define OutputDir "..\dist-installer"
#endif

#define MyAppName "WinFlow"
#define MyAppPublisher "WinFlow"
#define MyAppExeName "WinFlow.exe"

[Setup]
; Unique GUID for WinFlow
AppId={{9B7F1B68-C5A2-4E28-8B90-E5A8E5C16C91}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; Per-user install without UAC prompt by default; allows elevation if user desires
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=WinFlow-{#AppVersion}-Setup
SetupIconFile=..\Assets\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

; Detect and close running instances during install / upgrade / uninstall
CloseApplications=yes
CloseApplicationsFilter=*.exe

; Uninstall display settings
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Checked by default; the user can opt out. Portable builds never run this script,
; so they only autostart if the user flips the in-app "Start with Windows" toggle.
Name: "autostart"; Description: "Start WinFlow when Windows starts"

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Login autostart, per-user; written only when the task is checked (the default).
; --minimized starts WinFlow in the tray; the in-app toggle writes the same format.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WinFlow"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Tasks: autostart
; Reinstall with the task unchecked removes a previously installed value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "WinFlow"; Tasks: not autostart; Flags: deletevalue
; Uninstall always removes the value, even if the in-app toggle (re)wrote it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "WinFlow"; Flags: uninsdeletevalue

[Run]
; First launch after install shows the window (no --minimized) so the user can set up.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
