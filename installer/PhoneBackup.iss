; Inno Setup script for Phone Backup
; Builds a per-user installer (no admin / UAC prompt) with Start Menu + optional
; desktop shortcut and a clean entry in Settings > Apps (Add/Remove Programs).

#define MyAppName "OnePlus Phone Backup"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "OnePlus Phone Backup"
#define MyAppExeName "OnePlusPhoneBackup.exe"
#define SrcDir "C:\Users\Sunny\Desktop\claude code\11 - phone-backup"

[Setup]
; A stable, unique ID so upgrades replace the previous version cleanly.
AppId={{D9F4B7C3-6E18-4A92-B5D7-3F8C1E6A9B25}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=C:\Users\Sunny\Desktop\claude code\executable
OutputBaseFilename=11 - phone-backup
SetupIconFile={#SrcDir}\AppIcon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SrcDir}\publish-app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
