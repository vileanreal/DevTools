using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using DevTools.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Data;

public static class DatabaseInitializer
{
    private const int CurrentSchemaVersion = 10;
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
            AddMissingPatchColumns(connection, transaction);
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
                PackageDirectory TEXT NULL,
                RequireVersionChange INTEGER NOT NULL DEFAULT 0,
                BackupFormat TEXT NOT NULL DEFAULT 'zip'
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
                VersionFile TEXT NULL,
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
                LatestSnapshotId INTEGER NULL,
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
                Modules TEXT NULL,
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
                Modules TEXT NULL,
                ChangesGz BLOB NULL,
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
        // v9 (v8 is used by the 1.4 line): optional "require a new version" check per system, main file per module.
        if (!columns.Contains("RequireVersionChange")) Execute(connection, "ALTER TABLE DeploymentSystems ADD COLUMN RequireVersionChange INTEGER NOT NULL DEFAULT 0", transaction);
        if (!Columns(connection, transaction, "DeploymentModules").Contains("VersionFile")) Execute(connection, "ALTER TABLE DeploymentModules ADD COLUMN VersionFile TEXT NULL", transaction);
        // v10: backup format per system (.zip by default; existing backups keep their format and still roll back).
        if (!columns.Contains("BackupFormat")) Execute(connection, "ALTER TABLE DeploymentSystems ADD COLUMN BackupFormat TEXT NOT NULL DEFAULT 'zip'", transaction);
    }

    /// <summary>
    /// v7: each module has its own latest version. Adds the columns and, only when they are first added, fills them from
    /// existing rows: every module contained in the system's old "latest" snapshot points at it, and each snapshot's
    /// module names are copied out of its manifest. Rows are only updated, never removed.
    /// </summary>
    private static void AddMissingPatchColumns(DbConnection connection, DbTransaction transaction)
    {
        var snapshotColumns = Columns(connection, transaction, "PatchSnapshots");
        var fillSnapshotModules = !snapshotColumns.Contains("Modules");
        if (fillSnapshotModules) Execute(connection, "ALTER TABLE PatchSnapshots ADD COLUMN Modules TEXT NULL", transaction);
        var packageColumns = Columns(connection, transaction, "PatchPackages");
        if (!packageColumns.Contains("Modules")) Execute(connection, "ALTER TABLE PatchPackages ADD COLUMN Modules TEXT NULL", transaction);
        if (!packageColumns.Contains("ChangesGz")) Execute(connection, "ALTER TABLE PatchPackages ADD COLUMN ChangesGz BLOB NULL", transaction);
        var fillModulePointers = !Columns(connection, transaction, "PatchModules").Contains("LatestSnapshotId");
        if (fillModulePointers) Execute(connection, "ALTER TABLE PatchModules ADD COLUMN LatestSnapshotId INTEGER NULL", transaction);
        if (!fillSnapshotModules && !fillModulePointers) return;

        var snapshots = new List<(long Id, long SystemId, bool IsLatest, List<DevTools.Shared.Models.PatchModuleManifest> Manifest)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, PatchSystemId, IsLatest, ManifestGz FROM PatchSnapshots";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                List<DevTools.Shared.Models.PatchModuleManifest> manifest;
                try { manifest = PatchCodec.ReadManifest((byte[])reader.GetValue(3)); }
                catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException) { manifest = []; }
                snapshots.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) != 0, manifest));
            }
        }
        var modules = new List<(long Id, long SystemId, string Name)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, PatchSystemId, Name FROM PatchModules";
            using var reader = command.ExecuteReader();
            while (reader.Read()) modules.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }
        if (fillSnapshotModules)
            foreach (var snapshot in snapshots)
                Execute(connection, "UPDATE PatchSnapshots SET Modules = $value WHERE Id = $id", transaction,
                    ("$value", PatchCodec.WriteNames(snapshot.Manifest.Select(item => item.ModuleName))), ("$id", snapshot.Id));
        if (fillModulePointers)
            foreach (var snapshot in snapshots.Where(item => item.IsLatest))
                foreach (var module in modules.Where(item => item.SystemId == snapshot.SystemId && snapshot.Manifest.Any(manifest =>
                             manifest.ModuleId == item.Id || manifest.ModuleName.Equals(item.Name, StringComparison.OrdinalIgnoreCase))))
                    Execute(connection, "UPDATE PatchModules SET LatestSnapshotId = $value WHERE Id = $id", transaction, ("$value", snapshot.Id), ("$id", module.Id));
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
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_PatchModules_LatestSnapshotId ON PatchModules (LatestSnapshotId)", transaction);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_PatchPackages_PatchSystemId_CreatedAtUtc ON PatchPackages (PatchSystemId, CreatedAtUtc)", transaction);
    }

    private static bool RequiresSchemaRepair(DbConnection connection)
    {
        if (ApplicationTables.Any(table => !TableExists(connection, table))) return true;
        var columns = Columns(connection, null, "LogSources");
        string[] requiredColumns = ["Format", "CustomPattern", "TimestampFormat", "IncludeSubdirectories", "FileNameFilter"];
        if (requiredColumns.Any(column => !columns.Contains(column))) return true;
        return !Columns(connection, null, "DeploymentSystems").Contains("PackageDirectory")
            || !Columns(connection, null, "DeploymentSystems").Contains("RequireVersionChange")
            || !Columns(connection, null, "DeploymentSystems").Contains("BackupFormat")
            || !Columns(connection, null, "DeploymentModules").Contains("VersionFile")
            || !Columns(connection, null, "DeploymentPackages").Contains("Description")
            || !Columns(connection, null, "PatchModules").Contains("LatestSnapshotId")
            || !Columns(connection, null, "PatchSnapshots").Contains("Modules")
            || !Columns(connection, null, "PatchPackages").Contains("ChangesGz");
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

    private static void Execute(DbConnection connection, string sql, DbTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        command.ExecuteNonQuery();
    }
}
