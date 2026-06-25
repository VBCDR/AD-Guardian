using System;
using System.IO;
using Newtonsoft.Json;

namespace AdHealthMonitor;

/// <summary>
/// Migration marker payload persisted by the installer's post-install cleanup
/// step (see <c>installer/AD Guardian Installer.iss::CleanupLegacyAdCheckLogs</c>)
/// and read once by <c>App.OnStartup</c> on the first launch after the upgrade.
/// After the app reads and displays the toast, the marker file is deleted so
/// the toast never reappears on subsequent launches.
///
/// Schema is hard-coded between installer (Pascal Script, hand-rolled JSON)
/// and app (C#, Newtonsoft.Json). The on-disk shape is deliberately extensible
/// via OPTIONAL additive fields (schemaVersion stays at <see cref="CurrentSchemaVersion"/>)
/// so the installer's Pascal writer doesn't break when the C# reader gains
/// new fields. Bump <see cref="CurrentSchemaVersion"/> ONLY if a structural
/// shape change is required; otherwise prefer adding a new optional property
/// here AND a new optional line in the Pascal WriteMigrationMarker.
/// </summary>
internal sealed class MigrationMarker
{
    public const int CurrentSchemaVersion = 1;

    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonProperty("cleanupStatus")]
    public string CleanupStatus { get; set; } = "absent";

    [JsonProperty("entriesRemoved")]
    public int EntriesRemoved { get; set; }

    [JsonProperty("installTime")]
    public string InstallTime { get; set; } = string.Empty;

    [JsonProperty("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Number of legacy files copied into <see cref="DestinationRoot"/> BEFORE
    /// the legacy directory was removed. Zero on absent/failed paths. Always
    /// present in v1+ markers but possibly 0 (e.g., legacy dir contained only
    /// disallowed extensions or copies were all refused).
    /// </summary>
    [JsonProperty("entriesMigrated")]
    public int EntriesMigrated { get; set; }

    /// <summary>
    /// Root of the destination subtree, e.g.
    /// <c>C:\ProgramData\AdHealthMonitor\Logs\legacy-import-20260625-153045\</c>.
    /// Empty/absent if no files were migrated (the marker writer always emits
    /// this field so the consumer can answer "where did the legacy data go?"
    /// even when the count is zero).
    /// </summary>
    [JsonProperty("destinationRoot")]
    public string DestinationRoot { get; set; } = string.Empty;

    /// <summary>
    /// Number of legacy files whose destination name collided and were renamed
    /// with a timestamp suffix to avoid overwriting an existing file.
    /// Surfaced in the migration toast so the user knows nothing was clobbered.
    /// </summary>
    [JsonProperty("entriesCollisions")]
    public int EntriesCollisions { get; set; }

    /// <summary>
    /// Number of file copies that failed (Defender / AV lock, ACL, sharing
    /// violation). Never triggers a partial removal on its own — but combined
    /// with an EntriesRemoved &lt; expected count it produces a partial toast.
    /// </summary>
    [JsonProperty("errorCount")]
    public int ErrorCount { get; set; }

    /// <summary>
    /// Total size in bytes of all files copied into <see cref="DestinationRoot"/>.
    /// Summation done at write time by the installer's Pascal
    /// <c>MigrateLegacyLogsTo</c> walker (which iterates FindFirst/FindNext per
    /// file, so the per-file size is free), not at C# read time. Falls back
    /// to <see cref="ComputeBytesMigratedFromDisk"/> on the UI thread when
    /// the installer version pre-dates the byte-counting feature (older
    /// markers will deserialize to 0 here). Zero on absent / no-files paths.
    /// </summary>
    [JsonProperty("bytesMigrated")]
    public long BytesMigrated { get; set; }

    /// <summary>
    /// True for status values that correspond to a meaningful cleanup event
    /// ("removed", "partial", "failed") -- these warrant a user-visible toast.
    /// False for "absent" and any unknown value -- clean installs should
    /// never produce a Migration Complete toast.
    /// </summary>
    public bool IsSignificantForToast =>
        string.Equals(CleanupStatus, "removed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CleanupStatus, "partial", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CleanupStatus, "failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Auto-dismiss countdown for the success path. <c>removed</c> cleanups
    /// are purely informational so we close after 8 seconds if the user
    /// doesn't click OK. <c>partial</c> / <c>failed</c> require explicit user
    /// acknowledgement (returns 0 = manual dismiss) so the failure detail is
    /// not lost behind an auto-closing dialog.
    ///
    /// Driven by the marker (data) rather than the UI service so the dismiss
    /// policy stays in lockstep with the cleanup-status policy above.
    /// </summary>
    public int AutoDismissSeconds
    {
        get
        {
            if (!string.Equals(CleanupStatus, "removed", StringComparison.OrdinalIgnoreCase))
                return 0;
            // Removed + zero data is rare but possible (empty legacy dir, or
            // only-disallowed-extensions at the legacy path). Avoid auto-
            // dismiss in this case so the user actually reads "no files were
            // found" -- which would otherwise be invisible behind a dialog
            // that closes itself in 8 seconds.
            return BytesMigrated > 0 || EntriesMigrated > 0 || EntriesRemoved > 0
                ? 8
                : 0;
        }
    }

    public string ToToastTitle() => CleanupStatus switch
    {
        "removed" => "Migration Complete",
        "partial" => "Migration Partially Complete",
        "failed" => "Migration Cleanup Warning",
        _ => "Migration Checked"
    };

    public string ToToastBody()
    {
        string safeDestination = string.IsNullOrEmpty(DestinationRoot)
            ? "%ProgramData%\\AdHealthMonitor\\Logs\\legacy-import-*"
            : DestinationRoot;

        // Lead-in summary: a single user-facing headline sentence covering how
        // much legacy data was migrated. Surfaced only when the marker carries
        // a non-zero byte count -- otherwise no legacy data existed and the
        // status-specific detail paragraphs below speak for themselves.
        //
        // Per-status phrasing avoids the misleading "We cleaned up X" framing
        // for partial/failed (where cleanup actually didn't complete).
        string leadIn = BytesMigrated > 0
            ? CleanupStatus switch
            {
                "removed" => $"We cleaned up {FormatUnit(BytesMigrated)} of legacy logs.\n\n",
                "partial" => $"We migrated {FormatUnit(BytesMigrated)} of legacy logs before cleanup stopped at the legacy path.\n\n",
                "failed"  => $"We migrated {FormatUnit(BytesMigrated)} of legacy logs but C:\\ADCheckLogs could not be removed.\n\n",
                _ => string.Empty,
            }
            : string.Empty;

        return CleanupStatus switch
        {
            "removed" =>
                leadIn +
                // When the byte lead-in already conveys size, drop the legacy
                // "Cleared N entries" detail -- it just repeats the magnitude
                // and a bystander sees three separately-stated size numbers
                // (MB / N files / N entries) which is more operator-debug than
                // user-friendly. Empty-removed (BytesMigrated == 0) keeps the
                // detail as the only size information available.
                (BytesMigrated > 0
                    ? string.Empty
                    : $"Cleared {EntriesRemoved} top-level entries from C:\\ADCheckLogs.\n\n") +
                (EntriesMigrated > 0
                    ? $"Copied {EntriesMigrated} legacy log file{(EntriesMigrated == 1 ? "" : "s")} to:\n{safeDestination}\n\n"
                    : "No *.log/*.txt/*.json/*.csv files were found to copy.\n\n") +
                (EntriesCollisions > 0
                    ? $"{EntriesCollisions} file{(EntriesCollisions == 1 ? "" : "s")} collided with existing data and were renamed with a timestamp suffix.\n\n"
                    : string.Empty) +
                "Any new runs will write to %ProgramData%\\AdHealthMonitor\\Logs.",
            "partial" =>
                leadIn +
                $"Partially cleared C:\\ADCheckLogs: {EntriesRemoved} top-level entries removed; some files remained on disk.\n\n" +
                (EntriesMigrated > 0
                    ? $"Copied {EntriesMigrated} legacy log file{(EntriesMigrated == 1 ? "" : "s")} to:\n{safeDestination}\n\n"
                    : string.Empty) +
                (ErrorCount > 0
                    ? $"{ErrorCount} file copy failure{(ErrorCount == 1 ? "" : "s")} (Defender / AV lock or ACL).\n\n"
                    : string.Empty) +
                (string.IsNullOrEmpty(Reason) ? "(no diagnostic detail captured)" : $"Reason: {Reason}"),
            "failed" =>
                leadIn +
                $"Migration tried to clean up C:\\ADCheckLogs but could not remove the directory.\n\n" +
                (EntriesMigrated > 0
                    ? $"Copied {EntriesMigrated} legacy log file{(EntriesMigrated == 1 ? "" : "s")} to:\n{safeDestination}\n\n"
                    : string.Empty) +
                "Some pre-v2.0.26 ad-hoc test logs may still be on disk; see the installer's Setup Log (%TEMP%\\Setup Log YYYY-MM-DD #NNN.txt) for details.\n\n" +
                (string.IsNullOrEmpty(Reason) ? "Reason: (none provided)" : $"Reason: {Reason}"),
            _ =>
                $"Migration marker ignored (status: {CleanupStatus})."
        };
    }

    /// <summary>
    /// Default marker file path: <c>%ProgramData%\AdHealthMonitor\MigrationMarker.json</c>.
    /// The installer writes the file at the same location using
    /// <c>ExpandConstant('{commonappdata}')</c>, so the two resolve to the
    /// same path on the same machine even when folder redirection is in play.
    /// </summary>
    public static string DefaultMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AdHealthMonitor",
        "MigrationMarker.json");

    /// <summary>
    /// User-facing byte-size formatting for the migration toast lead-in.
    /// Rounds up to the largest natural unit; uses binary (1024) divisors
    /// because the on-disk byte counts come straight from raw
    /// <c>FileInfo.Length</c> / Pascal <c>Rec.Size</c> -- no conversion step
    /// where SI (1000) would feel more consistent.
    /// </summary>
    /// <remarks>
    /// Boundary cases:
    /// <list type="bullet">
    /// <item>0 bytes -> "0 bytes"</item>
    /// <item>1 byte -> "1 byte" (singular)</item>
    /// <item>2..1023 bytes -> "N bytes" (plural)</item>
    /// <item>1024..1048575 bytes -> "N KB" (integer; the KB range is wide enough that decimal precision adds noise)</item>
    /// <item>1 MiB..1023 MiB -> "N.# MB" (one decimal place)</item>
    /// <item>1 GiB+ -> "N.## GB" (two decimal places)</item>
    /// </list>
    /// Negative input is coerced to 0 (defensive against corrupt hand-rolled
    /// payloads rather than wrapped/unknown).
    /// </remarks>
    public static string FormatUnit(long bytes)
    {
        if (bytes <= 0) return "0 bytes";
        if (bytes == 1) return "1 byte";
        if (bytes < 1024) return $"{bytes} bytes";

        long kib = bytes / 1024;
        if (kib < 1024) return $"{kib} KB";

        double mib = bytes / (1024.0 * 1024.0);
        if (mib < 1024.0) return $"{mib:0.#} MB";

        double gib = bytes / (1024.0 * 1024.0 * 1024.0);
        return $"{gib:0.##} GB";
    }

    /// <summary>
    /// Read-only walk of <paramref name="destinationRoot"/> that sums
    /// <see cref="FileInfo.Length"/> for every <c>*.log</c> / <c>*.txt</c> /
    /// <c>*.json</c> / <c>*.csv</c> file (the installer's allowlist). Used as
    /// a defensive fallback when the marker carries <c>BytesMigrated == 0</c>
    /// -- i.e., the marker was written by an installer version that pre-dated
    /// the byte-summation feature in <c>MigrateLegacyLogsTo</c>. Caps both
    /// file count and wall-clock time so first-launch is never starved by a
    /// sluggish redirected volume or an unexpectedly deep tree.
    /// </summary>
    /// <remarks>
    /// Limits (defaults shown):
    /// <list type="bullet">
    /// <item><c>timeoutMs</c>: 500 ms wall-clock cap (Stopwatch). Realistic
    ///   ceiling for a one-time enumeration of a freshly-written log tree;
    ///   far below any UX "this app froze" perception threshold.</item>
    /// <item><c>maxFiles</c>: 5,000 files. The legacy dir pre-v2.0.26 was a
    ///   low-volume write target; 5K is orders of magnitude higher than any
    ///   realistic operator dump.</item>
    /// </list>
    /// Failures (Defender CFA / AV lock / IO instability on a sub-file):
    /// catch and continue. Top-level catch returns 0 -- we'd rather understate
    /// than produce a misleading number.
    /// </remarks>
    public static long ComputeBytesMigratedFromDisk(string? destinationRoot, int timeoutMs = 500, int maxFiles = 5000)
    {
        if (string.IsNullOrEmpty(destinationRoot) || !Directory.Exists(destinationRoot))
            return 0;

        // Mirror the installer's allowlist (HasAllowedLegacyExt in installer.iss):
        // only ever consider log-shaped files. AV caches, CrashDumps, etc.
        // dropped into the legacy path are deliberately excluded.
        string[] allowedExts = { ".log", ".txt", ".json", ".csv" };

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int visited = 0;
        long total = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(destinationRoot, "*", options))
            {
                if (visited >= maxFiles || sw.ElapsedMilliseconds > timeoutMs)
                    break;
                visited++;

                string ext = Path.GetExtension(file);
                if (ext == null) continue;
                ext = ext.ToLowerInvariant();
                bool allowed = false;
                for (int i = 0; i < allowedExts.Length; i++)
                {
                    if (ext == allowedExts[i]) { allowed = true; break; }
                }
                if (!allowed) continue;

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (UnauthorizedAccessException) { /* per-file Defender lock -- skip */ }
                catch (IOException) { /* per-file transient IO -- skip */ }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return 0; // top-level ACL blocked the walk -- accept 0 rather than miscalculate
        }
        catch (IOException)
        {
            return 0; // top-level IO failure -- accept 0
        }

        return total;
    }

    /// <summary>
    /// Reads the marker file, validates schema, deletes the consumed marker on
    /// success. Returns null on any failure -- the marker is NEVER partially
    /// consumed, so a failed read leaves it on disk for forensic inspection.
    ///
    /// Failure modes (all return null, all leave the file on disk where
    /// applicable so the next launch can re-attempt):
    ///   <list type="bullet">
    ///     <item>File does not exist -> silent skip.</item>
    ///     <item>ReadAllText throws <see cref="UnauthorizedAccessException"/>
    ///           (Defender Controlled Folder Access / AV lock) -> leave file.</item>
    ///     <item>ReadAllText throws <see cref="IOException"/> (sharing
    ///           violation / partial write) -> leave file.</item>
    ///     <item>Newtonsoft.Json throws (corrupted payload) -> leave file.</item>
    ///     <item>SchemaVersion out-of-range (zero or future) -> leave file.</item>
    ///     <item>CleanupStatus is null or empty -> leave file.</item>
    ///     <item>Deserialization returned null (literal "null" payload) -> leave file.</item>
    ///   </list>
    ///
    /// Best-effort delete: if <see cref="File.Delete"/> throws after a
    /// successful read (Defender locked the file mid-read), the marker is
    /// returned to the caller anyway -- the toast will surface this launch
    /// and reappear on the next launch until Defender unblocks. Deliberate
    /// robustness over idempotence so stubborn defence stacks still get the
    /// migration message.
    /// </summary>
    public static MigrationMarker? TryReadAndDelete(string? overridePath = null)
    {
        string markerPath = overridePath ?? DefaultMarkerPath;
        try
        {
            if (!File.Exists(markerPath))
                return null;

            string json;
            try
            {
                json = File.ReadAllText(markerPath);
            }
            catch (UnauthorizedAccessException)
            {
                // Defender / CFA / AV lock -- leave for forensics, do not probe further.
                return null;
            }
            catch (IOException)
            {
                // Sharing violation / partial write -- leave for forensics.
                return null;
            }

            MigrationMarker? marker;
            try
            {
                marker = JsonConvert.DeserializeObject<MigrationMarker>(json);
            }
            catch (JsonException)
            {
                // Corrupted payload -- leave for forensics so the user can grep it.
                return null;
            }

            if (marker == null ||
                marker.SchemaVersion <= 0 ||
                marker.SchemaVersion > CurrentSchemaVersion ||
                string.IsNullOrEmpty(marker.CleanupStatus))
            {
                // Future schema or structurally zeroed-out payload -- leave for forensics.
                return null;
            }

            try
            {
                File.Delete(markerPath);
            }
            catch
            {
                // Best-effort. The toast will display this launch and reappear on
                // the next launch until Defender/CFA releases the lock. Acceptable
                // Easter egg of robustness over idempotence.
            }

            return marker;
        }
        catch
        {
            // Top-level safety net: migration lookups must NEVER crash startup.
            return null;
        }
    }
}
