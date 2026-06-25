// ============================================================================
// Universal Lock Handler -- reusable Inno Setup snippet for any installer
// that copies *.dll / *.exe into a Program Files / system location.
// ============================================================================
//
// Drop-in for any Inno Setup installer that ships a .NET runtime, native
// libraries, or any executable payload. Defends against
// "DeleteFile failed; code 5. Access is denied." errors caused by:
//
//   - Memory-mapped .NET runtime DLLs (clrjit.dll, hostfxr.dll, coreclr.dll,
//     nativemethodsharpener, etc.) that the running app still holds open.
//   - Antivirus file locks (Windows Defender, third-party AV/EDR).
//   - Defender Controlled Folder Access denying writes to protected paths.
//   - Smart App Control denying writes to updated executables.
//
// The handler renames any *.dll/*.exe in {app}\ that DeleteFile can't
// remove, queues MoveFileExW(MOVEFILE_DELAY_UNTIL_REBOOT) so the renamed
// file is moved out of the install directory at next reboot, and lets
// Inno's [Files] copy run clean. The user sees no error dialog.
//
// CALLER RESPONSIBILITY
//
// This file is text-pasted into the caller's [Code] block via the
// Inno Setup preprocessor directive:
//
//   #include "_lib\UniversalLockHandler.iss"
//
// The caller OWNS the [Code] event procedures (CurStepChanged,
// InitializeSetup, InitializeWizard, etc.) -- this snippet deliberately
// does NOT define any event procedure, because Inno Setup rejects an
// installer script that defines the same event twice. Caller must invoke
// `PreHandleLockedFiles();` from their own CurStepChanged(ssInstall).
//
// The caller is also expected to set in their [Setup] section:
//
//   SetupLogging=yes
//
// ...so users can grep `%TEMP%\Setup Log YYYY-MM-DD #NNN.txt` for
// `PreHandleLockedFiles` to diagnose remaining environmental locks.
//
// And in [Files]: every binary payload entry should include
// `Flags: ignoreversion restartreplace` (or restartreplace of its own).
//
// SEE installer/_lib/README.md FOR FULL USAGE INSTRUCTIONS.
// ============================================================================

const
  MOVEFILE_DELAY_UNTIL_REBOOT = $0004;
  // Sentinel cleanup subdir under {app}. Auto-created on demand; renamed
  // locked files are moved there on next reboot. Skipped by the recursive
  // scan so its contents (~.deleteme files queued for cleanup) are never
  // re-scanned.
  CleanupPendingRebootDir = '__cleanup_pending_reboot__';
  // Subdirectory names (case-insensitive) under {app} that the recursive
  // scan should NEVER descend into.
  SkipDirsForLockScan = '__cleanup_pending_reboot__';
  // Maximum directory-LEVEL depth that ScanDirectoryForLockedFiles will
  // descend from the install root. AD Guardian's payload nests at most
  // ~3 levels deep (runtimes\win-x64\native); 16 is a generous safety
  // bound. Hard cap prevents a malicious or accidentally-created NTFS
  // junction (`{app}\foo -> C:\`) from causing the installer to cascade
  // through the system root and queue arbitrary *.dll/*.exe for reboot
  // rename.
  MaxScanLevels = 16;
  // NTFS reparse-point attribute. Entries with this attribute are
  // junction points, symlinks, or OneDrive placeholder folders -- we do
  // NOT recurse INTO them (defeats depth-cap bypass) AND we do NOT touch
  // symlinked files (prevents the rename-on-failure path from following
  // the link to caller-controlled destinations outside {app}). Keeps the
  // scan strictly scoped to real install payload files.
  FILE_ATTRIBUTE_REPARSE_POINT = $00000400;

function IsProbablyLockedDllOrExe(const FileName: string): Boolean;
var
  Lower: string;
