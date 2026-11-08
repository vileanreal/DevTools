namespace DevTools.Shared.Models;

public sealed record HashRequest(string? Input, string Algorithm = "SHA256", string? Key = null);

public sealed record HashResult(string Algorithm, string Value, int InputLength);

public sealed record FormatRequest(string? Input, string Language = "csharp", int IndentSize = 4, bool UseTabs = false);

public sealed record FormatResult(string Output, bool IsValid, string? Error = null);

public sealed record LogParseRequest(string? Input, string? Level = null, string? Search = null, int Page = 1, int PageSize = 100);

public sealed record LogEntry(string Timestamp, string Level, string Message, string Raw);

public sealed record LogLevelCount(string Level, int Count);
public sealed record LogParseResult(IReadOnlyList<LogEntry> Entries, int TotalCount, int FilteredCount, IReadOnlyList<LogLevelCount> Statistics, int Page, int PageSize, int TotalPages);

public sealed record CreateSystemRequest(string? Name);
public sealed record CreateLogSourceRequest(string? ModuleName, string? Path, string Format = "auto", string? CustomPattern = null, string? TimestampFormat = null, bool IncludeSubdirectories = true);
public sealed record UpdateLogSourceRequest(string? ModuleName, string? Path, string Format = "auto", string? CustomPattern = null, string? TimestampFormat = null, bool IncludeSubdirectories = true);
public sealed record LogSourceDto(int Id, string ModuleName, string Path, string Format, string? CustomPattern, string? TimestampFormat, bool IncludeSubdirectories);
public sealed record LogSystemDto(int Id, string Name, IReadOnlyList<LogSourceDto> Sources);
public sealed record LogRefreshStartResult(string JobId, string Status);
public sealed record LogRefreshStatus(string JobId, int SystemId, int? SourceId, string Status, int FilesDiscovered, int FilesScanned, int FilesIndexed, int FilesUnchanged, int FilesRemoved, int FilesFailed, long EntriesIndexed, string? CurrentFile, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error);
public sealed record LogIndexClearStatus(string JobId, int SystemId, int? SourceId, string Status, long BytesBefore, long BytesAfter, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error);
public sealed record SystemLogQuery(
    int? SourceId = null,
    string? Level = null,
    string? Search = null,
    string SearchMode = "contains",
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50,
    string ChartBy = "time",
    string ChartMetric = "all",
    string TimeBucket = "auto",
    DateTimeOffset? ChartFrom = null,
    DateTimeOffset? ChartTo = null,
    string? ChartLevel = null,
    string? ChartLevels = null,
    string? Columns = null);

public sealed record IndexedLogEntryDto(
    long Id,
    int SourceId,
    string ModuleName,
    string FileName,
    DateTimeOffset Timestamp,
    string TimestampSource,
    string Level,
    string MessagePreview,
    bool IsTruncated);

public sealed record LogChartPoint(string Label, int Count);
public sealed record LogChartSeries(string Level, IReadOnlyList<LogChartPoint> Points);
public sealed record LogQuerySummary(int TotalEntries, int ErrorCount, int WarningCount, int FileCount);
public sealed record SystemLogPage(
    int SystemId,
    IReadOnlyList<IndexedLogEntryDto> Entries,
    LogQuerySummary Summary,
    IReadOnlyList<LogChartSeries> Chart,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalEntries,
    IReadOnlyList<string> VisibleColumns,
    string ChartBy,
    string ChartMetric,
    string TimeBucket,
    IReadOnlyList<string> ChartLevels,
    DateTimeOffset? ChartFrom,
    DateTimeOffset? ChartTo,
    string ChartLevel,
    DateTimeOffset GeneratedAt);

public sealed record LogRefreshResult(int FilesDiscovered, int FilesIndexed, int FilesUnchanged, int FilesRemoved, int FilesFailed, long EntriesIndexed, DateTimeOffset RefreshedAt);
public sealed record LogPreviewRequest(string Sample, string Format = "auto", string? CustomPattern = null, string? TimestampFormat = null);

