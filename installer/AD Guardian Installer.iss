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

// --- Portable locked-file handler -----------------------------------------
// The universal *.dll / *.exe rename-and-queue handler (PreHandleLockedFiles)
// lives in installer/_lib/UniversalLockHandler.iss so the same logic can be
// re-used by future sibling installers without copy-paste divergence. This
// file is #include'd below -- the snippet's Pascal Script body is
// concatenated into this [Code] block and provides the public symbol:
//
//     procedure PreHandleLockedFiles();
//
// CALLER INVOKES PreHandleLockedFiles() FROM THEIR OWN CurStepChanged(ssInstall)
// -- Inno Setup rejects an installer script that defines an event procedure
// twice, so the snippet deliberately does NOT define CurStepChanged itself.
//
// SEE installer/_lib/README.md for what the caller must add to [Setup],
// [Files], and any per-product message text overrides.
#include "_lib\UniversalLockHandler.iss"

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
  // Surface in the setup log so admins managing existing installs see what
  // happens to legacy data. The constant 'C:\ADCheckLogs' below is the
  // hardcoded pre-v2.0.26 path the app used to write to before log writes
  // moved to CommonApplicationData. The post-install step now removes
  // that dir tree automatically -- admins no longer need to clean it up
  // manually. New writes go to the ProgramData path.
  Log('Note: pre-v2.0.26 logs in C:\ADCheckLogs will be removed by the post-install step. New writes go to ' + ExpandConstant('{commonappdata}') + '\AdHealthMonitor\Logs.');
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

// Silent cleanup of the pre-v2.0.26 legacy log dir. The app used to write to
// C:\ADCheckLogs but moved to {commonappdata}\AdHealthMonitor\Logs in v2.0.26
// to dodge Defender Controlled Folder Access / Smart App Control locks on
// arbitrary user-created system-root paths. Earlier installers preserved
// the legacy dir for forensic reasons; from this build onward we proactively
// remove it during CurStepChanged(ssPostInstall) so admins do not have to
// clean it up manually. Silent on the wizard surface -- the user sees no
// dialog -- but logged in %TEMP%\Setup Log YYYY-MM-DD #NNN.txt so admins can
// audit what happened.
//
// Inno's FindFirst/FindNext does not expose a recursive walker, so the byte
// count we surface is intentionally a TOP-LEVEL COUNT rather than a recursive
// size. The legacy dir in practice contains a handful of empty dated
// subdirs left over from the daily run cleanup, so top-level count is the
// meaningful signal for "did the cleanup find anything".
//
// LIMITATION: Inno's DelTree silently follows NTFS junctions / symlinks. If
// C:\ADCheckLogs was redirected (junction OR symlink) to a directory
// outside C:\, this cleanup will delete the target's contents too. The
// probability is low -- the legacy v2.0.25-and-earlier app only ever
// created files under C:\ADCheckLogs, never junctions -- but admins who
// intentionally redirected the dir are responsible for sanity-checking
// before upgrading. Pascal Script does not expose FILE_ATTRIBUTE_REPARSE_POINT
// natively so an in-script reparse probe would require Win32 externals; we
// accept that residual risk here because the historical app behaviour gives
// us no evidence the path ever contained a junction.
//
// If the path is locked (Defender scanning, open handles, ACL-protected),
// DelTree returns False and we log a warning -- we deliberately do NOT throw
// or pop a dialog because installing the new app must succeed independently
// of this optional cleanup.
function CleanupLegacyAdCheckLogs(): Boolean;
var
  LegacyDir: string;
  Rec: TFindRec;
  TopLevelCount: Integer;
begin
  Result := True;
  LegacyDir := 'C:\ADCheckLogs';

  if not DirExists(LegacyDir) then
  begin
    Log('Cleanup: legacy C:\ADCheckLogs is not present on this machine; nothing to remove.');
    Exit;
  end;

  TopLevelCount := 0;
  if FindFirst(LegacyDir + '\*', Rec) then
  try
    repeat
      Inc(TopLevelCount);
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;

  try
    if DelTree(LegacyDir, True, True, True) then
    begin
      Log(Format('Cleanup: removed legacy %s (%d top-level entries). New AD Guardian logs go to %s\AdHealthMonitor\Logs.', [LegacyDir, TopLevelCount, ExpandConstant('{commonappdata}')]));
      // Tell the app about the migration so it can show the "Migration Complete"
      // toast on first launch. Marker write is best-effort -- a failed write
      // here only suppresses the toast, never the install.
      if not WriteMigrationMarker('removed', TopLevelCount, '') then
        Log('Cleanup: WARNING could not write MigrationMarker.json (write-failure is non-fatal; app will not show migration toast).');
    end
    else
    begin
      Result := False;
      Log(Format('Cleanup: WARNING could not remove legacy %s. Some files may be locked by Defender Controlled Folder Access, Smart App Control, or third-party antivirus. The directory is still present after install. Admins can clear it manually via `rmdir /S /Q C:\ADCheckLogs` from an elevated cmd, or by rebooting and re-running setup.', [LegacyDir]));
      // Migration incomplete -- write a 'failed' marker so the app shows a
      // warning toast with the same root-cause detail. Best-effort write.
      if not WriteMigrationMarker('failed', 0, Format('DelTree returned False: some files in %s were locked at install time', [LegacyDir])) then
        Log('Cleanup: WARNING could not write MigrationMarker.json (write-failure is non-fatal; migration toast skipped).');
    end;
  except
    Result := False;
    Log(Format('Cleanup: REMOVAL of %s raised: %s', [LegacyDir, GetExceptionMessage]));
    // Exception branch also gets a 'failed' marker so the user sees the
    // diagnostic detail in the toast.
    if not WriteMigrationMarker('failed', 0, Format('Pascal exception: %s', [GetExceptionMessage])) then
      Log('Cleanup: WARNING could not write MigrationMarker.json (write-failure is non-fatal; migration toast skipped).');
  end;
