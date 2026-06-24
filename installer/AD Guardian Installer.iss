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
; restartreplace queues in-use file replacement via MoveFileEx(
; MOVEFILE_DELAY_UNTIL_REBOOT). Without it, AD Guardian self-updates fail
; with "DeleteFile failed; code 5. Access is denied." (clrjit.dll locked
; by running Domain Guardian.exe.) Do NOT remove.
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
  LockedFilesNeedReboot: Boolean;

const
  MOVEFILE_DELAY_UNTIL_REBOOT = $0004;
  LockedRuntimeDlls: string = 'clrjit.dll';

function MoveFileExW(lpExistingFileName, lpNewFileName: String; dwFlags: DWORD): BOOL;
  external 'MoveFileExW@kernel32.dll stdcall';

function QueueLockedFileForRebootRemoval(const LockedFile: string): Boolean;
const
  CleanupPendingRebootDir = '__cleanup_pending_reboot__';
var
  TempRenamedFile: String;
  CleanupDir: String;
  CleanupDestFile: String;
  RenameOk: Boolean;
begin
  // Windows blocks DELETING memory-mapped executables/DLLs (ERROR_ACCESS_DENIED,
  // code 5) but explicitly allows RENAMING them. We exploit that:
  //   1) Pre-create a sentinel cleanup subdir under {app}.
  //   2) Rename the locked file out of the way (.deleteme suffix). This succeeds
  //      even while the file is memory-mapped, because Windows treats rename
  //      differently from delete on open files.
  //   3) Queue MoveFileExW(.deleteme, cleanup-subdir\.deleteme, MOVEFILE_DELAY_UNTIL_REBOOT)
  //      so the old file is moved out of {app}\ on next reboot. We deliberately
  //      use a NON-empty destination. The Win32 docs specify that NULL target
  //      schedules a deferred delete, but Inno Pascal's marshalling of String
  //      to a stdcall parameter passes an empty string as a PWideChar to L"",
  //      NOT as a NULL PWideChar pointer — relying on that path risks the API
  //      silently failing or behaving undefined. A non-empty cleanup path is
  //      safe regardless of marshalling semantics.
  //   4) Inno's [Files] copy then sees an empty destination path and copies the
  //      new file cleanly, with no user-facing "DeleteFile failed; code 5" dialog.
  Result := False;
  CleanupDir := ExpandConstant('{app}\' + CleanupPendingRebootDir);
  ForceDirectories(CleanupDir);
  TempRenamedFile := LockedFile + '.deleteme';
  DeleteFile(TempRenamedFile);
  RenameOk := RenameFile(LockedFile, TempRenamedFile);
  if not RenameOk then
  begin
    Log('WARNING: ' + LockedFile + ' is in use (memory-mapped or ACL-protected) and could not be renamed out of the way.');
    if not WizardSilent then
      MsgBox('AD Guardian setup could not replace ' + LockedFile + ' because another process has it open.' + #13#10 + #13#10 +
             'Please close all AD Guardian windows (including any background instances) and click Retry to continue setup.', mbInformation, MB_OK);
    Exit;
  end;
  CleanupDestFile := AddBackslash(CleanupDir) + ExtractFileName(TempRenamedFile);
  DeleteFile(CleanupDestFile);
  // Once we've renamed the locked file away, the running AD Guardian.exe
  // process is still holding the OLD clrjit.dll memory-mapped in its
  // address space. New process launches will read the freshly copied new
  // file from disk, but the current process will continue using the stale
  // mmapped bytes indefinitely. Signal NeedRestart so Inno prompts for a
  // reboot at the end of install — this is independent of whether
  // MoveFileExW below succeeds at scheduling cleanup of the .deleteme file,
  // since those are orthogonal concerns.
  LockedFilesNeedReboot := True;
  if MoveFileExW(TempRenamedFile, CleanupDestFile, MOVEFILE_DELAY_UNTIL_REBOOT) then
    Log('PreHandleLockedFiles: Renamed ' + LockedFile + ' to ' + TempRenamedFile + ' and queued clean-up at next reboot (target ' + CleanupDestFile + ').')
  else
    Log('WARNING: MoveFileExW could not queue ' + TempRenamedFile + ' for clean-up at next reboot. The .deleteme file will remain in the install directory until manually removed, but AD Guardian has already been updated at ' + LockedFile + '.');
  Result := True;
end;

procedure PreHandleLockedFiles();
var
  DestFile: string;
  CommaPos: Integer;
  FileName: string;
begin
  // clrjit.dll is the canonical .NET runtime DLL that gets memory-mapped by the
  // .NET 9 runtime when AD Guardian is running. While mapped, DeleteFile returns
  // ERROR_ACCESS_DENIED (code 5), not ERROR_SHARING_VIOLATION (code 32). Inno's
  // built-in `restartreplace` flag treats code 5 as a permissions/ACL failure
  // and shows the user-facing "DeleteFile failed; code 5" dialog. We sidestep
  // that by renaming such files out of the way before Inno's [Files] copy step,
  // leaving the destination path free for a clean replace.
  //
  // Called from CurStepChanged(ssInstall) AFTER Restart Manager has had a
  // chance to shut down AD Guardian.exe, but BEFORE Setup runs the [Files] copy.
  // Restart Manager still does its job; this is the safety net for any file
  // that remained mapped because the .NET runtime hadn't fully torn down.
  while Length(LockedRuntimeDlls) > 0 do
  begin
    CommaPos := Pos(',', LockedRuntimeDlls);
    if CommaPos = 0 then
    begin
      FileName := LockedRuntimeDlls;
      LockedRuntimeDlls := '';
    end
    else
    begin
      FileName := Copy(LockedRuntimeDlls, 1, CommaPos - 1);
      LockedRuntimeDlls := Copy(LockedRuntimeDlls, CommaPos + 1, Length(LockedRuntimeDlls));
    end;
    FileName := Trim(FileName);
    if FileName = '' then
      Continue;
    DestFile := ExpandConstant('{app}\' + FileName);
    if not FileExists(DestFile) then
      Continue;
    if DeleteFile(DestFile) then
    begin
      Log('PreHandleLockedFiles: Removed out-of-date ' + DestFile + '.');
      Continue;
    end;
    Log('PreHandleLockedFiles: Could not DeleteFile ' + DestFile + ' (probably memory-mapped). Renaming out of the way.');
    QueueLockedFileForRebootRemoval(DestFile);
  end;
end;

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
    // Pre-empt locked-file rename-and-queue BEFORE Inno's [Files] copy runs.
    // Restart Manager has already tried to close the app; the surviving locks
    // here are typically from in-flight .NET runtime teardown of mmapped
    // files like clrjit.dll. Rename those out of the way and queue removal at
    // next boot via MoveFileEx+MOVEFILE_DELAY_UNTIL_REBOOT.
    PreHandleLockedFiles();
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
