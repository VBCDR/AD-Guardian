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
/// and app (C#, Newtonsoft.Json). Bump <see cref="CurrentSchemaVersion"/> only
/// if the on-disk shape changes; the parser deliberately leaves unknown future
/// markers on disk so the user can inspect them by hand.
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
    /// True for status values that correspond to a meaningful cleanup event
    /// ("removed", "partial", "failed") -- these warrant a user-visible toast.
    /// False for "absent" and any unknown value -- clean installs should
    /// never produce a Migration Complete toast.
    /// </summary>
    public bool IsSignificantForToast =>
        string.Equals(CleanupStatus, "removed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CleanupStatus, "partial", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CleanupStatus, "failed", StringComparison.OrdinalIgnoreCase);

    public string ToToastTitle() => CleanupStatus switch
    {
        "removed" => "Migration Complete",
        "partial" => "Migration Partially Complete",
        "failed" => "Migration Cleanup Warning",
        _ => "Migration Checked"
    };

    public string ToToastBody() => CleanupStatus switch
    {
        "removed" =>
            $"Removed {EntriesRemoved} top-level entries from C:\\ADCheckLogs.\n\n" +
            $"New logs are now written to %ProgramData%\\AdHealthMonitor\\Logs.",
        "partial" =>
            $"Some entries in C:\\ADCheckLogs could not be removed -- Defender Controlled Folder Access or third-party AV may be holding them.\n\n" +
            $"{EntriesRemoved} top-level entries cleared; remaining files still on disk.\n\n" +
            $"New logs continue writing to %ProgramData%\\AdHealthMonitor\\Logs.\n\n" +
            $"Reason: {(string.IsNullOrEmpty(Reason) ? "(none provided)" : Reason)}",
        "failed" =>
            $"Migration tried to clean up C:\\ADCheckLogs but could not remove the directory.\n\n" +
            $"Some pre-v2.0.26 ad-hoc test logs may still be on disk; see the installer's Setup Log (%TEMP%\\Setup Log YYYY-MM-DD #NNN.txt) for details.\n\n" +
            $"Reason: {(string.IsNullOrEmpty(Reason) ? "(none provided)" : Reason)}",
        _ =>
            $"Migration marker ignored (status: {CleanupStatus})."
    };

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
