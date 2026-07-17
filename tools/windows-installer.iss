; Windows installer for SQL Explorer (Inno Setup 6).
;
; Per-user install by design: no admin prompt, and it matches where the app already writes — the Plugin
; Store installs into %APPDATA%\Lionear\SqlExplorer\plugins, so an installer needing elevation would be
; the only part of the product that does.
;
; Built by .github/workflows/build.yml, which passes the values that change per run:
;   ISCC.exe tools\windows-installer.iss /DAppVersion=0.1.0-nightly.20260717.42 /DArch=x64 /DSourceDir=... /DOutputDir=...
;
; The .zip stays the primary artifact; this is the convenience path (Start-menu entry + uninstaller).
; Unsigned, so first run shows a SmartScreen warning — a code-signing certificate is the only fix.

#define AppName "SQL Explorer"
#define AppPublisher "Lionear"
#define AppUrl "https://lionear.dev"
#define ExeName "SqlExplorer.Desktop.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
; Stable AppId: upgrades replace the previous install instead of stacking a second copy. Never change it.
AppId={{8F3A6C21-4E7B-4D19-9A2E-6C5B1D0E7F84}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user: installs under %LOCALAPPDATA%\Programs, no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=LionearSqlExplorer-{#AppVersion}-win-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile={#SourceDir}\LICENSE
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
; In-app updater (SE-137): when the running app launches this installer silently to update itself, let
; Restart Manager close the running instance so its files can be replaced. We relaunch it ourselves in
; [Run] (see the silent entry), so Inno's own restart is off.
CloseApplications=yes
RestartApplications=no
; "x64" over the newer "x64compatible": the latter needs Inno Setup 6.3+, and this has to compile on
; whatever 6.x the runner happens to ship. 6.3+ treats x64 as an alias, so both work.
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish tree: single-file exe, the bundled plugins/ folder, LICENSE and
; THIRD-PARTY-NOTICES.md (attribution has to travel with the binaries — SE-127).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent self-update path: no wizard to tick "launch", so relaunch the app automatically once files are in.
Filename: "{app}\{#ExeName}"; Flags: nowait postinstall; Check: WizardSilent

[UninstallDelete]
; Plugins the Store installed live in %APPDATA% and are deliberately left behind on uninstall — the same
; reasoning as connections.json: user data outlives the binaries. Only what we installed goes.
Type: filesandordirs; Name: "{app}\plugins"
