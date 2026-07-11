namespace DevTools.Web.Data.Entities;

public sealed class IndexedLogFile
{
    public int Id { get; set; }
    public int LogSourceId { get; set; }
    public required string Path { get; set; }
    public required string FileName { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime IndexedUtc { get; set; }
    public int ParserVersion { get; set; }
    public string? ContentHash { get; set; }
    public LogSource? LogSource { get; set; }
    public List<IndexedLogEntry> Entries { get; set; } = [];
}

public sealed class IndexedLogEntry
{
    public long Id { get; set; }
    public int IndexedLogFileId { get; set; }
    public int LogSourceId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public required string TimestampSource { get; set; }
    public required string Level { get; set; }
    public required string Message { get; set; }
    public long LineNumber { get; set; }
    public IndexedLogFile? IndexedLogFile { get; set; }
    public LogSource? LogSource { get; set; }
}
