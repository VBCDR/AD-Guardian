## AD Guardian v2.0.18

### New Features
- **Update prompt changelog** — the update available modal now includes a collapsible "View Changes" button that shows the GitHub release notes, so you can see what's new before updating.

### Improvements
- **Health Score tooltip** — hover the info icon next to the Health Score on the Health tab to see exactly how the score is calculated (60% current pass rate + 40% trend, with severity-weighted findings penalty).
- **Settings tab guidance** — each optional test checkbox now shows the exact command it runs (e.g. "Runs: nslookup <DC>"), and a blue guidance box explains which settings match manual dcdiag/repadmin commands.
- **Scheduled email detail** — scheduled test emails now include the same detailed per-DC test result table as manual run emails, so you can see results for all domain controllers.
- **repadmin /replsummary** — replication summary output is now appended to the combined log after each run (manual and scheduled), available for reference in the Logs tab.

### Bug Fixes
- **Improved log error messages** — clicking "View Log" on a result with a missing log file now shows a specific, helpful message explaining why (previous session, auto-cleanup after 14 days, etc.) instead of a generic alarm.
- **Findings tab log errors** — the "Open Related Log" action now gives three distinct, actionable messages instead of a single unhelpful error.
- **Markdown parsing** — the update prompt changelog parser now handles HTML tags and blockquotes that GitHub release notes commonly contain.

### Technical
- All 570 unit tests passing.
- Release build: 0 errors.
