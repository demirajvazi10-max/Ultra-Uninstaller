; UltraUninstaller installer script (Inno Setup).
;
; Expects a self-contained, single-file publish of UltraUninstaller.App to
; already exist at ..\publish (produced by the GitHub Actions release workflow
; via `dotnet publish ... -o publish`). AppVersion is normally passed in from
; the workflow with /DAppVersion=1.2.3 (from the pushed tag, e.g. v1.2.3); the
; default below is only used for local test builds.
;
; Local build: install Inno Setup (https://jrsoftware.org/isinfo.php), then
; from the repo root run:
;   dotnet publish src\UltraUninstaller.App\UltraUninstaller.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\UltraUninstaller.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "UltraUninstaller"
#define AppPublisher "Ultra Creative Suite"
#define AppURL "https://github.com/demirajvazi10-max/UltraUninstaller"
#define AppExeName "UltraUninstaller.exe"

[Setup]
; Fixed GUID - never regenerate this, it's what lets a newer version's installer
; recognize and upgrade an existing install instead of creating a duplicate entry.
AppId={{4B1A8F0A-6E9C-4B0E-8B7A-9E7C6D2F5A1E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; The app itself only requests "highestAvailable" elevation (so it can still
; open normally without a UAC prompt), but installing into Program Files and
; registering Start Menu entries for all users does need admin at setup time.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=UltraUninstaller-Setup-{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Everything the self-contained publish produced (the exe plus its native
; dependencies) - not just the one exe, since PublishSingleFile still needs a
; few native runtime files alongside it.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
