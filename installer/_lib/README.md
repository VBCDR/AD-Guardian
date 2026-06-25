# Inno Setup installer library — `installer/_lib/`

Reusable Inno Setup building blocks so every sibling installer in this repo (or
any new sister project) gets the same production-grade hardening for free,
without copy-paste divergence.

---

## Files

| File | Purpose |
|---|---|
| `installer/_lib/UniversalLockHandler.iss` | Portable Pascal Script snippet. Defines the universal *.dll / *.exe rename-and-queue handler that defends against `DeleteFile failed; code 5. Access is denied.` errors caused by memory-mapped runtimes, AV file locks, Defender Controlled Folder Access, and Smart App Control. **No `[Code]` / `end.` boundary wrapper** — this snippet is text-pasted into the caller's `[Code]` block via `#include`. |
| `installer/AD Guardian Installer.iss` | Reference consumer. Demonstrates the include directive, the required `[Setup]` flag, the required `[Files]` flags, the `CurStepChanged(ssInstall)` invocation, and the product-side wiring of `LockedFilesNeedReboot`. |

---

## When to use UniversalLockHandler.iss

Use it in **any** Inno Setup installer that copies a payload containing
`*.dll` or `*.exe` into a folder Windows treats as program code. Typical
cases:

- A `.NET WPF / WinForms / console` app (the `.NET` runtime mmap's
  `clrjit.dll`, `hostfxr.dll`, `coreclr.dll`, `nativemethodsharpener`, and
  managed DLLs on launch).
- A native C++ app shipping DLLs alongside the EXE.
- Any wrapper/exec that itself launches one of the above (defends the
  wrapper's payload, not the wrapped app).
- Test fixtures, side utilities, or one-off power tools that ship a
  `.dll`/`.exe` for re-distribution.

Don't use it for installers that ship **only** scripts / data files —
they can't trip the mmap / AV code-5 paths.

---

## What the caller MUST do

The snippet provides the public Pascal Script symbol
`procedure PreHandleLockedFiles`, plus internal helpers and constants. The
caller owns everything else. In particular:

### 1. `[Setup]` — verbose setup log

```ini
[Setup]
SetupLogging=yes
```

Without this, the user has no way to grep `PreHandleLockedFiles` in
`%TEMP%\Setup Log YYYY-MM-DD #NNN.txt` to diagnose surviving locks.
Critical for in-the-wild support.

### 2. `[Files]` — `ignoreversion restartreplace`

Apply the `restartreplace` flag to **every binary** entry so Inno's own
`MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` fallback queues replacements
in addition to ours:

```ini
[Files]
Source: "{#SourcePayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
```

`restartreplace` only covers `ERROR_SHARING_VIOLATION` (code 32). The
snippet's `PreHandleLockedFiles` covers `ERROR_ACCESS_DENIED` (code 5),
which is the family that mmap'd DLLs, Defender Controlled Folder Access,
and Smart App Control actually return. **Both layers are required.**

### 3. `[Code]` — include + CurStepChanged invocation

```ini
[Code]
// Insert anywhere in the [Code] block BEFORE the caller's own
// CurStepChanged procedure is what reads PreHandleLockedFiles().
#include "_lib\UniversalLockHandler.iss"

// Caller's own Pascal Script follows (constants, vars, functions, etc).
// The caller MUST define CurStepChanged and invoke PreHandleLockedFiles()
// from the ssInstall step. The snippet deliberately does not define
// CurStepChanged itself because Inno Setup rejects an installer script
// that defines an event procedure twice.

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Run BEFORE Inno's [Files] copy so the rename-out + MoveFileEx
    // queue has cleared every locked *.dll/*.exe by the time copy runs.
    PreHandleLockedFiles();
    // ... rest of install-step logic ...
  end;
end;
```

The include directive resolves relative to the caller's `.iss` location,
so sibling projects that copy the caller installer's structure (i.e.
keep `_lib\UniversalLockHandler.iss` next to their own `.iss`) need no
extra path setup.

### 4. `[Dirs]` — cleanup sentinel sink (optional but recommended)

The snippet auto-creates `{app}\__cleanup_pending_reboot__` on demand and
queues renamed `.deleteme` files there for next-reboot relocation via
`MoveFileExW(MOVEFILE_DELAY_UNTIL_REBOOT)`. **No explicit `[Dirs]` entry
required for the sentinel.** If you want the sink to live somewhere
explicit so users can find it, you may add:

```ini
[Dirs]
Name: "{app}\__cleanup_pending_reboot__"
```

…but the recursion-skip constant `SkipDirsForLockScan` already prevents
its contents from being re-scanned at next install, so this is purely
cosmetic.

---

## Risks of partial adoption

