#define MyAppName "AD Guardian"
#define MyAppExeName "Domain Guardian.exe"
#define MyAppPublisher "AD Guardian"
#define MyAppURL "https://github.com/VBCDR/AD-Guardian"

[Setup]
AppId=ADGuardian
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={code:GetDefaultInstallDir}
DefaultGroupName=AD Guardian
AllowNoIcons=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=AD Guardian Installer
SetupIconFile={#SourcePayloadDir}\AD Guardian logo.ico
WizardStyle=modern
WizardImageFile=wizard-image.png
WizardSmallImageFile=wizard-small.png
WizardImageStretch=no
WizardImageBackColor=clWhite
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
DisableWelcomePage=no
DisableDirPage=no
DisableReadyMemo=no
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\AdHealthMonitor"
Name: "{commonappdata}\AdHealthMonitor\runs"
Name: "C:\ADCheckLogs"; Permissions: users-modify
Name: "C:\ADCheckLogs\runs"; Permissions: users-modify

[Files]
Source: "{#SourcePayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code] 
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\ADGuardian_is1';

var
  ExistingInstallDetected: Boolean;
  UpdateMode: Boolean;
  ClosingForExternalUninstall: Boolean;
  ExistingInstallPath: string;
  ExistingInstallUninstallString: string;
  ExistingInstallPage: TWizardPage;
  RepairRadio: TNewRadioButton;
  UninstallRadio: TNewRadioButton;
  ExistingInstallCaption: TNewStaticText;

function GetRoamingAppDataStatePath(): string;
var
  AppDataPath: string;
begin
  AppDataPath := GetEnv('APPDATA');
  if AppDataPath = '' then
    Result := ''
  else
    Result := AddBackslash(AppDataPath) + 'AdHealthMonitor';
end;

procedure SetInstallStatus(const Message: string);
begin
  if WizardForm <> nil then
    WizardForm.StatusLabel.Caption := Message;
end;

function GetDefaultInstallDir(Param: string): string;
begin
  if ExistingInstallDetected and (ExistingInstallPath <> '') then
    Result := ExistingInstallPath
  else
    Result := ExpandConstant('{autopf}\AD Guardian');
end;

procedure SplitCommandLine(const CommandLine: string; var FileName, Params: string);
var
  I: Integer;
begin
  FileName := '';
  Params := '';

  if CommandLine = '' then
    Exit;

  if CommandLine[1] = '"' then
  begin
    I := 2;
    while (I <= Length(CommandLine)) and (CommandLine[I] <> '"') do
      Inc(I);

    FileName := Copy(CommandLine, 2, I - 2);
    Params := Trim(Copy(CommandLine, I + 1, Length(CommandLine)));
  end
  else
  begin
    I := 1;
    while (I <= Length(CommandLine)) and (CommandLine[I] <> ' ') do
      Inc(I);

    FileName := Copy(CommandLine, 1, I - 1);
    Params := Trim(Copy(CommandLine, I + 1, Length(CommandLine)));
  end;
end;

function TryReadExistingInstall(var InstallPath: string; var UninstallString: string): Boolean;
var
  Value: string;
  ExePath: string;
  Params: string;
begin
  Result := False;
  InstallPath := '';
  UninstallString := '';

  if RegQueryStringValue(HKLM64, UninstallKey, 'UninstallString', Value) or
     RegQueryStringValue(HKLM32, UninstallKey, 'UninstallString', Value) or
     RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Value) then
  begin
    UninstallString := Value;
    SplitCommandLine(Value, ExePath, Params);
    if ExePath <> '' then
      InstallPath := ExtractFileDir(ExePath);
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', Value) or
     RegQueryStringValue(HKLM32, UninstallKey, 'InstallLocation', Value) or
     RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Value) then
  begin
    InstallPath := Value;
    Result := True;
  end;
end;

function InitializeSetup(): Boolean;
begin
  UpdateMode := Pos('/UPDATE', UpperCase(GetCmdTail)) > 0;
  ClosingForExternalUninstall := False;
  ExistingInstallDetected := TryReadExistingInstall(ExistingInstallPath, ExistingInstallUninstallString);
  Result := True;
