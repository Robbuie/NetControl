; Inno Setup script for NetControl.
;
; Per-user install (%LOCALAPPDATA%\Programs\NetControl), no administrator rights, no driver, no
; runtime to install first. That is the same hard requirement the portable exe was built around:
; anything needing admin is a conversation with plant IT before anybody can do any work.
;
; The portable single exe has not gone away and is published beside this installer on every
; release. The installer is for the laptop somebody uses every week and wants a Start menu entry,
; a stable path for the firewall rule, and an entry in Add/Remove Programs; the loose exe is for
; the machine somebody was handed that morning. See DEPLOY.md.
;
; Build:  ISCC.exe /DAppVersion=0.5.0 /DSourceDir=..\artifacts\NetControl-0.5.0 installer\NetControl.iss
;
; tools/publish.ps1 -Installer does this for you and passes both values.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\NetControl-" + AppVersion
#endif

#define AppName    "NetControl"
#define AppExeName "NetControl.exe"
#define AppPublisher "Robbuie"

[Setup]
; Keep this GUID forever - it is how Windows and any future update recognise an existing install
; and upgrade it in place instead of leaving two NetControls in Add/Remove Programs.
AppId={{B2934E15-79BE-411A-9305-543B01455706}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL=https://github.com/Robbuie/netcontrol
AppUpdatesURL=https://github.com/Robbuie/netcontrol/releases/latest
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; Lowest, and the default. With PrivilegesRequired=lowest, {autopf} resolves to
; %LOCALAPPDATA%\Programs - no UAC prompt, and nothing written outside the user's own profile.
; The override is left available for a site that genuinely wants one copy per machine, which is a
; decision for whoever manages the laptop rather than something to assume.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist_installer
OutputBaseFilename=NetControl-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\NetControl.App\Assets\NetControl.ico
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; So reinstalling over a running copy shuts it down cleanly rather than failing on a locked file.
; NetControl holds UDP/67 while it is listening, and a half-replaced install that still owns the
; port is a worse state than either.
CloseApplications=yes
RestartApplications=yes
SetupLogging=yes

UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; One file. NetControl publishes self-contained and single-file, so there is no runtime to lay
; down beside it and nothing here to keep in step with a .NET version on the machine.
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The rolling diagnostic log, which is regenerable and deleted on a schedule anyway.
;
; settings.json is deliberately NOT removed and must stay that way. It is site configuration -
; most importantly the switch that stops this tool making any outbound request - and an uninstall
; that quietly discarded that decision would turn a reinstall into a machine phoning out again
; without anybody choosing it. Project files are never under here at all; they are wherever the
; user saved them.
Type: filesandordirs; Name: "{localappdata}\NetControl\logs"
