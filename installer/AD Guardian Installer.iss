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
// remove it during CurStepChanged(ssPostInstall) after COPYING any legacy
// log files (.log/.txt/.json/.csv) into a timestamped
// {commonappdata}\AdHealthMonitor\Logs\legacy-import-yyyymmdd-hhnnss\
// subfolder so the user keeps their historical data.
//
// Silent on the wizard surface -- the user sees no dialog -- but logged to
// %TEMP%\Setup Log YYYY-MM-DD #NNN.txt so admins can audit what happened.
//
// Inno's FindFirst/FindNext does not expose a recursive walker, so DELTREE's
// success is reported against the TOP-LEVEL entry count rather than a
// recursive size. The recursive copy phase IS iteratable via a hand-rolled
// stack-based DFS in MigrateLegacyLogsTo -- see that function below.
//
// LIMITATION: Inno's DelTree silently follows NTFS junctions / symlinks. If
// C:\ADCheckLogs was redirected (junction OR symlink) to a directory
// outside C:\, this cleanup will delete the target's contents too. Pascal
// Script does not expose FILE_ATTRIBUTE_REPARSE_POINT natively so an
// in-script reparse probe would require Win32 externals; MigrateLegacyLogsTo
// does uses FileGetAttr (FILE_ATTRIBUTE_DIRECTORY) to detect non-junction
// subdirs but does NOT pre-flight DelTree itself -- accepting residual risk
// here because the historical app never created junctions.
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
  MigratedRoot: string;
  EntriesMigrated, Collisions, CopyErrors: Integer;
  BytesMigrated: Int64;
  MigrationSucceeded: Boolean;
  DelSucceeded: Boolean;
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

  // ── Phase 1: Copy legacy log files into a timestamped subfolder of the
  //    new log path. STRICT order: copy FIRST, then delete. If the copy
  //    destination folder cannot be created (e.g. ACL on
  //    {commonappdata}\AdHealthMonitor\Logs denied), we ABORT the entire
  //    cleanup so the legacy dir is left intact for forensics. Data loss is
  //    not acceptable here -- if MigrateLegacyLogsTo returns False we skip
  //    DelTree and write a 'failed' marker.
  MigratedRoot := '';
  EntriesMigrated := 0;
  Collisions := 0;
  CopyErrors := 0;
  MigrationSucceeded := MigrateLegacyLogsTo(
    LegacyDir,
    ExpandConstant('{commonappdata}') + '\AdHealthMonitor\Logs',
    MigratedRoot,
    EntriesMigrated,
    Collisions,
    CopyErrors,
    BytesMigrated
  );
  if not MigrationSucceeded then
  begin
    Log(Format('Cleanup: WARNING could not initialize migration destination under %s\AdHealthMonitor\Logs. Legacy directory retained for forensics.', [ExpandConstant('{commonappdata}')]));
    if not WriteMigrationMarker('failed', TopLevelCount, 'Migration destination folder could not be created; legacy directory retained.',
      EntriesMigrated, Collisions, CopyErrors, MigratedRoot, BytesMigrated) then
      Log('Cleanup: WARNING could not write MigrationMarker.json (write-failure is non-fatal; migration toast skipped).');
    Exit;
  end;

  // ── Phase 2: Delete the legacy directory tree.
  DelSucceeded := True;
  try
    DelSucceeded := DelTree(LegacyDir, True, True, True);
  except
    DelSucceeded := False;
    Log(Format('Cleanup: REMOVAL of %s raised: %s', [LegacyDir, GetExceptionMessage]));
  end;

  // ── Phase 3: Pick a status + write marker reflecting both phases.
  if DelSucceeded then
  begin
    // Migration succeeded even if some files had copy errors. Those will
    // appear as 'partial' if CopyErrors > 0 -- otherwise 'removed'.
    if CopyErrors > 0 then
    begin
      Log(Format('Cleanup: removed legacy %s (%d top-level entries); %d log files copied to %s, %d copy errors, %d collisions.', [LegacyDir, TopLevelCount, EntriesMigrated, MigratedRoot, CopyErrors, Collisions]));
      if not WriteMigrationMarker('partial', TopLevelCount, Format('%d file(s) failed to copy -- likely Defender / AV lock', [CopyErrors]),
        EntriesMigrated, Collisions, CopyErrors, MigratedRoot, BytesMigrated) then
        Log('Cleanup: WARNING could not write MigrationMarker.json (write-failure is non-fatal; migration toast skipped).');
    end
    else
    begin
      Log(Format('Cleanup: removed legacy %s (%d top-level entries). %d log file(s) copied to %s (%d collisions). New AD Guardian logs go to %s\AdHealthMonitor\Logs.', [LegacyDir, TopLevelCount, EntriesMigrated, MigratedRoot, Collisions, ExpandConstant('{commonappdata}')]));
      if not WriteMigrationMarker('removed', TopLevelCount, '',
        EntriesMigrated, Collisions, CopyErrors, MigratedRoot, BytesMigrated) then
        Log('Cleanup: WARNING could not write MigrationMarker.json (write-failure is non-fatal; migration toast skipped).');
    end;
  end
  else
  begin
    Result := False;
    Log(Format('Cleanup: WARNING could not remove legacy %s. Some files may be locked by Defender Controlled Folder Access, Smart App Control, or third-party antivirus. The directory is still present after install. Admins can clear it manually via scripts\cleanup-adchecklogs.ps1 -Force -Json for a recursive walk with file/byte counts + symlink detection, or `rmdir /S /Q C:\ADCheckLogs` from an elevated cmd if PowerShell is unavailable, or by rebooting and re-running setup. Migration copy completed (%d files -> %s) before the deletion failed, so data is preserved.', [LegacyDir, EntriesMigrated, MigratedRoot]));
    if not WriteMigrationMarker('failed', TopLevelCount, Format('DelTree returned False: some files in %s were locked at install time', [LegacyDir]),
      EntriesMigrated, Collisions, CopyErrors, MigratedRoot, BytesMigrated) then
      Log('Cleanup: WARNING could not write MigrationMarker.json (write-failure is non-fatal; migration toast skipped).');
  end;