end;

// Writes a v1 MigrationMarker.json to %ProgramData%\AdHealthMonitor\. Hand-rolled
// JSON (Pascal Script does not expose System.Text.Json / JSON.NET). Calls
// MigrationMarker.TryReadAndDelete in App.OnStartup, so schema must stay in
// lockstep. Update both sides together if the schema is ever bumped.
//
// The marker is written UNCONDITIONALLY on each post-install cleanup attempt
// EXCEPT for the absent-path branch -- on a brand-new machine (no legacy data
// ever existed), we deliberately do NOT write a marker because the installer's
// already-empty audit log is sufficient auditable evidence. The app would
// otherwise show a confusing "Migration Complete" toast to a user who never
// had legacy data.
function GetMigrationMarkerPath(): string;
begin
  Result := ExpandConstant('{commonappdata}') + '\AdHealthMonitor\MigrationMarker.json';
end;

function WriteMigrationMarker(const Status: string; Entries: Integer; const Reason: string): Boolean;
var
  MarkerPath: string;
  MarkerJson: TStringList;
  ReasonEscaped: string;
  EntriesStr: string;
  TimeStr: string;
  I: Integer;
  C: Char;
  Code: Integer;
  HexStr: string;
begin
  Result := False;
  MarkerPath := GetMigrationMarkerPath();

  // RFC 8259 JSON-escape the Reason string by hand -- Pascal Script has no
  // JSON library and the installer's audit log can contain quotes, backslashes,
  // and newlines. The on-disk file is read by Newtonsoft.Json which is strict
  // about escape correctness.
  ReasonEscaped := '';
  for I := 1 to Length(Reason) do
  begin
    C := Reason[I];
    // RFC 8259 string escapes, named chars first then \u00XX for remaining 0..31 control chars.
    case C of
      '"':  ReasonEscaped := ReasonEscaped + '\"';
      '\':  ReasonEscaped := ReasonEscaped + '\\';
      #8:   ReasonEscaped := ReasonEscaped + '\b';   { \b  -- backspace (unlikely in installer logs but per RFC) }
      #9:   ReasonEscaped := ReasonEscaped + '\t';
      #10:  ReasonEscaped := ReasonEscaped + '\n';
      #12:  ReasonEscaped := ReasonEscaped + '\f';   { \f  -- form feed   (unlikely in installer logs but per RFC) }
      #13:  ReasonEscaped := ReasonEscaped + '\r';
    else
      begin
        Code := Ord(C);
        if (Code >= 0) and (Code < 32) then
        begin
          HexStr := IntToHex(Code, 2);
          if Length(HexStr) = 1 then
            HexStr := '0' + HexStr;
          ReasonEscaped := ReasonEscaped + '\u00' + HexStr;
        end
        else
          ReasonEscaped := ReasonEscaped + C;
      end;
    end;
  end;

  EntriesStr := IntToStr(Entries);

  // Local-time ISO-8601 (no UTC claim -- Pascal Script's FormatDateTime does
  // not expose UtcNow natively, and an installation timestamp rounded to
  // local time is fine for "when did I install this?" UX). The C# reader
  // treats this as an opaque string.
  TimeStr := FormatDateTime('yyyy-mm-dd', Now) + 'T' + FormatDateTime('hh:nn:ss', Now);

  MarkerJson := TStringList.Create;
  try
    MarkerJson.Add('{');
    // schemaVersion: 1 is HARDCODED here on purpose -- it MUST match
    // MigrationMarker.CurrentSchemaVersion in C# (MigrationMarker.cs). If you
    // bump the C# version, update this literal in lockstep or future markers
    // will be silently left on disk by the parser's `> CurrentSchemaVersion`
    // guard -- acceptable fallout, but documented so the invariant is obvious.
    MarkerJson.Add('  "schemaVersion": 1,');
    MarkerJson.Add('  "cleanupStatus": "' + Status + '",');
    MarkerJson.Add('  "entriesRemoved": ' + EntriesStr + ',');
    MarkerJson.Add('  "installTime": "' + TimeStr + '",');
    if Reason <> '' then
      MarkerJson.Add('  "reason": "' + ReasonEscaped + '"');
    MarkerJson.Add('}');
    // TEncoding.UTF8 writes a BOM; Newtonsoft.Json handles UTF-8 with or
    // without BOM, so this round-trips cleanly on the C# side.
    MarkerJson.SaveToFile(MarkerPath, TEncoding.UTF8);
    Result := True;
  except
    Result := False;
  end;
  MarkerJson.Free;
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
    // Silent (no UI) cleanup of pre-v2.0.26 legacy C:\ADCheckLogs tree now
    // that the new CommonApplicationData-backed log path is in place. See
    // CleanupLegacyAdCheckLogs above for the implementation + invariants
    // (deliberately best-effort: install completion does NOT depend on
    // removal succeeding -- so a Defender-locked legacy tree does not block
    // the install).
    CleanupLegacyAdCheckLogs();
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if CurProgress > 0 then
    SetInstallStatus('Installing application files...');
end;
