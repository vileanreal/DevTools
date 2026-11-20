namespace DevTools.Web.Data.Entities;

public sealed class LogSystem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<LogSource> Sources { get; set; } = [];
}

public sealed class LogSource
{
    public int Id { get; set; }
    public required string ModuleName { get; set; }
    public required string Path { get; set; }
    public string Format { get; set; } = "auto";
    public string? CustomPattern { get; set; }
    public string? TimestampFormat { get; set; }
    public bool IncludeSubdirectories { get; set; } = true;
    public string? FileNameFilter { get; set; }
    public int LogSystemId { get; set; }
    public LogSystem? LogSystem { get; set; }
}
