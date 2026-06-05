# AD Guardian

Welcome to **AD Guardian** — a tool built to monitor and test Active Directory health in a smart, automated, and user-friendly way. It simplifies routine AD checks, schedules tests, and sends detailed email notifications so you can always stay on top of your environment.

**Current Version: 2.0.6** | [Latest Release](https://github.com/VBCDR/AD-Guardian/releases/latest)

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

### v2.0.6
- Added 9 new edge case unit tests (special characters, DateTime preservation, concurrent access)
- Improved EnsureLogsTab null-safety for lazy tab initialization
- Removed defensive try-catch in DisplayTestResults in favour of fixing root cause

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

1. **Download the installer** from the [latest release](https://github.com/VBCDR/AD-Guardian/releases/latest)

   — or —

2. **Clone the repository:**

   ```bash
   git clone https://github.com/VBCDR/AD-Guardian.git