public sealed record CreateServiceSystemRequest(string? Name);
public sealed record CreateManagedServiceRequest(string? Name, string? Kind, string? TargetName);
public sealed record ServiceRuntimeStatus(string Status, string? Detail, DateTimeOffset CheckedAt);
public sealed record ManagedServiceDto(int Id, int SystemId, string Name, string Kind, string TargetName, string Status, string? StatusDetail, DateTimeOffset? CheckedAt);
public sealed record ServiceSystemDto(int Id, string Name, IReadOnlyList<ManagedServiceDto> Services);

public sealed record SqlDocSourceDto(int Id, string Name, string Provider, string ConnectionString, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SqlDocConnectionRequest(
    string? Provider,
    string? ConnectionString = null,
    string? Server = null,
    string? Port = null,
    string? Database = null,
    string? Username = null,
    string? Password = null,
    bool IntegratedSecurity = false,
    bool TrustServerCertificate = true,
    string? SqlitePath = null);
public sealed record CreateSqlDocSourceRequest(
    string? Name,
    string? Provider,
    string? ConnectionString = null,
    string? Server = null,
    string? Port = null,
    string? Database = null,
    string? Username = null,
    string? Password = null,
    bool IntegratedSecurity = false,
    bool TrustServerCertificate = true,
    string? SqlitePath = null);
public sealed record UpdateSqlDocSourceRequest(
    string? Name,
    string? Provider,
    string? ConnectionString = null,
    string? Server = null,
    string? Port = null,
    string? Database = null,
    string? Username = null,
    string? Password = null,
    bool IntegratedSecurity = false,
    bool TrustServerCertificate = true,
    string? SqlitePath = null);
public sealed record SqlDocDatabaseList(IReadOnlyList<string> Databases);
public sealed record SqlDocGenerateRequest(bool Tables = true, bool Views = true, bool StoredProcedures = true, bool Functions = true, bool Indexes = true, bool Relationships = true, string? ObjectNames = null);
public sealed record SqlDocDiagramDto(int Id, int SourceId, string Name, string DiagramJson, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SaveSqlDocDiagramRequest(string? Name, string? DiagramJson);
public sealed record SqlDocOverview(string DatabaseName, string Provider, IReadOnlyList<SqlDocObjectSummary> Objects);
public sealed record SqlDocObjectSummary(string Type, string Name, int DetailCount);
public sealed record SqlDocSchema(
    int SourceId,
    string SourceName,
    string Provider,
    string DatabaseName,
    IReadOnlyList<SqlDocTable> Tables,
    IReadOnlyList<SqlDocView> Views,
    IReadOnlyList<SqlDocRoutine> StoredProcedures,
    IReadOnlyList<SqlDocRoutine> Functions,
    IReadOnlyList<SqlDocIndex> Indexes);
public sealed record SqlDocTable(string Schema, string Name, IReadOnlyList<SqlDocColumn> Columns, IReadOnlyList<SqlDocForeignKey> ForeignKeys);
public sealed record SqlDocColumn(string Name, string DataType, bool IsNullable, bool IsPrimaryKey, string? DefaultValue, int? MaxLength = null);
public sealed record SqlDocForeignKey(string Name, string ColumnName, string ReferencedTable, string ReferencedColumn);
public sealed record SqlDocView(string Schema, string Name, string? Definition);
public sealed record SqlDocRoutine(string Schema, string Name, string? Definition);
public sealed record SqlDocIndex(string Schema, string TableName, string Name, bool IsUnique, IReadOnlyList<string> Columns);

public sealed record ApiResult<T>(bool Success, T? Data = default, string? Error = null)
{
    public static ApiResult<T> Ok(T data) => new(true, data);
    public static ApiResult<T> Fail(string error) => new(false, default, error);
}
