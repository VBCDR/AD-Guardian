# AD Guardian

Welcome to **AD Guardian** — a tool built to monitor and test Active Directory health in a smart, automated, and user-friendly way. It simplifies routine AD checks, schedules tests, and sends detailed email notifications so you can always stay on top of your environment.

**Current Version: [v2.0.27](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.27)** | [Latest Release](https://github.com/VBCDR/AD-Guardian/releases/latest)

## Features

- **Health Dashboard:**  
  Real-time health score, pass/fail rates, and findings overview at a glance.

- **Scheduled Tests:**  
  Automatically run health checks on your domain controllers on a schedule via Windows Task Scheduler.

- **Findings & Remediation:**  
  Actionable issues from tests, AD inventory, telemetry, and privilege analysis with remediation guidance.

- **Infrastructure Inventory:**  
  AD forest/domain details, DC counts, trusts, OUs, GPOs, privileged group membership.

- **Security Posture:**  
  SMB/LDAP signing checks, certificate/DHCP analysis, and privilege group monitoring.

- **Detailed Logging:**  
  Logs for every test are saved, viewable in-app, exportable as CSV/HTML, and pop-out capable.

- **Email Notifications:**  
  Receive well-formatted HTML emails summarizing test results — automatically sent when tests complete.

- **Modern User Interface:**  
  A clean, animated WPF UI with sidebar navigation, lazy-loaded tab pages, and responsive layout.

## Changelog

### v2.0.27
- **Installer auto-cleanup of pre-v2.0.26 `C:\ADCheckLogs`** during `CurStepChanged(ssPostInstall)` — replaces the v2.0.26 "preserved for forensics" behaviour. When legacy data is detected, the installer removes the legacy dir tree and writes a `MigrationMarker.json` under `%ProgramData%\AdHealthMonitor\` so the app can surface the migration on first launch. Tremoved-branch state (entries count) is preserved in the marker so the user sees exactly what was cleared. Failures surface as a `Migration Cleanup Warning` toast with the underlying diagnostic reason.
- **First-launch "Migration Complete" toast** — `App.OnStartup` reads + consumes the installer's marker after `MainWindow.Loaded` fires and displays a modal toast summarising the entries removed. The marker is deleted after consumption so the toast never reappears. Clean installs on a brand-new machine (no legacy dir) deliberately do NOT produce a toast — the absent branch is silent because there's nothing to migrate.
- New `scripts/cleanup-adchecklogs.ps1` — reusable parameterised PowerShell helper (`-Path`, `-Force`, `-DryRun`, `-Json`) that performs the same legacy-dir cleanup identically across dev machines, test boxes, and CI artifacts. Includes NTFS junction/symlink detection (refuses unless `-Force`) and CI-friendly exit codes: `0` = ok, `1` = refused/Remove-Item threw, `2` = partial (Defender mid-scan). `-DryRun -Json` emits a single-line JSON payload suitable for log capture.
- Backwards-compatible: a failed `MigrationMarker.json` write during install is non-fatal — the install still completes, only the toast is suppressed.
- [Full release notes on GitHub →](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.27)
- 637 total tests, all passing at release time

### v2.0.26
- **Installer fix: universal locked-file rename handler** replaces the v2.0.25 clrjit.dll-only workaround. An older previously-working installer also produced `Access is denied` errors on some user machines — the root cause is environmental (Defender Controlled Folder Access, Smart App Control, antivirus file locks) and can affect ANY `*.dll`/`*.exe` in `{app}`, not just `clrjit.dll`. New behaviour:
  - `PreHandleLockedFiles` in `[Code]` recursively scans `{app}\*\*` for every `*.dll`/`*.exe`, attempting `DeleteFile` on each. On error, the file is `RenameFile`'d to `<file>.deleteme` (Windows always allows rename of mmapped/locked files) and `MoveFileExW(..., MOVEFILE_DELAY_UNTIL_REBOOT)` queues it for removal at next boot via the existing `__cleanup_pending_reboot__` sentinel under `{app}\`.
  - `SetupLogging=yes` enabled so the user can open `%TEMP%\Setup Log YYYY-MM-DD #NNN.txt` and grep `PreHandleLockedFiles` to see exactly which file was renamed (or which rename failed) — required to diagnose environmental locks the installer can't auto-resolve.
  - All paths previously hardcoded to `C:\ADCheckLogs` moved to `{commonappdata}\AdHealthMonitor\Logs` (= `C:\ProgramData\AdHealthMonitor\Logs`) so the installer stops creating a system-root path that AV/Defender Anti-Ransomware routinely lock. Runtime constant `App.LogDirectoryPath` now resolves to the same `SpecialFolder.CommonApplicationData` path so install-at-boot and run-at-runtime produce identical log locations.
- Backwards compatibility: pre-v2.0.26 log data in `C:\ADCheckLogs` is preserved on disk but no longer referenced. New runs write to `%ProgramData%\AdHealthMonitor\Logs`. Old log dirs can be deleted manually.
- [Full release notes on GitHub →](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.26)

### v2.0.25
- **Installer fix: `DeleteFile failed; code 5. Access is denied.`** on memory-mapped `clrjit.dll` during self-updates against a running install
  - `restartreplace` flag was insufficient — Inno Setup only silently queues for `ERROR_SHARING_VIOLATION` (code 32), not for `ERROR_ACCESS_DENIED` (code 5) which memory-mapped DLLs return
  - New Pascal Script `PreHandleLockedFiles` (in `[Code]` section) runs at the top of `CurStepChanged(ssInstall)`, *after* Restart Manager attempts to close AD Guardian but *before* Inno's `[Files]` copy: tries `DeleteFile`, falls back to `RenameFile(clrjit.dll → clrjit.dll.deleteme)` (Windows allows rename of mmapped files), then `MoveFileExW(.deleteme, __cleanup_pending_reboot__\.deleteme, MOVEFILE_DELAY_UNTIL_REBOOT)` to schedule the rename at next reboot
  - Non-empty destination path is used deliberately — Inno's Pascal marshalling of empty-string `String` to stdcall passes a `PWideChar` to `L""`, not a NULL pointer, so relying on the documented NULL-destination delete behavior would risk silent failure
  - Sets `LockedFilesNeedReboot := True` on rename success so Inno prompts for a reboot; surfaces a `MsgBox` with actionable guidance if even the rename fails
- **CI: branch-protection required status check retargeted** at the deterministic `build-and-test` job (was pointing at `Required perf tests (LinqOptimizationBenchmarks + PerformanceTests)` which wasn't actually emitted on every push, producing a "Bypassed rule violations" warning every commit)
- Subscribers receive a normal GitHub auto-update notification from the new `v2.0.25` tag — canonical release flow when a real bug fix lands in a shipped release
- [Full release notes on GitHub →](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.25)

### v2.0.24
- "View Changelog on GitHub" link button replaces the inline markdown toggle in the Update window
- New `scripts/ReleaseNotes.psm1` module generates natural-tone release notes for the GitHub release body
- 15 xUnit tests added for the release-notes generator (highlights, boot-commit filter, no-prior-tag fallback, MaxBullets validation)
- 4 WPF STA E2E tests added for the changelog button
- [Full release notes on GitHub →](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.24)
- 596 total tests, all passing at release time

### v2.0.7
- Fixed false positive failures in optional diagnostic checks (TimeSkew, LDAP Bind, SMB/LDAP Signing)
- Defaulted optional diagnostic checks (DNS, TimeSkew, LDAP Bind, Cert/DHCP, SMB Signing) to off — old behaviour (dcdiag + repadmin only) restored out of the box
- Added per-test breakdown table to failure emails so users can see which checks failed without opening the attachment
- 254 total tests, all passing

### v2.0.6
- Partial class refactoring: MainWindow.xaml.cs (4912 lines) split into 9 focused partial class files
- Added DiagnosticsPipelineTests, LogsFilterTests, FindingsLogicTests, HistoryLogicTests
- Added GitHub Actions CI pipeline (build + test on windows-latest with .NET 9 SDK)
- 254 total tests (up from 52)

### v2.0.5
- Fixed log page crash during test runs (isLogContentReady tracking flag)
- Fixed "No log files found" error — now shows friendly "Logs Still Loading" message
- Added loading indicator in Logs tab when logs are still being generated

### v2.0.4
- Version bump maintenance release

### v2.0.3
- Version bump maintenance release

### v2.0.2
- Back arrow icons on Findings and Logs "Back to Health" navigation buttons
- Added xUnit test project with 43 tests (data models + SQLite persistence)
- Fixed lazy tab build errors (40 event handlers changed from private to internal)
- Removed bogus True method from SettingsTabPage (script generation artifact)
- Locale-safe ScheduledTask.ToString() test

### v2.0.1
- Startup performance: ReadyToRun pre-JIT, UAC removed from launch, 800ms delay eliminated
- Lazy-loaded 8 of 9 tab pages for faster startup
- Admin detection with on-demand elevation and warning banner
- Auto-elevate scheduled task launches as safety net

## Getting Started

### Prerequisites

- **Windows 10 or later**
- **.NET 9.0** (included in self-contained builds)
- Basic familiarity with Active Directory, **dcdiag**, and **repadmin**

### Installation

1. **Download the installer** for [v2.0.27](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.27) (or grab the [latest release](https://github.com/VBCDR/AD-Guardian/releases/latest) for the most recent build)

   — or —

2. **Clone the repository:**

   ```bash
   git clone https://github.com/VBCDR/AD-Guardian.git


## Continuous Integration

Builds and tests run on every push to `master` and every pull request via
GitHub Actions (`.github/workflows/build-and-test.yml`).The `build-and-test` job
is the required status check on `master`.
