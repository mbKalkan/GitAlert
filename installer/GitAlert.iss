; GitAlert installer
;
; Built by build.ps1, which passes the version and the publish folder:
;
;   ISCC /DAppVersion=1.0.0 /DPublishDir=..\artifacts\publish /DOutputDir=..\artifacts GitAlert.iss
;
; GitAlert is a per-user application: it installs under the user's profile, needs no
; administrator rights, and writes only to HKEY_CURRENT_USER.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "GitAlert"
#define AppPublisher "Mert Berkan Kalkan"
#define AppUrl "https://github.com/mbKalkan/GitAlert"
#define AppExeName "GitAlert.exe"

[Setup]
AppId={{7F1C3A64-2B58-4E0D-9C0A-9B7A5E2D1F84}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

; Per-user install: no UAC prompt, no effect on other accounts on the machine.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes

OutputDir={#OutputDir}
OutputBaseFilename=GitAlert-Setup-{#AppVersion}-x64
SetupIconFile=..\src\GitAlert\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Additional options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Same value name and argument the app itself uses, so the "Start with Windows" switch in
; GitAlert's settings stays in sync with what the installer wrote.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "GitAlert"; \
    ValueData: """{app}\{#AppExeName}"" --startup"; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the tray app before removing its files, so nothing is left locked.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExeName}"; RunOnceId: "StopGitAlert"; Flags: runhidden skipifdoesntexist

[UninstallDelete]
; The user's settings, encrypted token and alert history live outside {app}; leave them in
; place so a reinstall picks up where they left off. Uncomment to remove them instead.
; Type: filesandordirs; Name: "{userappdata}\GitAlert"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
