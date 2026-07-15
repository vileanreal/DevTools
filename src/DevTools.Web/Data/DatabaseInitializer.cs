using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Data;

public static class DatabaseInitializer
{
    private const int CurrentSchemaVersion = 1;
    private static readonly string[] ApplicationTables =
    [
        "LogSystems", "LogSources", "SqlDocSources", "SqlDocDiagrams", "ServiceSystems", "ManagedServices"
    ];

    public static void Initialize(DevToolsDbContext database)
    {
        var connection = database.Database.GetDbConnection();
        var existingDatabaseLength = GetExistingDatabaseLength(connection);

        // EnsureCreated only creates a brand-new schema. It never updates, drops, or replaces an existing database.
        database.Database.EnsureCreated();
        if (connection.State != ConnectionState.Open) connection.Open();

        Execute(connection, "PRAGMA foreign_keys=ON");
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=NORMAL");
        Execute(connection, "PRAGMA temp_store=MEMORY");
        Execute(connection, "PRAGMA busy_timeout=5000");

        var upgradeRequired = GetSchemaVersion(connection) < CurrentSchemaVersion || RequiresSchemaRepair(connection);
        if (upgradeRequired && existingDatabaseLength > 0 && ApplicationTables.Any(table => TableExists(connection, table)))
            CreatePreUpgradeBackup(connection);

        using var transaction = connection.BeginTransaction();
        try
        {
            CreateCurrentSchema(connection, transaction);
            AddMissingLogSourceColumns(connection, transaction);
            CreateCurrentIndexes(connection, transaction);
            SetSchemaVersion(connection, transaction, CurrentSchemaVersion);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void CreateCurrentSchema(DbConnection connection, DbTransaction transaction)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS LogSystems (
                Id INTEGER NOT NULL CONSTRAINT PK_LogSystems PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS LogSources (
                Id INTEGER NOT NULL CONSTRAINT PK_LogSources PRIMARY KEY AUTOINCREMENT,
                ModuleName TEXT NOT NULL,
                Path TEXT NOT NULL,
                Format TEXT NOT NULL DEFAULT 'auto',
                CustomPattern TEXT NULL,
                TimestampFormat TEXT NULL,
                IncludeSubdirectories INTEGER NOT NULL DEFAULT 1,
                LogSystemId INTEGER NOT NULL,
                CONSTRAINT FK_LogSources_LogSystems_LogSystemId FOREIGN KEY (LogSystemId) REFERENCES LogSystems (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS SqlDocSources (
                Id INTEGER NOT NULL CONSTRAINT PK_SqlDocSources PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Provider TEXT NOT NULL,
                ConnectionString TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS SqlDocDiagrams (
                Id INTEGER NOT NULL CONSTRAINT PK_SqlDocDiagrams PRIMARY KEY AUTOINCREMENT,
                SqlDocSourceId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                DiagramJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_SqlDocDiagrams_SqlDocSources_SqlDocSourceId FOREIGN KEY (SqlDocSourceId) REFERENCES SqlDocSources (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS ServiceSystems (
                Id INTEGER NOT NULL CONSTRAINT PK_ServiceSystems PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS ManagedServices (
                Id INTEGER NOT NULL CONSTRAINT PK_ManagedServices PRIMARY KEY AUTOINCREMENT,
                ServiceSystemId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Kind TEXT NOT NULL,
                TargetName TEXT NOT NULL,
                CONSTRAINT FK_ManagedServices_ServiceSystems_ServiceSystemId FOREIGN KEY (ServiceSystemId) REFERENCES ServiceSystems (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DevToolsSchema (
                Id INTEGER NOT NULL CONSTRAINT PK_DevToolsSchema PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            )
            """, transaction);
    }

    private static void AddMissingLogSourceColumns(DbConnection connection, DbTransaction transaction)
    {
        var columns = Columns(connection, transaction, "LogSources");
        if (!columns.Contains("Format")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN Format TEXT NOT NULL DEFAULT 'auto'", transaction);
        if (!columns.Contains("CustomPattern")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN CustomPattern TEXT NULL", transaction);
        if (!columns.Contains("TimestampFormat")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN TimestampFormat TEXT NULL", transaction);
        if (!columns.Contains("IncludeSubdirectories")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN IncludeSubdirectories INTEGER NOT NULL DEFAULT 1", transaction);
    }

    private static void CreateCurrentIndexes(DbConnection connection, DbTransaction transaction)
    {
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_LogSystems_Name ON LogSystems (Name)", transaction);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_LogSources_LogSystemId ON LogSources (LogSystemId)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_SqlDocSources_Name ON SqlDocSources (Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_SqlDocDiagrams_SqlDocSourceId_Name ON SqlDocDiagrams (SqlDocSourceId, Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ServiceSystems_Name ON ServiceSystems (Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ManagedServices_ServiceSystemId_Name ON ManagedServices (ServiceSystemId, Name)", transaction);
    }

    private static bool RequiresSchemaRepair(DbConnection connection)
    {
        if (ApplicationTables.Any(table => !TableExists(connection, table))) return true;
        var columns = Columns(connection, null, "LogSources");
        string[] requiredColumns = ["Format", "CustomPattern", "TimestampFormat", "IncludeSubdirectories"];
        return requiredColumns.Any(column => !columns.Contains(column));
    }

    private static int GetSchemaVersion(DbConnection connection)
    {
        if (!TableExists(connection, "DevToolsSchema")) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM DevToolsSchema WHERE Id = 1";
        return command.ExecuteScalar() is long version ? checked((int)version) : 0;
    }

    private static void SetSchemaVersion(DbConnection connection, DbTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO DevToolsSchema (Id, Version, UpdatedAtUtc) VALUES (1, $version, $updatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET Version = excluded.Version, UpdatedAtUtc = excluded.UpdatedAtUtc
            """;
        var versionParameter = command.CreateParameter();
        versionParameter.ParameterName = "$version";
        versionParameter.Value = version;
        command.Parameters.Add(versionParameter);
        var updatedParameter = command.CreateParameter();
        updatedParameter.ParameterName = "$updatedAtUtc";
        updatedParameter.Value = DateTime.UtcNow.ToString("O");
        command.Parameters.Add(updatedParameter);
        command.ExecuteNonQuery();
    }

    private static void CreatePreUpgradeBackup(DbConnection connection)
    {
        if (connection is not SqliteConnection source || string.IsNullOrWhiteSpace(source.DataSource) || source.DataSource == ":memory:") return;
        var backupPath = $"{source.DataSource}.pre-upgrade-v{CurrentSchemaVersion}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak";
        var connectionString = new SqliteConnectionStringBuilder { DataSource = backupPath, ForeignKeys = true }.ToString();
        using var destination = new SqliteConnection(connectionString);
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static long GetExistingDatabaseLength(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite || string.IsNullOrWhiteSpace(sqlite.DataSource) || sqlite.DataSource == ":memory:") return 0;
        return File.Exists(sqlite.DataSource) ? new FileInfo(sqlite.DataSource).Length : 0;
    }

    private static bool TableExists(DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() is not null;
    }

    private static HashSet<string> Columns(DbConnection connection, DbTransaction? transaction, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info('{table}')";
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static void Execute(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