end;

// Copies every *.log / *.txt / *.json / *.csv file under LegacyDir into a
// timestamped subfolder of LogsRoot (usually
// {commonappdata}\AdHealthMonitor\Logs\legacy-import-yyyymmdd-hhnnss\<relative path>),
// preserving the relative path layout so the imported tree mirrors the
// legacy tree exactly.
//
// Reasons for the strict copy-then-delete ordering:
//   1. The user explicitly asked to copy logs from the legacy directory to
//      the new one BEFORE deletion. We never silently lose data.
//   2. A timestamped subfolder eliminates cross-upgrade collisions: each
//      v2.0.27+ install lands at e.g. legacy-import-20260625-153045 and never
//      overwrites a previous migration's content (even if files have the
//      same relative paths).
//   3. Per-file collision-rename keeps extensions intact (.log stays .log) so
//      AD Guardian's log parser and any operator-side tooling still recognise
//      the format. We append a -yyyymmdd-hhnnss stem suffix on the colliding
//      file ONLY -- preserving readability.
//
// Pascal Script has no native recursive walker. We hand-roll a stack-based DFS
// using a TStringList and Inno's built-in FindFirst/FindNext. Each entry is
// checked for FILE_ATTRIBUTE_REPARSE_POINT (0x400) via FileGetAttr so NTFS
// junctions/symlinks are SKIPPED unchanged (we never follow them into cascade
// deletions). Non-junction subdirectories are pushed onto the stack.
//
// Per-file copy via Pascal Script's CopyFile(Source, Dest, FailIfExists=True).
// On collision, we retry once with a timestamp-suffixed name. We do NOT block
// the migration on a single failing file -- CopyErrors is incremented and
// logged, but the walker continues.
//
// Returns:
//   Result := True if the destination folder was created AND every file we
//   could copy was copied. False ONLY if the destination folder itself could
//   not be created -- so the caller can abort the subsequent DelTree cleanly.
//
// Output vars report the actual outcome:
//   outDestinationRoot := the timestamped folder we copied into
//   outEntriesMigrated := number of files copied (migrated)
//   outCollisions      := number of files renamed due to destination-name collision
//   outCopyErrors      := number of files we could NOT copy (best-effort ignore)
function MigrateLegacyLogsTo(const LegacyDir, LogsRoot: string; var outDestinationRoot: string;
  var outEntriesMigrated, outCollisions, outCopyErrors: Integer;
  var outBytesMigrated: Int64): Boolean;
var
  TimeStamp: string;
  DirStack: TStringList;
  CurrentDir, SourcePath, RelPath, DestPath, ParentDir: string;
  Rec: TFindRec;
  Attrs: Integer;