end;

procedure LaunchUninstallerAndExit;
var
  UninstallExe: string;
  UninstallParams: string;
  ResultCode: Integer;
begin
  SplitCommandLine(ExistingInstallUninstallString, UninstallExe, UninstallParams);
  if UninstallExe = '' then
  begin
    MsgBox('The existing installation was detected, but its uninstall command could not be read.', mbError, MB_OK);
    Exit;
  end;

  if not Exec(UninstallExe, UninstallParams, '', SW_SHOW, ewNoWait, ResultCode) then
  begin
    MsgBox('Unable to start the existing uninstaller.', mbError, MB_OK);
    Exit;
  end;

  ClosingForExternalUninstall := True;
  WizardForm.Close;
end;

procedure InitializeWizard();
begin
  if WizardSilent or UpdateMode then
    Exit;

  if not ExistingInstallDetected then
    Exit;

  ExistingInstallPage := CreateCustomPage(wpWelcome, 'Existing Installation Detected', 'Choose how to proceed with the current AD Guardian installation.');

  ExistingInstallCaption := TNewStaticText.Create(ExistingInstallPage.Surface);
  ExistingInstallCaption.Parent := ExistingInstallPage.Surface;
  ExistingInstallCaption.Left := ScaleX(0);
  ExistingInstallCaption.Top := ScaleY(0);
  ExistingInstallCaption.Width := ExistingInstallPage.SurfaceWidth;
  ExistingInstallCaption.Caption := 'An existing installation was detected at:';

  with TNewStaticText.Create(ExistingInstallPage.Surface) do
  begin
    Parent := ExistingInstallPage.Surface;
    Left := ScaleX(0);
    Top := ScaleY(18);
    Width := ExistingInstallPage.SurfaceWidth;
    Caption := ExistingInstallPath;
  end;

  RepairRadio := TNewRadioButton.Create(ExistingInstallPage.Surface);
  RepairRadio.Parent := ExistingInstallPage.Surface;
  RepairRadio.Left := ScaleX(0);
  RepairRadio.Top := ScaleY(52);
  RepairRadio.Width := ExistingInstallPage.SurfaceWidth;
  RepairRadio.Caption := 'Repair / reinstall the current installation';
  RepairRadio.Checked := True;

  UninstallRadio := TNewRadioButton.Create(ExistingInstallPage.Surface);
  UninstallRadio.Parent := ExistingInstallPage.Surface;
  UninstallRadio.Left := ScaleX(0);
  UninstallRadio.Top := ScaleY(76);
  UninstallRadio.Width := ExistingInstallPage.SurfaceWidth;
  UninstallRadio.Caption := 'Uninstall the existing installation and exit';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (ExistingInstallPage <> nil) and (CurPageID = ExistingInstallPage.ID) then
  begin
    if UninstallRadio.Checked then
    begin
      Result := False;
      LaunchUninstallerAndExit;
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  if UpdateMode or WizardSilent then
    Exit;

  if ExistingInstallDetected and (PageID = wpWelcome) then
    Result := True;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if ClosingForExternalUninstall then
  begin
    Confirm := False;
  end;
end;

    procedure CurStepChanged(CurStep: TSetupStep);
var
  StateDir: string;
begin
  if CurStep = ssInstall then
  begin
    SetInstallStatus('Creating application folders and log paths...');
    StateDir := GetRoamingAppDataStatePath();
    if StateDir <> '' then
    begin
      ForceDirectories(StateDir);
      ForceDirectories(AddBackslash(StateDir) + 'runs');
    end;
    ForceDirectories('C:\ADCheckLogs');
    ForceDirectories('C:\ADCheckLogs\runs');

    SetInstallStatus('Preparing application state directories...');
        if StateDir <> '' then
        begin
          { AppState.db is created by the application on first launch via AppStateStore.Initialize(). }
        end;
  end
  else if CurStep = ssPostInstall then
  begin
    SetInstallStatus('Finalising shortcuts, uninstall registration, and launch options...');
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if CurProgress > 0 then
    SetInstallStatus('Installing application files...');
end;
