using System.Data;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Data;

public static class LogIndexDatabaseInitializer
{
    public static void Initialize(LogIndexDbContext database)
    {
        database.Database.EnsureCreated();
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON");
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=NORMAL");
        Execute(connection, "PRAGMA temp_store=MEMORY");
        Execute(connection, "PRAGMA busy_timeout=5000");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS IndexedLogFiles (
                Id INTEGER NOT NULL CONSTRAINT PK_IndexedLogFiles PRIMARY KEY AUTOINCREMENT,
                LogSourceId INTEGER NOT NULL,
                Path TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Length INTEGER NOT NULL,
                LastWriteUtc TEXT NOT NULL,
                IndexedUtc TEXT NOT NULL,
                ParserVersion INTEGER NOT NULL DEFAULT 0,
                ContentHash TEXT NULL
            )
            """);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS IndexedLogEntries (
                Id INTEGER NOT NULL CONSTRAINT PK_IndexedLogEntries PRIMARY KEY AUTOINCREMENT,
                IndexedLogFileId INTEGER NOT NULL,
                LogSourceId INTEGER NOT NULL,
                TimestampUtc TEXT NOT NULL,
                TimestampSource TEXT NOT NULL,
                Level TEXT NOT NULL,
                Message TEXT NOT NULL,
                LineNumber INTEGER NOT NULL,
                CONSTRAINT FK_IndexedLogEntries_IndexedLogFiles_IndexedLogFileId FOREIGN KEY (IndexedLogFileId) REFERENCES IndexedLogFiles (Id) ON DELETE CASCADE
            )
            """);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_IndexedLogFiles_LogSourceId_Path ON IndexedLogFiles (LogSourceId, Path)");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_IndexedLogEntries_LogSourceId_TimestampUtc ON IndexedLogEntries (LogSourceId, TimestampUtc)");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_IndexedLogEntries_LogSourceId_Level_TimestampUtc ON IndexedLogEntries (LogSourceId, Level, TimestampUtc)");
        Execute(connection, "CREATE VIRTUAL TABLE IF NOT EXISTS LogEntrySearch USING fts5(Message, content='IndexedLogEntries', content_rowid='Id')");
        Execute(connection, """CREATE TRIGGER IF NOT EXISTS LogEntrySearch_Insert AFTER INSERT ON IndexedLogEntries BEGIN INSERT INTO LogEntrySearch(rowid, Message) VALUES (new.Id, new.Message); END""");
        Execute(connection, """CREATE TRIGGER IF NOT EXISTS LogEntrySearch_Delete AFTER DELETE ON IndexedLogEntries BEGIN INSERT INTO LogEntrySearch(LogEntrySearch, rowid, Message) VALUES ('delete', old.Id, old.Message); END""");
        Execute(connection, """CREATE TRIGGER IF NOT EXISTS LogEntrySearch_Update AFTER UPDATE ON IndexedLogEntries BEGIN INSERT INTO LogEntrySearch(LogEntrySearch, rowid, Message) VALUES ('delete', old.Id, old.Message); INSERT INTO LogEntrySearch(rowid, Message) VALUES (new.Id, new.Message); END""");
        Execute(connection, "DELETE FROM IndexedLogEntries WHERE NOT EXISTS (SELECT 1 FROM IndexedLogFiles WHERE IndexedLogFiles.Id = IndexedLogEntries.IndexedLogFileId)");
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