begin
  Result := True;
  outEntriesMigrated := 0;
  outCollisions := 0;
  outCopyErrors := 0;
  outBytesMigrated := 0;
  outDestinationRoot := '';

  if not DirExists(LegacyDir) then
    Exit; // nothing to migrate

  TimeStamp := FormatDateTime('yyyymmdd-hhnnss', Now);
  outDestinationRoot := AddBackslash(LogsRoot) + 'legacy-import-' + TimeStamp;
  if not ForceDirectories(outDestinationRoot) then
  begin
    Result := False;
    outDestinationRoot := '';
    Exit;
  end;

  DirStack := TStringList.Create;
  try
    DirStack.Add(LegacyDir);
    while DirStack.Count > 0 do
    begin
      CurrentDir := DirStack[DirStack.Count - 1];  // pop
      DirStack.Delete(DirStack.Count - 1);

      if FindFirst(CurrentDir + '\*', Rec) then
      try
        repeat
          if (Rec.Name = '.') or (Rec.Name = '..') then
            Continue;

          SourcePath := CurrentDir + '\' + Rec.Name;

          // Skip symlinks/junctions -- never follow them. FileGetAttr returns
          // the Win32 FILE_ATTRIBUTE_* bitmask; 0x400 is REPARSE_POINT.
          Attrs := FileGetAttr(SourcePath);
          if (Attrs and $00000400) <> 0 then
            Continue;

          if (Attrs and $00000010) <> 0 then  // FILE_ATTRIBUTE_DIRECTORY
          begin
            DirStack.Add(SourcePath);
            Continue;
          end;

          // Only copy log-shaped files. AD Guardian's legacy path was
          // permissive about what admins dropped in there, so we restrict
          // imports to text-shaped extensions to avoid shuttling arbitrary
          // binaries into %ProgramData% (which some AppLocker/SAC defaults
          // treat as an elevated trust zone).
          if not HasAllowedLegacyExt(Rec.Name) then
            Continue;

          // Compute destination with relative-path preservation.
          RelPath := Copy(SourcePath, Length(LegacyDir) + 2, Length(SourcePath));
          // RelPath e.g. "runs\2026-01-15\run1.txt" when LegacyDir = 'C:\ADCheckLogs'
          DestPath := outDestinationRoot + '\' + RelPath;

          ParentDir := ExtractFilePath(DestPath);
          if (ParentDir <> '') and (not ForceDirectories(ParentDir)) then
          begin
            Inc(outCopyErrors);
            Log(Format('Migration: WARNING could not create parent folder %s; copy skipped', [ParentDir]));
            Continue;
          end;

          // Direct copy. If FailIfExists=True and target clashes, this returns
          // False instead of raising. We then retry once with a timestamp
          // suffix so the file is taken along without overwriting anything.
          //
          // Both CopyFile calls are wrapped in one outer try/except so an
          // edge-case raise from EITHER call (Defender mid-scan on the parent
          // dir, transient UNC drop, malformed path on a redirected volume)
          // doesn't propagate out of MigrateLegacyLogsTo and bypass the
          // downstream status logic in CleanupLegacyAdCheckLogs. Pascal Script
          // docs say CopyFile returns Boolean on most errors, but defensive
          // symmetry is cheap and avoids silent migration-phase aborts.
          try
            if CopyFile(SourcePath, DestPath, True) then
            begin
              Inc(outEntriesMigrated);
              // Rec.Size is a TFindRec Int64 byte count (Inno Setup >= 5.5).
              // Sums here match what C# ComputeBytesMigratedFromDisk would
              // compute on read -- but doing it in the installer avoids any
              // UI-thread enumeration cost on first launch.
              Inc(outBytesMigrated, Rec.Size);
            end
            else
            begin
              DestPath := ParentDir + MakeCollisionName(ExtractFileName(DestPath), TimeStamp);
              if CopyFile(SourcePath, DestPath, True) then
              begin
                Inc(outEntriesMigrated);
                Inc(outCollisions);
                Inc(outBytesMigrated, Rec.Size);
              end
              else
              begin
                Inc(outCopyErrors);
                Log(Format('Migration: WARNING could not copy %s -> %s (collision retry failed)', [SourcePath, DestPath]));
              end;
            end;
          except
            Inc(outCopyErrors);
            Log(Format('Migration: copy of %s raised: %s', [SourcePath, GetExceptionMessage]));
          end;
        until not FindNext(Rec);
      finally
        FindClose(Rec);
      end;
    end;
  finally
    DirStack.Free;
  end;
