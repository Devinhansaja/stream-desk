; Inno Setup script for the StreamDeck Host Windows installer.
; Build:  iscc installer\StreamDeckHost.iss      (after running build-windows.ps1, which fills windows\publish)
#define AppName "StreamDeck Host"
#define AppVersion "1.2.2"
#define AppExe "StreamDeckHost.exe"

[Setup]
AppId={{B7C5E0A2-4D0C-4A3B-9B77-5D3E6F1A2C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=StreamDeck Mobile Controller
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\dist
OutputBaseFilename=StreamDeckHost-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=lowest
MinVersion=10.0
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
SetupIconFile=..\windows\StreamDeckHost\app.ico
CloseApplications=yes

[Tasks]
Name: "autostart"; Description: "Start StreamDeck Host with Windows (runs in the system tray)"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\windows\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "StreamDeckHost"; ValueType: string; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Allow the phone to reach the PC over Wi-Fi (private networks only).
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "kill"
