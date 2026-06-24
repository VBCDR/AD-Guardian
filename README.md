# AD Guardian

Welcome to **AD Guardian** — a tool built to monitor and test Active Directory health in a smart, automated, and user-friendly way. It simplifies routine AD checks, schedules tests, and sends detailed email notifications so you can always stay on top of your environment.

**Current Version: [v2.0.24](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.24)** | [Latest Release](https://github.com/VBCDR/AD-Guardian/releases/latest)

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

1. **Download the installer** for [v2.0.24](https://github.com/VBCDR/AD-Guardian/releases/tag/v2.0.24) (or grab the [latest release](https://github.com/VBCDR/AD-Guardian/releases/latest) for the most recent build)

   — or —

2. **Clone the repository:**

   ```bash
   git clone https://github.com/VBCDR/AD-Guardian.git


## Continuous Integration

Builds and tests run on every push to `master` and every pull request via
GitHub Actions (`.github/workflows/build-and-test.yml`). The
`Required perf tests (LinqOptimizationBenchmarks + PerformanceTests)` job
is the required status check on `master`.
