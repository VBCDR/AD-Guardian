using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Domain_Guardian.Properties;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace AdHealthMonitor;

public sealed class AppStateStore
{
    private const string SnapshotRowId = "dashboard";
    private const string SettingsRowId = "settings";
    private const string SchedulerTasksRowId = "schedulerTasks";
    private const string LegacyMigrationRowId = "legacyMigration";
    private static readonly object InitializationLock = new();
    private static readonly HashSet<string> InitializedDatabasePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string databasePath;
    private readonly string legacyHistoryPath;
    private readonly string legacySnapshotPath;
    private readonly string legacySchedulerTasksPath;

    public AppStateStore(string databasePath, string legacyHistoryPath, string legacySnapshotPath, string legacySchedulerTasksPath)
    {
        this.databasePath = databasePath;
        this.legacyHistoryPath = legacyHistoryPath;
        this.legacySnapshotPath = legacySnapshotPath;
        this.legacySchedulerTasksPath = legacySchedulerTasksPath;
    }

    public static AppStateStore CreateDefault()
    {
        string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdHealthMonitor");
        return new AppStateStore(
            Path.Combine(appDataPath, "AppState.db"),
            Path.Combine(appDataPath, "TestHistory.xml"),
            Path.Combine(appDataPath, "DashboardSnapshot.json"),
            Path.Combine(appDataPath, "ScheduledTasks.xml"));
    }

