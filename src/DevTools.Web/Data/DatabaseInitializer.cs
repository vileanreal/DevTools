using System.Data;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Data;

public static class DatabaseInitializer
{
    public static void Initialize(DevToolsDbContext database)
    {
        database.Database.EnsureCreated();
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=NORMAL");
        Execute(connection, "PRAGMA temp_store=MEMORY");
        Execute(connection, "PRAGMA busy_timeout=5000");
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('LogSources')";
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("Format")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN Format TEXT NOT NULL DEFAULT 'auto'");
        if (!columns.Contains("CustomPattern")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN CustomPattern TEXT NULL");
        if (!columns.Contains("TimestampFormat")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN TimestampFormat TEXT NULL");
        if (!columns.Contains("IncludeSubdirectories")) Execute(connection, "ALTER TABLE LogSources ADD COLUMN IncludeSubdirectories INTEGER NOT NULL DEFAULT 1");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS SqlDocSources (
                Id INTEGER NOT NULL CONSTRAINT PK_SqlDocSources PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Provider TEXT NOT NULL,
                ConnectionString TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            )
            """);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_SqlDocSources_Name ON SqlDocSources (Name)");
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
            """);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_SqlDocDiagrams_SqlDocSourceId_Name ON SqlDocDiagrams (SqlDocSourceId, Name)");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS ServiceSystems (
                Id INTEGER NOT NULL CONSTRAINT PK_ServiceSystems PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL
            )
            """);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ServiceSystems_Name ON ServiceSystems (Name)");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS ManagedServices (
                Id INTEGER NOT NULL CONSTRAINT PK_ManagedServices PRIMARY KEY AUTOINCREMENT,
                ServiceSystemId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Kind TEXT NOT NULL,
                TargetName TEXT NOT NULL,
                CONSTRAINT FK_ManagedServices_ServiceSystems_ServiceSystemId FOREIGN KEY (ServiceSystemId) REFERENCES ServiceSystems (Id) ON DELETE CASCADE
            )
            """);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_ManagedServices_ServiceSystemId_Name ON ManagedServices (ServiceSystemId, Name)");
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static HashSet<string> Columns(System.Data.Common.DbConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info('{table}')";
        using var reader = command.ExecuteReader(); while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }
}
