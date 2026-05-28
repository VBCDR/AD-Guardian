# Domain Guardian Bug List

## Fixed in this pass

- Main dashboard layout used overlapping fixed-position controls and wasted a large amount of space. The interface has been rebuilt into a structured action-first dashboard.
- About dialog referenced absolute asset paths under `C:\Users\crogers\ADCheckUtility\...`, which would break on other machines. Those machine-specific dependencies were removed from the UI.
- Custom popup and modal windows had inconsistent layout and missing close affordances. The dialogs now use a consistent card layout and explicit close actions.
- Scheduler time entry accepted arbitrary strings and failed later during task creation. It now validates `HH:mm` before saving.

## Still needs follow-up

- `MainWindow.xaml.cs`: logs are still written to `C:\ADCheckLogs`, which can fail under restricted permissions and should move to `%AppData%` or `%ProgramData%`.
- `MainWindow.xaml.cs`: SMTP credentials and server configuration are hard-coded in code. This is both a security issue and an operational risk.
- `MainWindow.xaml.cs`: `AutoUpdateAsync()` swallows exceptions silently on startup, which makes update failures invisible.
- `TaskSchedulerWindow.xaml.cs`: hourly repetition uses a one-day repetition duration, which may not match the intended forever-hourly behavior.
- `TaskSchedulerWindow.xaml.cs`: weekly scheduling does not expose a weekday choice in the UI.
- `MainWindow.xaml.cs`: `ParseDCDiagOutput()` relies on loose string matching for `passed` and `failed`, which is brittle.
- `MainWindow.xaml.cs`: command execution shells through `cmd.exe /C` with string composition. Domain controller inputs should be sanitized or executed without shell composition.
