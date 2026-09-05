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
; dialog lets a user choose "for everyone" during a normal install; commandline lets a
; scripted upgrade pass /ALLUSERS or /CURRENTUSER so it lands where the previous one did.
PrivilegesRequiredOverridesAllowed=dialog commandline
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
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Additional options:"; Check: IsFirstInstall
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[InstallDelete]
; The Avalonia build replaced the WPF one and ships a different runtime payload; an upgrade would
; otherwise leave the previous build's libraries behind. Everything needed is laid down again below.
; The uninstaller and its log (unins000.exe, unins000.dat) are left alone on purpose.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"

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
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7F1C3A64-2B58-4E0D-9C0A-9B7A5E2D1F84}_is1';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'GitAlert';

{ True only when GitAlert is not already installed. The "start with Windows" task is offered on
  a first install and never again: from then on the switch inside GitAlert's settings owns that
  preference, and an upgrade must not quietly turn it back on. }
function IsFirstInstall(): Boolean;
begin
  Result := not RegKeyExists(HKEY_CURRENT_USER, UninstallKey)
        and not RegKeyExists(HKEY_LOCAL_MACHINE, UninstallKey);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing: String;
begin
  { On an upgrade, leave the user's choice alone but repoint it at the new location, in case
    they chose a different folder this time. }
  if (CurStep = ssPostInstall) and not IsFirstInstall() then
    if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, RunValueName, Existing) then
      RegWriteStringValue(HKEY_CURRENT_USER, RunKey, RunValueName,
        '"' + ExpandConstant('{app}\{#AppExeName}') + '" --startup');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { uninsdeletevalue only covers a value this installer wrote. GitAlert writes the very same
    value when you enable startup from its own settings, so remove it unconditionally -
    otherwise uninstalling leaves Windows trying to launch an executable that is gone. }
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, RunValueName);
end;