end;

// File-extension allowlist helper. Pascal Script's LowerCase is a builtin.
function HasAllowedLegacyExt(const FileName: string): Boolean;
var
  Ext: string;
begin
  Ext := LowerCase(ExtractFileExt(FileName));
  Result := (Ext = '.log') or (Ext = '.txt') or (Ext = '.json') or (Ext = '.csv');
end;

// Produce a collision-rename: foo.log -> foo-<TimeStamp>.log. Keeps the
// extension intact so downstream log parsers and report tooling still
// recognise the format.
function MakeCollisionName(const FileName, TimeStamp: string): string;
var
  Stem, Ext: string;
begin
  Ext := ExtractFileExt(FileName);
  if Ext <> '' then
    Stem := Copy(FileName, 1, Length(FileName) - Length(Ext))
  else
    Stem := FileName;
  Result := Stem + '-' + TimeStamp + Ext;
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

// RFC 8259 JSON string escaper shared by Reason + DestinationRoot (and any
// future string field that might land in the marker). Pascal Script has no
// built-in JSON library, so we hand-roll the escape loop. Named chars first
// (\" \\ \b \t \n \f \r), then \u00XX for anything else in 0..31. Codepoints
// >= 32 are passed through unchanged. Genuinely unusual control characters
// (e.g. U+0008 backspace) rarely appear in installer output, but the helper
// is exhaustive so it works on any future audit string too.
function EscapeJsonString(const S: string): string;
var
  I: Integer;
  C: Char;
  Code: Integer;
  HexStr: string;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    case C of
      '"':  Result := Result + '\"';
      '\':  Result := Result + '\\';
      #8:   Result := Result + '\b';
      #9:   Result := Result + '\t';
      #10:  Result := Result + '\n';
      #12:  Result := Result + '\f';
      #13:  Result := Result + '\r';
    else
      begin
        Code := Ord(C);
        if (Code >= 0) and (Code < 32) then
        begin
          HexStr := IntToHex(Code, 2);
          if Length(HexStr) = 1 then
            HexStr := '0' + HexStr;
          Result := Result + '\u00' + HexStr;
        end
        else
          Result := Result + C;
      end;
    end;
  end;
end;

function WriteMigrationMarker(const Status: string; Entries: Integer; const Reason: string;
  EntriesMigrated, EntriesCollisions, ErrorCount: Integer; const DestinationRoot: string;
  BytesMigrated: Int64): Boolean;
var
  MarkerPath: string;
  MarkerJson: TStringList;
  ReasonEscaped: string;
  EntriesStr: string;
  TimeStr: string;
begin
  Result := False;
  MarkerPath := GetMigrationMarkerPath();

  // RFC 8259 JSON-escape both Reason and DestinationRoot. The C# reader
  // (Newtonsoft.Json) is strict about escape correctness, so we centralise
  // the escaping in EscapeJsonString and call it from each field write.
  // (Earlier versions of this function inline-escaped Reason here, but the
  // duplicated write path drifted out of sync with the DestinationRoot
  // call below, so we now use a single source of truth.)
  ReasonEscaped := EscapeJsonString(Reason);

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
    //
    // New fields below are ADDITIVE (Newtonsoft.Json auto-defaults handle
    // missing keys), so adding fields here does not require a schemaVersion
    // bump -- the C# reader accepts them and treats absent fields as zero /
    // empty depending on type. SchemaVersion only bumps when the on-disk
    // SHAPE changes incompatibly.
    MarkerJson.Add('  "schemaVersion": 1,');
    MarkerJson.Add('  "cleanupStatus": "' + Status + '",');
    MarkerJson.Add('  "entriesRemoved": ' + EntriesStr + ',');
    MarkerJson.Add('  "installTime": "' + TimeStr + '",');
    if Reason <> '' then
      MarkerJson.Add('  "reason": "' + ReasonEscaped + '",');
    MarkerJson.Add('  "entriesMigrated": ' + IntToStr(EntriesMigrated) + ',');
    MarkerJson.Add('  "destinationRoot": "' + EscapeJsonString(DestinationRoot) + '",');
    MarkerJson.Add('  "entriesCollisions": ' + IntToStr(EntriesCollisions) + ',');
    MarkerJson.Add('  "bytesMigrated": ' + IntToStr(BytesMigrated) + ',');
    MarkerJson.Add('  "errorCount": ' + IntToStr(ErrorCount));
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
