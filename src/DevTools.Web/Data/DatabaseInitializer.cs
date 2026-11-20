using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Data;

public static class DatabaseInitializer
{
    private const int CurrentSchemaVersion = 6;
    private static readonly string[] ApplicationTables =
    [
        "LogSystems", "LogSources", "SqlDocSources", "SqlDocDiagrams", "ServiceSystems", "ManagedServices",
        "DeploymentSystems", "DeploymentModules", "DeploymentPackages", "DeploymentEvents",
        "PatchSystems", "PatchModules", "PatchSnapshots", "PatchPackages"
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
            AddMissingDeploymentColumns(connection, transaction);
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
                FileNameFilter TEXT NULL,
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
            CREATE TABLE IF NOT EXISTS DeploymentSystems (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentSystems PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                BackupDirectory TEXT NULL,
                PackageDirectory TEXT NULL
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DeploymentModules (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentModules PRIMARY KEY AUTOINCREMENT,
                DeploymentSystemId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                AppPoolName TEXT NULL,
                ServiceName TEXT NULL,
                DefaultExclusions TEXT NULL,
                CONSTRAINT FK_DeploymentModules_DeploymentSystems_DeploymentSystemId FOREIGN KEY (DeploymentSystemId) REFERENCES DeploymentSystems (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DeploymentPackages (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentPackages PRIMARY KEY AUTOINCREMENT,
                DeploymentSystemId INTEGER NOT NULL,
                FileName TEXT NOT NULL,
                Description TEXT NULL,
                StoredPath TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                UploadedAtUtc TEXT NOT NULL,
                Status TEXT NOT NULL,
                AnalysisJson TEXT NOT NULL,
                BackupPath TEXT NULL,
                ManifestJson TEXT NULL,
                DeployedAtUtc TEXT NULL,
                RolledBackAtUtc TEXT NULL,
                Error TEXT NULL,
                CONSTRAINT FK_DeploymentPackages_DeploymentSystems_DeploymentSystemId FOREIGN KEY (DeploymentSystemId) REFERENCES DeploymentSystems (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DeploymentEvents (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentEvents PRIMARY KEY AUTOINCREMENT,
                DeploymentPackageId INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                AtUtc TEXT NOT NULL,
                Detail TEXT NULL,
                CONSTRAINT FK_DeploymentEvents_DeploymentPackages_DeploymentPackageId FOREIGN KEY (DeploymentPackageId) REFERENCES DeploymentPackages (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS PatchSystems (
                Id INTEGER NOT NULL CONSTRAINT PK_PatchSystems PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                OutputDirectory TEXT NULL
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS PatchModules (
                Id INTEGER NOT NULL CONSTRAINT PK_PatchModules PRIMARY KEY AUTOINCREMENT,
                PatchSystemId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                Exclusions TEXT NULL,
                CONSTRAINT FK_PatchModules_PatchSystems_PatchSystemId FOREIGN KEY (PatchSystemId) REFERENCES PatchSystems (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS PatchSnapshots (
                Id INTEGER NOT NULL CONSTRAINT PK_PatchSnapshots PRIMARY KEY AUTOINCREMENT,
                PatchSystemId INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                Source TEXT NOT NULL,
                Label TEXT NULL,
                CreatedBy TEXT NULL,
                IsLatest INTEGER NOT NULL DEFAULT 0,
                MarkedLatestAtUtc TEXT NULL,
                FileCount INTEGER NOT NULL,
                TotalBytes INTEGER NOT NULL,
                ManifestGz BLOB NOT NULL,
                CONSTRAINT FK_PatchSnapshots_PatchSystems_PatchSystemId FOREIGN KEY (PatchSystemId) REFERENCES PatchSystems (Id) ON DELETE CASCADE
            )
            """, transaction);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS PatchPackages (
                Id INTEGER NOT NULL CONSTRAINT PK_PatchPackages PRIMARY KEY AUTOINCREMENT,
                PatchSystemId INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                Format TEXT NOT NULL,
                FileName TEXT NOT NULL,
                StoredPath TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                Label TEXT NULL,
                CreatedBy TEXT NULL,
                Added INTEGER NOT NULL,
                Changed INTEGER NOT NULL,
                Deleted INTEGER NOT NULL,
                BaseSnapshotId INTEGER NULL,
                SnapshotId INTEGER NOT NULL,
                CONSTRAINT FK_PatchPackages_PatchSystems_PatchSystemId FOREIGN KEY (PatchSystemId) REFERENCES PatchSystems (Id) ON DELETE CASCADE
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
        if (!columns.Contains("FileNameFilter")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN FileNameFilter TEXT NULL", transaction);
    }

    private static void AddMissingDeploymentColumns(DbConnection connection, DbTransaction transaction)
    {
        var columns = Columns(connection, transaction, "DeploymentSystems");
        if (!columns.Contains("PackageDirectory")) Execute(connection, "ALTER TABLE DeploymentSystems ADD COLUMN PackageDirectory TEXT NULL", transaction);
        if (!Columns(connection, transaction, "DeploymentPackages").Contains("Description")) Execute(connection, "ALTER TABLE DeploymentPackages ADD COLUMN Description TEXT NULL", transaction);
    }

    private static void CreateCurrentIndexes(DbConnection connection, DbTransaction transaction)
    {
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_LogSystems_Name ON LogSystems (Name)", transaction);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_LogSources_LogSystemId ON LogSources (LogSystemId)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_SqlDocSources_Name ON SqlDocSources (Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_SqlDocDiagrams_SqlDocSourceId_Name ON SqlDocDiagrams (SqlDocSourceId, Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ServiceSystems_Name ON ServiceSystems (Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ManagedServices_ServiceSystemId_Name ON ManagedServices (ServiceSystemId, Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentSystems_Name ON DeploymentSystems (Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentModules_DeploymentSystemId_Name ON DeploymentModules (DeploymentSystemId, Name)", transaction);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DeploymentPackages_DeploymentSystemId_UploadedAtUtc ON DeploymentPackages (DeploymentSystemId, UploadedAtUtc)", transaction);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DeploymentEvents_DeploymentPackageId_AtUtc ON DeploymentEvents (DeploymentPackageId, AtUtc)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_PatchSystems_Name ON PatchSystems (Name)", transaction);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_PatchModules_PatchSystemId_Name ON PatchModules (PatchSystemId, Name)", transaction);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_PatchSnapshots_PatchSystemId_CreatedAtUtc ON PatchSnapshots (PatchSystemId, CreatedAtUtc)", transaction);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_PatchPackages_PatchSystemId_CreatedAtUtc ON PatchPackages (PatchSystemId, CreatedAtUtc)", transaction);
    }

    private static bool RequiresSchemaRepair(DbConnection connection)
    {
        if (ApplicationTables.Any(table => !TableExists(connection, table))) return true;
        var columns = Columns(connection, null, "LogSources");
        string[] requiredColumns = ["Format", "CustomPattern", "TimestampFormat", "IncludeSubdirectories", "FileNameFilter"];
        if (requiredColumns.Any(column => !columns.Contains(column))) return true;
        return !Columns(connection, null, "DeploymentSystems").Contains("PackageDirectory")
            || !Columns(connection, null, "DeploymentPackages").Contains("Description");
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