    public void Initialize()
    {
        string normalizedDatabasePath = Path.GetFullPath(databasePath);
        lock (InitializationLock)
        {
            if (InitializedDatabasePaths.Contains(normalizedDatabasePath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(normalizedDatabasePath)!);

            using SqliteConnection connection = CreateConnection();
            connection.Open();
            ConfigureConnection(connection, configureJournalMode: true);

            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS TestHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunDateTicks INTEGER NOT NULL,
                    Total INTEGER NOT NULL,
                    Passed INTEGER NOT NULL,
                    Failed INTEGER NOT NULL,
                    Details TEXT NOT NULL,
                    LogFilePath TEXT NOT NULL,
                    TestType TEXT NOT NULL
                );
                """);

            ExecuteNonQuery(connection, """
                CREATE INDEX IF NOT EXISTS IX_TestHistory_RunDateTicks
                ON TestHistory (RunDateTicks DESC);
                """);

            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS DashboardSnapshot (
                    Id TEXT PRIMARY KEY,
                    CapturedAtTicks INTEGER NOT NULL,
                    JsonPayload TEXT NOT NULL
                );
                """);

            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS AppDocuments (
                    Id TEXT PRIMARY KEY,
                    UpdatedAtTicks INTEGER NOT NULL,
                    JsonPayload TEXT NOT NULL
                );
                """);

            if (!HasDocument(connection, LegacyMigrationRowId))
            {
                ImportLegacyHistoryIfNeeded(connection);
                ImportLegacySnapshotIfNeeded(connection);
                ImportLegacySettingsIfNeeded(connection);
                ImportLegacySchedulerTasksIfNeeded(connection);
                ArchiveLegacyStateFiles(connection);
                SaveDocument(connection, LegacyMigrationRowId, new { CompletedAtUtc = DateTime.UtcNow });
            }

            InitializedDatabasePaths.Add(normalizedDatabasePath);
        }
    }

    public AppStartupState LoadStartupState()
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        return new AppStartupState(
            LoadDocument<PersistedAppSettings>(connection, SettingsRowId) ?? new PersistedAppSettings(),
            LoadDashboardSnapshot(connection),
            LoadHistory(connection),
            LoadDocument<List<ScheduledTask>>(connection, SchedulerTasksRowId) ?? new List<ScheduledTask>());
    }

    public List<TestHistoryEntry> LoadHistory()
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        return LoadHistory(connection);
    }

    private static List<TestHistoryEntry> LoadHistory(SqliteConnection connection)
    {

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunDateTicks, Total, Passed, Failed, Details, LogFilePath, TestType
            FROM TestHistory
            ORDER BY RunDateTicks DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<TestHistoryEntry> items = new();
        while (reader.Read())
        {
            items.Add(new TestHistoryEntry
            {
                RunDate = new DateTime(reader.GetInt64(0), DateTimeKind.Local),
                Total = reader.GetInt32(1),
                Passed = reader.GetInt32(2),
                Failed = reader.GetInt32(3),
                Details = reader.GetString(4),
                LogFilePath = reader.GetString(5),
                TestType = reader.GetString(6)
            });
        }

        return items;
    }

    public void SaveHistory(IReadOnlyCollection<TestHistoryEntry> entries)
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        SaveHistory(connection, transaction, entries);
        transaction.Commit();
    }

    private static void SaveHistory(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<TestHistoryEntry> entries)
    {
        using (SqliteCommand deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM TestHistory;";
            deleteCommand.ExecuteNonQuery();
        }

        foreach (TestHistoryEntry entry in entries)
        {
            using SqliteCommand insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO TestHistory (RunDateTicks, Total, Passed, Failed, Details, LogFilePath, TestType)
                VALUES ($runDateTicks, $total, $passed, $failed, $details, $logFilePath, $testType);
                """;
            insertCommand.Parameters.AddWithValue("$runDateTicks", entry.RunDate.Ticks);
            insertCommand.Parameters.AddWithValue("$total", entry.Total);
            insertCommand.Parameters.AddWithValue("$passed", entry.Passed);
            insertCommand.Parameters.AddWithValue("$failed", entry.Failed);
            insertCommand.Parameters.AddWithValue("$details", entry.Details ?? string.Empty);
            insertCommand.Parameters.AddWithValue("$logFilePath", entry.LogFilePath ?? string.Empty);
            insertCommand.Parameters.AddWithValue("$testType", entry.TestType ?? string.Empty);
            insertCommand.ExecuteNonQuery();
        }
    }

    public DashboardSnapshot? LoadDashboardSnapshot()
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        return LoadDashboardSnapshot(connection);
    }

    private static DashboardSnapshot? LoadDashboardSnapshot(SqliteConnection connection)
    {

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT JsonPayload
            FROM DashboardSnapshot
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", SnapshotRowId);

        object? result = command.ExecuteScalar();
        if (result is not string payload || string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        return JsonConvert.DeserializeObject<DashboardSnapshot>(payload);
    }

    public void SaveDashboardSnapshot(DashboardSnapshot snapshot)
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        SaveDashboardSnapshot(connection, snapshot);
    }

    private static void SaveDashboardSnapshot(SqliteConnection connection, DashboardSnapshot snapshot)
    {
        string payload = JsonConvert.SerializeObject(snapshot, Formatting.None);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DashboardSnapshot (Id, CapturedAtTicks, JsonPayload)
            VALUES ($id, $capturedAtTicks, $jsonPayload)
            ON CONFLICT(Id) DO UPDATE SET
                CapturedAtTicks = excluded.CapturedAtTicks,
                JsonPayload = excluded.JsonPayload;
            """;
        command.Parameters.AddWithValue("$id", SnapshotRowId);
        command.Parameters.AddWithValue("$capturedAtTicks", snapshot.CapturedAtUtc.Ticks);
        command.Parameters.AddWithValue("$jsonPayload", payload);
        command.ExecuteNonQuery();
    }

    public PersistedAppSettings LoadSettings()
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);
        return LoadDocument<PersistedAppSettings>(connection, SettingsRowId) ?? new PersistedAppSettings();
    }

    public void SaveSettings(PersistedAppSettings settings)
    {
        SaveDocument(SettingsRowId, settings);
    }

    public List<ScheduledTask> LoadScheduledTasks()
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);
        return LoadDocument<List<ScheduledTask>>(connection, SchedulerTasksRowId) ?? new List<ScheduledTask>();
    }

    public void SaveScheduledTasks(IReadOnlyCollection<ScheduledTask> tasks)
    {
        SaveDocument(SchedulerTasksRowId, tasks.ToList());
    }

    private SqliteConnection CreateConnection()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };

        return new SqliteConnection(builder.ToString());
    }

    private static void ConfigureConnection(SqliteConnection connection, bool configureJournalMode = false)
    {
        ExecuteNonQuery(connection, "PRAGMA busy_timeout=3000;");
        if (configureJournalMode)
        {
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        }
        ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
        ExecuteNonQuery(connection, "PRAGMA temp_store=MEMORY;");
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private void ImportLegacyHistoryIfNeeded(SqliteConnection connection)
    {
        if (!File.Exists(legacyHistoryPath) || GetHistoryRowCount(connection) > 0)
        {
            return;
        }

        XmlSerializer serializer = new(typeof(List<TestHistoryEntry>));
        using StreamReader reader = new(legacyHistoryPath);
        List<TestHistoryEntry> importedEntries = (List<TestHistoryEntry>)(serializer.Deserialize(reader) ?? new List<TestHistoryEntry>());
        using SqliteTransaction transaction = connection.BeginTransaction();
        SaveHistory(connection, transaction, importedEntries.OrderByDescending(entry => entry.RunDate).ToList());
        transaction.Commit();
    }

    private void ImportLegacySnapshotIfNeeded(SqliteConnection connection)
    {
        if (!File.Exists(legacySnapshotPath) || HasDashboardSnapshot(connection))
        {
            return;
        }

        string json = File.ReadAllText(legacySnapshotPath);
        DashboardSnapshot? snapshot = JsonConvert.DeserializeObject<DashboardSnapshot>(json);
        if (snapshot != null)
        {
            SaveDashboardSnapshot(connection, snapshot);
        }
    }

    private void ImportLegacySettingsIfNeeded(SqliteConnection connection)
    {
        if (HasDocument(connection, SettingsRowId))
        {
            return;
        }

        SaveDocument(connection, SettingsRowId, new PersistedAppSettings
        {
            DomainControllers = Settings.Default.DomainControllers,
            RecipientEmail = Settings.Default.RecipientEmail,
            TestDnsCheck = Settings.Default.TestDnsCheck,
            TestReplication = Settings.Default.TestReplication,
            TestTimeSkew = Settings.Default.TestTimeSkew,
            TestLdapBind = Settings.Default.TestLdapBind,
            TestCertDhcp = Settings.Default.TestCertDhcp,
            TestSmbLdapSigning = Settings.Default.TestSmbLdapSigning,
            SendEmailManual = Settings.Default.SendEmailManual,
            SendEmailScheduled = Settings.Default.SendEmailScheduled
        });
    }

    private void ImportLegacySchedulerTasksIfNeeded(SqliteConnection connection)
    {
        if (!File.Exists(legacySchedulerTasksPath) || HasDocument(connection, SchedulerTasksRowId))
        {
            return;
        }

        XmlSerializer serializer = new(typeof(List<ScheduledTask>));
        using StreamReader reader = new(legacySchedulerTasksPath);
        List<ScheduledTask> importedTasks = (List<ScheduledTask>)(serializer.Deserialize(reader) ?? new List<ScheduledTask>());
        SaveDocument(connection, SchedulerTasksRowId, importedTasks);
    }

    private static long GetHistoryRowCount(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM TestHistory;";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static bool HasDashboardSnapshot(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM DashboardSnapshot
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", SnapshotRowId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool HasDocument(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM AppDocuments
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private T? LoadDocument<T>(string id) where T : class
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        return LoadDocument<T>(connection, id);
    }

    private static T? LoadDocument<T>(SqliteConnection connection, string id) where T : class
    {

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT JsonPayload
            FROM AppDocuments
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        object? result = command.ExecuteScalar();
        if (result is not string payload || string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        return JsonConvert.DeserializeObject<T>(payload);
    }

    private void SaveDocument<T>(string id, T document)
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        SaveDocument(connection, id, document);
    }

    private static void SaveDocument<T>(SqliteConnection connection, string id, T document)
    {
        string payload = JsonConvert.SerializeObject(document, Formatting.None);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppDocuments (Id, UpdatedAtTicks, JsonPayload)
            VALUES ($id, $updatedAtTicks, $jsonPayload)
            ON CONFLICT(Id) DO UPDATE SET
                UpdatedAtTicks = excluded.UpdatedAtTicks,
                JsonPayload = excluded.JsonPayload;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$updatedAtTicks", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("$jsonPayload", payload);
        command.ExecuteNonQuery();
    }

    private void ArchiveLegacyStateFiles(SqliteConnection connection)
    {
        bool hasHistory = GetHistoryRowCount(connection) > 0;
        bool hasSnapshot = HasDashboardSnapshot(connection);
        bool hasSchedulerTasks = HasDocument(connection, SchedulerTasksRowId);

        string backupRoot = Path.Combine(Path.GetDirectoryName(databasePath)!, "LegacyStateBackup");
        string timestampFolder = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        bool archivedAny = false;

        archivedAny |= ArchiveLegacyFileIfEligible(legacyHistoryPath, hasHistory, timestampFolder);
        archivedAny |= ArchiveLegacyFileIfEligible(legacySnapshotPath, hasSnapshot, timestampFolder);
        archivedAny |= ArchiveLegacyFileIfEligible(legacySchedulerTasksPath, hasSchedulerTasks, timestampFolder);

        if (archivedAny)
        {
            File.WriteAllText(
                Path.Combine(timestampFolder, "README.txt"),
                "These files were archived after successful migration to AppState.db. They are no longer used by the live app.");
        }
    }

    private static bool ArchiveLegacyFileIfEligible(string sourcePath, bool hasMigratedData, string archiveDirectory)
    {
        if (!hasMigratedData || !File.Exists(sourcePath))
        {
            return false;
        }

        Directory.CreateDirectory(archiveDirectory);
        string destinationPath = Path.Combine(archiveDirectory, Path.GetFileName(sourcePath));

        if (File.Exists(destinationPath))
        {
            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            destinationPath = Path.Combine(
                archiveDirectory,
                $"{fileName}_{DateTime.UtcNow:HHmmssfff}{extension}");
        }

        File.Move(sourcePath, destinationPath);
        return true;
    }
}

public sealed record AppStartupState(
    PersistedAppSettings Settings,
    DashboardSnapshot? DashboardSnapshot,
    List<TestHistoryEntry> History,
    List<ScheduledTask> ScheduledTasks);
