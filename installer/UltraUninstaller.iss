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
  #define AppVersion "1.1.0"
#endif

#define AppName "UltraUninstaller"
#define AppPublisher "Ultra Creative Suite"
#define AppURL "https://github.com/demirajvazi10-max/Ultra-Uninstaller"
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
; Install mode: "for all users" (Program Files, needs admin) is the default,
; but Setup first shows an accessible choice dialog that also offers "only for
; me" (installs into the user's own Programs folder, no administrator rights or
; UAC prompt needed). Without this, a cancelled or unseen UAC prompt - or an
; account without admin rights - made Setup simply close with no message at all,
; which is exactly what one tester reported. /ALLUSERS or /CURRENTUSER on the
; command line also picks the mode silently.
; {autopf}, {group} and {autodesktop} below follow whichever mode is chosen.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
; Always write a log to %TEMP% ("Setup Log yyyy-mm-dd #nnn.txt"), so if Setup
; ever fails on someone's machine they can just send that file.
SetupLogging=yes
; Only ask for the Setup language when Windows' own language isn't English or
; Serbian - otherwise pick the matching one automatically (one dialog fewer).
ShowLanguageDialog=auto
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
; Serbian (Latin script). Inno Setup only ships this as an unofficial translation,
; so a copy lives in this repo (installer\Languages) instead of relying on the
; compiler's own folder.
Name: "serbian"; MessagesFile: "Languages\SerbianLatin.isl"

[Tasks]
; {cm:...} texts come from the language file, so they appear in English or Serbian.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything the self-contained publish produced (the exe plus its native
; dependencies) - not just the one exe, since PublishSingleFile still needs a
; few native runtime files alongside it.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
