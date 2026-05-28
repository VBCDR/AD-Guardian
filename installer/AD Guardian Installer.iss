#define MyAppName "AD Guardian"
#define MyAppPublisher "Cian Rogers"
#define MyAppExeName "Domain Guardian.exe"
#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif
#ifndef SourcePayloadDir
  #define SourcePayloadDir "..\\AD-Guardian\\artifacts\\distributions\\portable\\win-x64\\app"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir ".\\Release"
#endif

[Setup]
AppId={{D6C23564-E1AE-4E90-A6E5-6A21C44DF6A4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/CianRogers/AD-Guardian
AppSupportURL=https://github.com/CianRogers/AD-Guardian
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoTextVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Installer
DefaultDirName={autopf}\AD Guardian
DefaultGroupName=AD Guardian
DisableProgramGroupPage=yes
WizardStyle=modern
WizardImageFile=wizard-image.png
WizardSmallImageFile=wizard-small.png
WizardImageStretch=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=..\AD-Guardian\AD-Guardian-logo-_2_.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#InstallerOutputDir}
OutputBaseFilename=AD Guardian Installer
ChangesAssociations=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
WizardResizable=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Dirs]
Name: "{userappdata}\AdHealthMonitor"
Name: "{userappdata}\AdHealthMonitor\LegacyStateBackup"
Name: "{sd}\ADCheckLogs"
Name: "{sd}\ADCheckLogs\runs"

[Files]
Source: "{#SourcePayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AD Guardian"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AD Guardian logo.ico"
Name: "{autodesktop}\AD Guardian"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AD Guardian logo.ico"; Tasks: desktopicon

[CustomMessages]
LaunchAfterInstall=Launch AD Guardian now
UninstallRemoveLogs=Remove stored diagnostic logs from C:\ADCheckLogs as well?
UninstallRemoveState=Remove saved application state from %AppData%\AdHealthMonitor as well?
InitializeStateStatus=Preparing application folders and database...

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "-initialize-state"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchAfterInstall}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WizardForm.StatusLabel.Caption := ExpandConstant('{cm:InitializeStateStatus}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then
    exit;

  if DirExists(ExpandConstant('{sd}\ADCheckLogs')) then
  begin
    if MsgBox(ExpandConstant('{cm:UninstallRemoveLogs}'), mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{sd}\ADCheckLogs'), True, True, True);
  end;

  if DirExists(ExpandConstant('{userappdata}\AdHealthMonitor')) then
  begin
    if MsgBox(ExpandConstant('{cm:UninstallRemoveState}'), mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{userappdata}\AdHealthMonitor'), True, True, True);
  end;
end;