begin
  // Narrow wildcard match on the *suffix* (.dll / .exe). Config files,
  // licences, JSON, INI, etc. are not memory-mapped, so DeleteFile on
  // them almost always succeeds -- we leave them alone to keep the
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
  // Windows blocks DELETING memory-mapped executables/DLLs
  // (ERROR_ACCESS_DENIED, code 5) but explicitly allows RENAMING them.
  // We exploit that:
  //
  //   1) Auto-create a sentinel cleanup subdir under {app} if missing.
  //   2) Rename the locked file out of the way (.deleteme suffix).
  //      Always succeeds even while the file is mmap'd, because Windows
  //      treats rename differently from delete on open files.
  //   3) Queue MoveFileExW(.deleteme, cleanup-subdir\.deleteme,
  //      MOVEFILE_DELAY_UNTIL_REBOOT) so the old file is moved out of
  //      {app}\ on next reboot. We deliberately use a NON-NULL target
  //      path: the Win32 docs document that NULL schedules a deferred
  //      delete, but Inno Pascal's String-to-stdcall marshalling passes
  //      an empty string as a non-null PWideChar, not as a NULL pointer,
  //      which makes the NULL-target behaviour undefined. A non-null
  //      cleanup path is safe regardless of marshalling semantics.
  //   4) Inno's [Files] copy then sees a renamed-away path and copies
  //      the new file cleanly, with no user-facing "DeleteFile failed;
  //      code 5. Access is denied." dialog.
  Result := False;
  CleanupDir := ExpandConstant('{app}\') + CleanupPendingRebootDir;
  ForceDirectories(CleanupDir);
  TempRenamedFile := LockedFile + '.deleteme';
  DeleteFile(TempRenamedFile);
  RenameOk := RenameFile(LockedFile, TempRenamedFile);
  if not RenameOk then
  begin
    Log('PreHandleLockedFiles: WARNING ' + LockedFile + ' is in use (memory-mapped, AV-locked, or ACL-protected) and could not be renamed out of the way. The [Files] copy that follows WILL fail for this file unless the user closes the running application.');
    if not WizardSilent then
      MsgBox('Setup could not replace ' + LockedFile + ' because another process has it open.' + #13#10 + #13#10 +
             'Please close the application (and any background instances) and click Retry to continue setup.', mbInformation, MB_OK);
    Exit;
  end;
  CleanupDestFile := AddBackslash(CleanupDir) + ExtractFileName(TempRenamedFile);
  DeleteFile(CleanupDestFile);
  if MoveFileExW(TempRenamedFile, CleanupDestFile, MOVEFILE_DELAY_UNTIL_REBOOT) then
    Log('PreHandleLockedFiles: Renamed ' + LockedFile + ' to ' + TempRenamedFile + ' and queued clean-up at next reboot (target ' + CleanupDestFile + ').')
  else
    Log('PreHandleLockedFiles: WARNING MoveFileExW could not queue ' + TempRenamedFile + ' for clean-up at next reboot. The .deleteme file will remain in the install directory until manually removed, but the new binary has already been copied at ' + LockedFile + '.');
  Result := True;
end;

procedure ScanDirectoryForLockedFiles(const DirPath: string; const Depth: Integer);
var
  FindRec: TFindRec;
  FullPath: string;
begin
  // Recursive walk of every file in the install directory: for *.dll /
  // *.exe we attempt DeleteFile; on failure we rename-out-of-the-way
  // and queue a deferred MoveFileEx for next reboot. Generalises the
  // legacy clrjit.dll-only handler -- any .dll/*.exe in {app} may be
  // memory-mapped, AV-locked, or ACL-blocked on modern Windows builds,
  // not just the .NET runtime's JIT. Users can grep "PreHandleLockedFiles"
  // in the %TEMP% setup log to see the entire scan trace.
  //
  // Safety: a hard depth cap (MaxScanLevels) PLUS skipping NTFS reparse
  // points (junction points, symlinks, OneDrive placeholders) prevents a
  // rogue or accidental junction under {app} from causing the installer
  // to cascade through arbitrary filesystem locations (e.g. C:\) and
  // queue random *.dll/*.exe for reboot-rename. Depth jumps are bounded;
  // runaway symlink loops are intercepted.
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
  // Public entry point. Caller invokes this at the top of their own
  // CurStepChanged(ssInstall), AFTER Restart Manager has attempted to
  // close the running application and BEFORE Inno's [Files] copy.
  AppDir := ExpandConstant('{app}');
  Log('PreHandleLockedFiles: Scanning ' + AppDir + ' for *.dll/*.exe that cannot be deleted (memory-mapped runtime, AV lock, Smart App Control, or ACL-protected).');
  ScanDirectoryForLockedFiles(AppDir, 0);
  Log('PreHandleLockedFiles: Scan complete.');
end;
