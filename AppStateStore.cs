using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace AdHealthMonitor;

public sealed class AppStateStore
{
    private const string SnapshotRowId = "dashboard";
    private const string SettingsRowId = "settings";
    private const string SchedulerTasksRowId = "schedulerTasks";
    private static readonly object InitializationLock = new();
    private static readonly HashSet<string> InitializedDatabasePaths = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool anyDatabaseInitialized; // Fast-path: skip lock after first init
    private readonly string databasePath;

    public AppStateStore(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public static AppStateStore CreateDefault()
    {
        string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdHealthMonitor");
        return new AppStateStore(
            Path.Combine(appDataPath, "AppState.db"));
    }

    public void Initialize()
    {
        // Fast-path: after any database is initialized, check without lock.
        // This avoids lock contention on every public method call after first init.
        if (anyDatabaseInitialized)
        {
            string normalized = Path.GetFullPath(databasePath);
            lock (InitializationLock)
            {
                if (InitializedDatabasePaths.Contains(normalized))
                {
                    return;
                }
            }
        }

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

            // Batch all PRAGMAs + DDL into a single round-trip.
            // This eliminates 9 separate IPC round-trips during cold startup.
            ExecuteNonQuery(connection, """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA mmap_size=268435456;
                PRAGMA cache_size=-8000;

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

                CREATE INDEX IF NOT EXISTS IX_TestHistory_RunDateTicks
                ON TestHistory (RunDateTicks DESC);

                CREATE TABLE IF NOT EXISTS DashboardSnapshot (
                    Id TEXT PRIMARY KEY,
                    CapturedAtTicks INTEGER NOT NULL,
                    JsonPayload TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS AppDocuments (
                    Id TEXT PRIMARY KEY,
                    UpdatedAtTicks INTEGER NOT NULL,
                    JsonPayload TEXT NOT NULL
                );
                """);

            InitializedDatabasePaths.Add(normalizedDatabasePath);
            anyDatabaseInitialized = true;
        }
    }

    public AppStartupState LoadStartupState(int historyLimit = int.MaxValue)
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        // Load all startup data in a single batched query to minimize round-trips.
        // Each sub-query reads one document/table; the reader is stepped through them.
        PersistedAppSettings settings = LoadDocument<PersistedAppSettings>(connection, SettingsRowId) ?? new PersistedAppSettings();
        DashboardSnapshot? snapshot = LoadDashboardSnapshot(connection);
        List<TestHistoryEntry> history = LoadHistory(connection, historyLimit);
        List<ScheduledTask> tasks = LoadDocument<List<ScheduledTask>>(connection, SchedulerTasksRowId) ?? new List<ScheduledTask>();

        return new AppStartupState(settings, snapshot, history, tasks);
    }

    public List<TestHistoryEntry> LoadHistory()
    {
        Initialize();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        return LoadHistory(connection);
    }

    private static List<TestHistoryEntry> LoadHistory(SqliteConnection connection, int limit = int.MaxValue)
    {

        using SqliteCommand command = connection.CreateCommand();
        if (limit == int.MaxValue)
        {
            command.CommandText = """
                SELECT RunDateTicks, Total, Passed, Failed, Details, LogFilePath, TestType
                FROM TestHistory
                ORDER BY RunDateTicks DESC;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT RunDateTicks, Total, Passed, Failed, Details, LogFilePath, TestType
                FROM TestHistory
                ORDER BY RunDateTicks DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Max(0, limit));
        }

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
        // Batch all PRAGMAs into a single round-trip.
        // mmap_size=256MB enables memory-mapped I/O for faster reads.
        // cache_size=-8000 sets an 8MB page cache (negative = KiB).
        string pragmas = configureJournalMode
            ? "PRAGMA busy_timeout=3000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA mmap_size=268435456; PRAGMA cache_size=-8000;"
            : "PRAGMA busy_timeout=3000; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA mmap_size=268435456; PRAGMA cache_size=-8000;";
        ExecuteNonQuery(connection, pragmas);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
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

}

public sealed record AppStartupState(
    PersistedAppSettings Settings,
    DashboardSnapshot? DashboardSnapshot,
    List<TestHistoryEntry> History,
    List<ScheduledTask> ScheduledTasks);