| Risk | Symptom | Mitigation |
|---|---|---|
| Caller forgets `SetupLogging=yes` | User cannot diagnose a remaining lock. Setup dialog shows only the generic "code 5" text. | Always enable. |
| Caller forgets `restartreplace` on a `[Files]` entry | Our handler rename-and-queue fires, but if `MoveFileExW` itself fails (kernel-mode lock, EDR), Inno's copy throws synchronously with no fallback. | Apply uniformly. |
| Caller forgets the `PreHandleLockedFiles()` call from `CurStepChanged` | Snippet is dormant; every lock still surfaces as a user-facing "DeleteFile failed; code 5" dialog. | Verify with a manual install over a running instance. |
| Caller overrides the snippet's `CurStepChanged` | Snippet's handler event hook is bypassed. | The snippet now never defines event procedures; if a future version changes that, the inheritor conflict will surface at ISCC compile time. |
| Caller redeclares one of the snippet's `const`/`var` symbols in their own `[Code]` block | ISCC compile error: duplicate identifier. The following names are owned by the snippet and must NOT be re-declared by the caller: `MOVEFILE_DELAY_UNTIL_REBOOT`, `CleanupPendingRebootDir`, `SkipDirsForLockScan`, `MaxScanLevels`, `FILE_ATTRIBUTE_REPARSE_POINT`, `IsProbablyLockedDllOrExe`, `MoveFileExW`, `QueueLockedFileForRebootRemoval`, `ScanDirectoryForLockedFiles`, `PreHandleLockedFiles`. | Pick caller-side names that do not collide, or place the caller's `const`/`var` blocks BEFORE the `.iss` `#include` so the snippet's identical names silently shadow (Pascal Script allows re-declaration with the same value; if values differ, this is a real error and a compile fail). |

---

## Sibling-project vendoring

`installer/_lib/UniversalLockHandler.iss` currently lives inside
*this* repository. Future sibling projects that ship their own Inno
Setup installer cannot `#include` across repo boundaries without git
submodule surgery, so **every sibling installer that wants the same
hardening must vendor a copy** of the snippet into their own installer
tree. Concrete steps:

1. Copy `installer/_lib/UniversalLockHandler.iss` from this repo into
   the sibling project's installer directory (e.g.
   `MyTool/installer/_lib/UniversalLockHandler.iss`).
2. Adjust the `#include` directive in the sibling's `.iss` to the
   resulting relative path (e.g. `#include "_lib\\UniversalLockHandler.iss"`
   if both `.iss` files live in the same directory).
3. Re-vendor when this repo publishes an update to the snippet. There
   is no automated sync today — until we publish the snippet as a git
   submodule or shared asset, vendoring is a manual, copy-the-file
   operation.

If the sibling project lives in a separate solution and cannot share
the snippet as a tree, treat the snippet copy as a one-off pin and
re-sync when this repo's v2.x branch publishes an update.

## Worked template (copy-paste)

```ini
; MyToolInstaller.iss  -- minimal v1.0 example

#define MyAppName "MyTool"
#define MyAppExeName "MyTool.exe"

[Setup]
AppId=MyTool
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\MyTool
PrivilegesRequired=admin
SetupLogging=yes        ; REQUIRED for diagnosis
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#SourcePayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Code]
#include "_lib\UniversalLockHandler.iss"

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    PreHandleLockedFiles();   ; rename-and-queue BEFORE [Files] copy
  end;
end;
```

Save this installer next to `installer\_lib\UniversalLockHandler.iss`
and `#include` resolves automatically.

---

## Testing the slice end-to-end

After authoring a new installer:

1. Install once normally (creates `{app}\__cleanup_pending_reboot__`).
2. Launch the installed app (it mmaps several DLLs from `{app}`).
3. Re-run the installer over the running instance.
4. Open `%TEMP%\Setup Log YYYY-MM-DD #NNN.txt` → grep
   `PreHandleLockedFiles`. You should see lines like:

   ```
   PreHandleLockedFiles: Scanning C:\Program Files\MyTool for ...
   PreHandleLockedFiles: Could not DeleteFile ...\clrjit.dll ...
   PreHandleLockedFiles: Could not DeleteFile ...\hostfxr.dll ...
   PreHandleLockedFiles: Could not DeleteFile ...\System.Private.CoreLib.dll ...
   PreHandleLockedFiles: Renamed ...\clrjit.dll to ...\clrjit.dll.deleteme and queued clean-up at next reboot ...
   PreHandleLockedFiles: Renamed ...\hostfxr.dll to ...\hostfxr.dll.deleteme and queued clean-up at next reboot ...
   PreHandleLockedFiles: Scan complete.
   ```

5. Reboot. The queued `.deleteme` files move out of `{app}\` to
   `{app}\__cleanup_pending_reboot__\` — visible if you want to audit.

If the log instead shows `DeleteFile failed; code 5. Access is denied.`
with no `PreHandleLockedFiles` lines, the caller forgot the
`CurStepChanged` invocation or `#include`. Re-check the `[Code]` block.

---

## Customising the warning text (NOT YET SUPPORTED)

The snippet's user-visible warning (`MsgBox` when `RenameFile` itself
fails) is currently **hardcoded** with generic wording:

> "Setup could not replace `<file>` because another process has it open.
> Please close the application (and any background instances) and click
> Retry to continue setup."

The snippet does **NOT** look for any caller-supplied override
constant. There is **no** `LockedFileRetryText`, `LockedFileMsgPrefix`,
or similar mechanism today. If you need product-specific wording,
fork the snippet locally and modify the `MsgBox(...)` call inside
`QueueLockedFileForRebootRemoval`, then re-vendor. A future version
may add a configurable override; do not write siblings that depend on
one before that ships.

Sibling projects that place their own messaging above the `MsgBox`
call (e.g. `WizardForm.Caption := 'MyTool setup';`) remain unaffected.
