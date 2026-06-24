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
; Verbose setup log -> %TEMP%\Setup Log YYYY-MM-DD #NNN.txt. Critical for
; diagnosing "DeleteFile failed; code 5" / "access denied" failures because
; the Inno error dialog rarely pinpoints which file/dir is locked. The user
; can open the log and grep for "PreHandleLockedFiles" to see exactly which
; *.dll/*.exe in {app} was renamed out of the way (or which rename failed).
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Health logs live under CommonApplicationData (C:\ProgramData) so:
;   1. AV / Defender Controlled Folder Access doesn't lock the path the way
;      they lock arbitrary user-created roots like C:\ADCheckLogs.
;   2. UAC doesn't virtualise the path for non-admin installers.
;   3. DefensiveMountPoint + Windows Modules Installer compatibility is
;      preserved because the location matches the OS-managed layout.
; AppState.db stays under %AppData%\AdHealthMonitor (per-user, see
; AppStateStore.cs); the installer doesn't touch that directory.
Name: "{commonappdata}\AdHealthMonitor\Logs"
Name: "{commonappdata}\AdHealthMonitor\Logs\runs"

[Files]
; restartreplace queues in-use file replacement via MoveFileEx(
; MOVEFILE_DELAY_UNTIL_REBOOT). Without it, AD Guardian self-updates fail
; for *any* locked executable/DLL (not just clrjit.dll) with
; "DeleteFile failed; code 5. Access is denied.". PreHandleLockedFiles in
; the [Code] section below renames every locked *.dll/*.exe out of the way
; BEFORE [Files] runs, queuing cleanup at next reboot via __cleanup_pending_reboot__.
; Do NOT remove either flag.
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
  CleanupPendingRebootDir = '__cleanup_pending_reboot__';
  // Subdirectories under {app} that should not be recursively scanned for
  // locked files: the cleanup_pending_reboot sink is created on demand by
  // QueueLockedFileForRebootRemoval and would otherwise be scanned in
  // recursively (harmless but wasteful).
  SkipDirsForLockScan = '__cleanup_pending_reboot__';
  // Maximum number of directory LEVELS scanned by ScanDirectoryForLockedFiles.
  // The AD Guardian payload nests at most ~3 levels deep (runtimes\win-x64\native),
  // so 16 is a generous safety bound. Bounded levels prevents a malicious or
  // accidentally-created NTFS junction / symlink (e.g. {app}\foo -> C:\) from
  // causing the installer to cascade through the entire system root.
  MaxScanLevels = 16;
  // NTFS reparse-point attribute. Entries with this attribute are junction
  // points, symlinks, or OneDrive placeholder folders — we DO NOT recurse
  // INTO them (to avoid depth-cap bypass) AND we DO NOT touch symlinked files
  // (to avoid the rename-on-failure path following the link to caller-controlled
  // destinations outside {app}). This keeps the scan strictly scoped to actual
  // install payload files.
  FILE_ATTRIBUTE_REPARSE_POINT = $00000400;

function IsProbablyLockedDllOrExe(const FileName: string): Boolean;
var
  Lower: string;
begin
  // Wildcard match on the *suffix* (.dll / .exe) is enough — Inno's [Files]
  // section above copies every file in the payload regardless of extension,
  // but only executables and DLLs are memory-mapped by the running .NET
  // runtime or held by AV scanners / Smart App Control. Config files,
  // licences, JSON, and INI files are not memory-mapped and DeleteFile on
  // them almost always succeeds, so we leave them alone to keep the
  // expensive rename-and-queue path narrowly targeted.
  Lower := LowerCase(FileName);
  Result := (Pos('.dll', Lower) > 0) or (Pos('.exe', Lower) > 0);
end;

function MoveFileExW(lpExistingFileName, lpNewFileName: String; dwFlags: DWORD): BOOL;
  external 'MoveFileExW@kernel32.dll stdcall';

function QueueLockedFileForRebootRemoval(const LockedFile: string): Boolean;
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
  //   4) Inno's [Files] copy then sees a renamed-away file path and copies the
  //      new file cleanly, with no user-facing "DeleteFile failed; code 5" dialog.
  Result := False;
  CleanupDir := ExpandConstant('{app}\') + CleanupPendingRebootDir;
  ForceDirectories(CleanupDir);
  TempRenamedFile := LockedFile + '.deleteme';
  DeleteFile(TempRenamedFile);
  RenameOk := RenameFile(LockedFile, TempRenamedFile);
  if not RenameOk then
  begin
    Log('PreHandleLockedFiles: WARNING ' + LockedFile + ' is in use (memory-mapped, AV-locked, or ACL-protected) and could not be renamed out of the way. The copy step that follows WILL fail for this file unless the user closes AD Guardian.');
    if not WizardSilent then
      MsgBox('AD Guardian setup could not replace ' + LockedFile + ' because another process has it open.' + #13#10 + #13#10 +
             'Please close all AD Guardian windows (including any background instances) and click Retry to continue setup.', mbInformation, MB_OK);
    Exit;
  end;
  CleanupDestFile := AddBackslash(CleanupDir) + ExtractFileName(TempRenamedFile);
  DeleteFile(CleanupDestFile);
  // Once we've renamed the locked file away, the running AD Guardian.exe
  // process is still holding the OLD memory-mapped bytes in its address space.
  // New process launches will read the freshly copied new file from disk, but
  // the current process will continue using the stale mmapped bytes indefinitely.
  // Signal NeedRestart so Inno prompts for a reboot at the end of install — this
  // is independent of whether MoveFileExW below succeeds at scheduling cleanup
  // of the .deleteme file, since those are orthogonal concerns.
  LockedFilesNeedReboot := True;
  if MoveFileExW(TempRenamedFile, CleanupDestFile, MOVEFILE_DELAY_UNTIL_REBOOT) then
    Log('PreHandleLockedFiles: Renamed ' + LockedFile + ' to ' + TempRenamedFile + ' and queued clean-up at next reboot (target ' + CleanupDestFile + ').')
  else
    Log('PreHandleLockedFiles: WARNING MoveFileExW could not queue ' + TempRenamedFile + ' for clean-up at next reboot. The .deleteme file will remain in the install directory until manually removed, but AD Guardian has already been updated at ' + LockedFile + '.');
  Result := True;
end;

procedure ScanDirectoryForLockedFiles(const DirPath: string; const Depth: Integer);
var
  FindRec: TFindRec;
  FullPath: string;
begin
  // Recursive walk of every file in the install directory: for *.dll/*.exe
  // we attempt DeleteFile; on failure we rename-out-of-the-way and queue a
  // deferred MoveFileEx for next reboot. This generalises the legacy
  // clrjit.dll-only handler — older Windows builds, AV/scanners, and
  // Smart App Control can lock any .dll/*.exe in {app}, not just the
  // .NET runtime's JIT. The user can grep "PreHandleLockedFiles" in the
  // %TEMP% setup log to see the entire scan trace.
  //
  // Safety: a hard depth cap (MaxScanDepth) PLUS skipping NTFS reparse
  // points (junction points, symlinks, OneDrive placeholders) prevents a
  // rogue or accidental junction under {app} from causing the installer to
  // cascade through arbitrary filesystem locations (e.g. C:\) and queue
  // random *.dll/*.exe for reboot-rename. Jumps in depth (Foo -> Foo\Bar
  // -> Foo\Bar\Baz) are bounded; runaway symlink loops are intercepted.
  if Depth >= MaxScanLevels then
  begin
    Log('PreHandleLockedFiles: Scan cap (' + IntToStr(MaxScanLevels) + ' levels) reached at ' + DirPath + '; not descending further.');
    Exit;
  end;
  if not DirExists(DirPath) then Exit;
  if not FindFirst(DirPath + '\*', FindRec) then Exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
           (LowerCase(FindRec.Name) <> SkipDirsForLockScan) and
           ((FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) = 0) then
          ScanDirectoryForLockedFiles(DirPath + '\' + FindRec.Name, Depth + 1);
      end
      else if IsProbablyLockedDllOrExe(FindRec.Name) and
              ((FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) = 0) then
      begin
        FullPath := DirPath + '\' + FindRec.Name;
        if not FileExists(FullPath) then
          Continue;
        if DeleteFile(FullPath) then
        begin
          Log('PreHandleLockedFiles: Removed out-of-date ' + FullPath + '.')
        end
        else
        begin
          Log('PreHandleLockedFiles: Could not DeleteFile ' + FullPath + ' (probably memory-mapped or held by AV). Renaming out of the way.');
          QueueLockedFileForRebootRemoval(FullPath);
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure PreHandleLockedFiles();
var
  AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  Log('PreHandleLockedFiles: Scanning ' + AppDir + ' for *.dll/*.exe that cannot be deleted (memory-mapped .NET runtime, AV lock, Smart App Control, or ACL-protected). This handles more files than just clrjit.dll.');
  ScanDirectoryForLockedFiles(AppDir, 0);
  Log('PreHandleLockedFiles: Scan complete.');
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
  // Surface in the setup log so admins managing existing installs immediately
  // understand why their pre-v2.0.26 C:\ADCheckLogs data is still on disk
  // after upgrading. The installer does not migrate old logs — preserving
  // forensic evidence wins over reclaiming disk space.
  Log('Note: pre-v2.0.26 logs in C:\ADCheckLogs are intentionally preserved on disk (not migrated, not deleted). New writes go to ' + ExpandConstant('{commonappdata}') + '\AdHealthMonitor\Logs.');
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
begin
  if CurStep = ssInstall then
  begin
    // Pre-empt locked-file rename-and-queue BEFORE Inno's [Files] copy runs.
    // Restart Manager has already tried to close the app; the surviving locks
    // here are typically from in-flight .NET runtime teardown of mmapped
    // files (clrjit.dll, hostfxr.dll, nativemethodsharper, runtimeconfig deps),
    // antivirus file locks, or Smart App Control denying write access to
    // executables in ProgramData. Rename those out of the way and queue
    // removal at next boot via MoveFileEx+MOVEFILE_DELAY_UNTIL_REBOOT.
    // Health log directories (ProgramData\AdHealthMonitor\Logs and \runs)
    // are created by the [Dirs] section above; no ForceDirectories needed
    // here. The AppState.db under %AppData%\AdHealthMonitor is created by
    // AppStateStore.Initialize() on first launch, not the installer.
    PreHandleLockedFiles();
    SetInstallStatus('Installing application files...');
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
