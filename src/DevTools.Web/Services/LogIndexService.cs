using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DevTools.Application.Services;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;
using DevTools.Web.Data;
using DevTools.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace DevTools.Web.Services;

public interface ILogIndexService
{
    Task<LogRefreshResult> RefreshAsync(int systemId, int? sourceId, CancellationToken cancellationToken);
    Task<LogRefreshResult> RefreshAsync(int systemId, int? sourceId, IProgress<LogRefreshProgress>? progress, CancellationToken cancellationToken);
    Task<LogIndexClearResult> ClearAsync(int systemId, int? sourceId, CancellationToken cancellationToken);
    Task<SystemLogPage?> QueryAsync(int systemId, SystemLogQuery query, CancellationToken cancellationToken);
    Task<string?> GetMessageAsync(int systemId, long entryId, CancellationToken cancellationToken);
    Task<LogFileDownload?> GetLogFileAsync(int systemId, long entryId, CancellationToken cancellationToken);
}

public sealed record LogFileDownload(string Path, string FileName);
public sealed record LogIndexClearResult(long BytesBefore, long BytesAfter);
public sealed record LogRefreshProgress(int FilesDiscovered, int FilesScanned, int FilesIndexed, int FilesUnchanged, int FilesRemoved, int FilesFailed, long EntriesIndexed, string? CurrentFile);

public sealed partial class LogIndexService(DevToolsDbContext database, LogIndexDbContext indexDatabase, ILogParserService parser, IMemoryCache cache) : ILogIndexService
{
    private const int BatchSize = 100;
    private const int CurrentParserVersion = 7;
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> IndexLocks = new();
    private static readonly ConcurrentDictionary<int, long> IndexVersions = new();
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".log", ".txt", ".out", ".json", ".jsonl" };

    [GeneratedRegex(@"(?<!\d)(?<date>20\d{2}[-_.]?\d{2}[-_.]?\d{2})[-_. T]?(?<time>\d{2}[-_.:]?\d{2}[-_.:]?\d{2})(?!\d)")]
    private static partial Regex FileTimestampPattern();

    [GeneratedRegex(@"(?<!\d)(?<date>20\d{2}[-_.]?\d{2}[-_.]?\d{2})(?!\d)")]
    private static partial Regex FileDatePattern();

    [GeneratedRegex(@"(?<!\d)(?<unix>1\d{9}|1\d{12})(?!\d)")]
    private static partial Regex UnixTimestampPattern();

    [GeneratedRegex(@"[\p{L}\p{N}_-]+")]
    private static partial Regex SearchTermPattern();

    [GeneratedRegex(@"^(?<hour>\d{1,2}):(?<minute>\d{2}):(?<second>\d{2})(?:[.,](?<fraction>\d+))?\s*(?<period>AM|PM)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeValuePattern();

    public Task<LogRefreshResult> RefreshAsync(int systemId, int? sourceId, CancellationToken cancellationToken) =>
        RefreshAsync(systemId, sourceId, null, cancellationToken);

    public async Task<LogRefreshResult> RefreshAsync(int systemId, int? sourceId, IProgress<LogRefreshProgress>? progress, CancellationToken cancellationToken)
    {
        var indexLock = IndexLocks.GetOrAdd(systemId, _ => new SemaphoreSlim(1, 1));
        await indexLock.WaitAsync(cancellationToken);
        try
        {
            var sourcesQuery = database.LogSources.AsNoTracking().Where(item => item.LogSystemId == systemId);
            if (sourceId.HasValue) sourcesQuery = sourcesQuery.Where(item => item.Id == sourceId.Value);
            var sources = await sourcesQuery.ToListAsync(cancellationToken);
            if (sourceId.HasValue && sources.Count == 0) throw new KeyNotFoundException("Module not found in this system.");

            var discovered = 0; var scanned = 0; var indexed = 0; var unchanged = 0; var removed = 0; var failed = 0; long entriesIndexed = 0;
            void Report(string? currentFile = null) => progress?.Report(new LogRefreshProgress(discovered, scanned, indexed, unchanged, removed, failed, entriesIndexed, currentFile));
            foreach (var source in sources)
            {
                var paths = DiscoverFiles(source.Path, source.IncludeSubdirectories).ToList();
                discovered += paths.Count;
                Report();
                var found = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var existing = await indexDatabase.IndexedLogFiles.Where(item => item.LogSourceId == source.Id).ToListAsync(cancellationToken);
                foreach (var stale in existing.Where(item => !found.Contains(item.Path)))
                {
                    indexDatabase.IndexedLogFiles.Remove(stale); removed++;
                }
                if (removed > 0) { await indexDatabase.SaveChangesAsync(cancellationToken); Report(); }

                var indexedByPath = existing.Where(item => found.Contains(item.Path)).ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentFile = Path.GetFileName(path);
                    Report(currentFile);
                    try
                    {
                        var info = new FileInfo(path);
                        var lastWriteUtc = NormalizeUtc(info.LastWriteTimeUtc);
                        if (indexedByPath.TryGetValue(path, out var record) && record.ParserVersion == CurrentParserVersion)
                        {
                            if (record.Length == info.Length && !string.IsNullOrWhiteSpace(record.ContentHash))
                            {
                                var currentHash = await ComputeContentHashAsync(info.FullName, cancellationToken);
                                if (currentHash == record.ContentHash)
                                {
                                    await indexDatabase.IndexedLogFiles.Where(item => item.Id == record.Id).ExecuteUpdateAsync(setters => setters
                                        .SetProperty(item => item.LastWriteUtc, lastWriteUtc)
                                        .SetProperty(item => item.IndexedUtc, DateTime.UtcNow), cancellationToken);
                                    unchanged++; scanned++; Report(currentFile); continue;
                                }
                                entriesIndexed += await ReindexFileAsync(source, info, record, currentHash, cancellationToken);
                                indexed++; scanned++; Report(currentFile); continue;
                            }
                            if (record.Length == info.Length && SameUtc(record.LastWriteUtc, lastWriteUtc))
                            {
                                var currentHash = await ComputeContentHashAsync(info.FullName, cancellationToken);
                                await indexDatabase.IndexedLogFiles.Where(item => item.Id == record.Id).ExecuteUpdateAsync(setters => setters
                                    .SetProperty(item => item.ContentHash, currentHash)
                                    .SetProperty(item => item.IndexedUtc, DateTime.UtcNow), cancellationToken);
                                unchanged++; scanned++; Report(currentFile); continue;
                            }
                        }
                        entriesIndexed += await ReindexFileAsync(source, info, record, null, cancellationToken);
                        indexed++; scanned++; Report(currentFile);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed++; scanned++; Report(currentFile); }
                }
            }
            if (indexed > 0 || removed > 0) IndexVersions.AddOrUpdate(systemId, 1, (_, version) => version + 1);
            return new LogRefreshResult(discovered, indexed, unchanged, removed, failed, entriesIndexed, DateTimeOffset.UtcNow);
        }
        finally { indexLock.Release(); }
    }

    public async Task<SystemLogPage?> QueryAsync(int systemId, SystemLogQuery query, CancellationToken cancellationToken)
    {
        if (!await database.LogSystems.AnyAsync(item => item.Id == systemId, cancellationToken)) return null;
        var sourceRows = await database.LogSources.AsNoTracking()
            .Where(item => item.LogSystemId == systemId)
            .Select(item => new { item.Id, item.ModuleName })
            .ToListAsync(cancellationToken);
        if (query.SourceId.HasValue) sourceRows = sourceRows.Where(item => item.Id == query.SourceId.Value).ToList();
        var sourceIds = sourceRows.Select(item => item.Id).ToList();
        var moduleNames = sourceRows.ToDictionary(item => item.Id, item => item.ModuleName);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 10, 200);
        IQueryable<IndexedLogEntry> rootEntries = CreateSearchQuery(query.Search);
        rootEntries = rootEntries.Where(item => sourceIds.Contains(item.LogSourceId));
        var entries = rootEntries;
        if (query.From.HasValue)
        {
            var from = query.From.Value.UtcDateTime;
            entries = entries.Where(item => item.TimestampUtc >= from);
        }
        if (query.To.HasValue)
        {
            var to = query.To.Value.UtcDateTime;
            entries = entries.Where(item => item.TimestampUtc <= to);
        }
        if (!string.IsNullOrWhiteSpace(query.Level) && !query.Level.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            entries = entries.Where(item => item.Level == query.Level.ToUpperInvariant());

        var chartBy = "time";
        var chartMetric = "levels";
        var chartFrom = query.ChartFrom ?? query.From;
        var chartTo = query.ChartTo ?? query.To;
        var chartEntries = rootEntries;
        if (chartFrom.HasValue)
        {
            var from = chartFrom.Value.UtcDateTime;
            chartEntries = chartEntries.Where(item => item.TimestampUtc >= from);
        }
        if (chartTo.HasValue)
        {
            var to = chartTo.Value.UtcDateTime;
            chartEntries = chartEntries.Where(item => item.TimestampUtc <= to);
        }
        var timeBucket = await ResolveTimeBucketAsync(query.TimeBucket, chartEntries, chartFrom, chartTo, cancellationToken);
        string[] allowedLevels = ["FATAL", "ERROR", "WARN", "INFO", "DEBUG", "TRACE", "OTHER"];
        var chartLevel = string.IsNullOrWhiteSpace(query.ChartLevel) ? "ALL" : query.ChartLevel.ToUpperInvariant();
        var chartLevels = chartLevel != "ALL" && allowedLevels.Contains(chartLevel)
            ? new List<string> { chartLevel }
            : (query.ChartLevels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(level => level.ToUpperInvariant()).Where(level => allowedLevels.Contains(level)).Distinct().ToList();
        if (!allowedLevels.Contains(chartLevel)) chartLevel = "ALL";
        if (chartLevels.Count == 0) chartLevels = allowedLevels.ToList();
        var version = IndexVersions.GetOrAdd(systemId, 0);
        var chartLevelKey = string.Join(',', chartLevels);
        var rowFilterKey = $"{systemId}:{version}:{query.SourceId}:{query.Level}:{query.Search}:{query.From?.UtcTicks}:{query.To?.UtcTicks}";
        var chartFilterKey = $"{systemId}:{version}:{query.SourceId}:{query.Search}:{chartFrom?.UtcTicks}:{chartTo?.UtcTicks}:{timeBucket}:{chartLevelKey}";
        var summary = await cache.GetOrCreateAsync($"summary:{rowFilterKey}", async cacheEntry =>
        {
            cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2); cacheEntry.Size = 2;
            var aggregate = await entries.GroupBy(_ => 1).Select(group => new { Total=group.Count(), Errors=group.Count(item=>item.Level=="ERROR"||item.Level=="FATAL"), Warnings=group.Count(item=>item.Level=="WARN"), Files=group.Select(item=>item.IndexedLogFileId).Distinct().Count() }).SingleOrDefaultAsync(cancellationToken);
            return new LogQuerySummary(aggregate?.Total??0,aggregate?.Errors??0,aggregate?.Warnings??0,aggregate?.Files??0);
        }) ?? new LogQuerySummary(0,0,0,0);
        var chart = await cache.GetOrCreateAsync($"chart:{chartFilterKey}", async cacheEntry =>
        {
            cacheEntry.AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(2);cacheEntry.Size=5;
            return await BuildChartAsync(chartEntries.Where(item=>chartLevels.Contains(item.Level)),timeBucket,chartFrom,chartTo,cancellationToken);
        }) ?? [];
        var total = summary.TotalEntries;
        var pages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        page = Math.Min(page, Math.Max(1, pages));
        var allowedColumns = new[] { "timestamp", "module", "file", "level", "message" };
        var requestedColumns = (query.Columns ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visibleColumns = requestedColumns.Where(column => allowedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)).Select(column => column.ToLowerInvariant()).Distinct().ToList();
        if (visibleColumns.Count == 0) visibleColumns = allowedColumns.ToList();
        var includeModule = visibleColumns.Contains("module");
        var includeFile = visibleColumns.Contains("file");
        var includeMessage = visibleColumns.Contains("message");
        var includeLevel = visibleColumns.Contains("level") || includeMessage;
        var columnKey=string.Join(',',visibleColumns);
        var rows = await cache.GetOrCreateAsync($"rows:{rowFilterKey}:{page}:{pageSize}:{columnKey}", async cacheEntry =>
        {
            cacheEntry.AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(2);cacheEntry.Size=Math.Max(1,pageSize/10);
            var rawRows=await entries.OrderByDescending(item=>item.TimestampUtc).ThenByDescending(item=>item.Id).Skip((page-1)*pageSize).Take(pageSize).Select(item=>new{item.Id,item.LogSourceId,FileName=includeFile?item.IndexedLogFile!.FileName:string.Empty,item.TimestampUtc,item.TimestampSource,Level=includeLevel?item.Level:string.Empty,Preview=includeMessage?item.Message.Length>160?item.Message.Substring(0,160):item.Message:string.Empty,IsTruncated=includeMessage&&item.Message.Length>160}).ToListAsync(cancellationToken);
            return rawRows.Select(item=>new IndexedLogEntryDto(item.Id,item.LogSourceId,includeModule&&moduleNames.TryGetValue(item.LogSourceId,out var moduleName)?moduleName:string.Empty,item.FileName,new DateTimeOffset(DateTime.SpecifyKind(item.TimestampUtc,DateTimeKind.Utc)),item.TimestampSource,item.Level,item.Preview,item.IsTruncated)).ToList();
        }) ?? [];
        return new SystemLogPage(systemId, rows, summary, chart, page, pageSize, pages, total, visibleColumns, chartBy, chartMetric, timeBucket, chartLevels, chartFrom, chartTo, chartLevel, DateTimeOffset.UtcNow);
    }

    public async Task<LogIndexClearResult> ClearAsync(int systemId, int? sourceId, CancellationToken cancellationToken)
    {
        var indexLock = IndexLocks.GetOrAdd(systemId, _ => new SemaphoreSlim(1, 1));
        await indexLock.WaitAsync(cancellationToken);
        try
        {
            var databasePath = indexDatabase.Database.GetDbConnection().DataSource;
            var configurationPath = database.Database.GetDbConnection().DataSource;
            var bytesBefore = DatabaseSize(databasePath) + DatabaseSize(configurationPath);
            var sourceIds = await database.LogSources.AsNoTracking().Where(item => item.LogSystemId == systemId && (!sourceId.HasValue || item.Id == sourceId.Value)).Select(item => item.Id).ToListAsync(cancellationToken);
            if (sourceIds.Count > 0)
            {
                await indexDatabase.IndexedLogEntries.Where(item => sourceIds.Contains(item.LogSourceId)).ExecuteDeleteAsync(cancellationToken);
                await indexDatabase.IndexedLogFiles.Where(item => sourceIds.Contains(item.LogSourceId)).ExecuteDeleteAsync(cancellationToken);
                indexDatabase.ChangeTracker.Clear();
                IndexVersions.AddOrUpdate(systemId, 1, (_, version) => version + 1);
            }

            await indexDatabase.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
            await indexDatabase.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
            await indexDatabase.Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken);
            await RemoveLegacyConfigurationIndexAsync(cancellationToken);
            return new LogIndexClearResult(bytesBefore, DatabaseSize(databasePath) + DatabaseSize(configurationPath));
        }
        finally { indexLock.Release(); }
    }

    private static long DatabaseSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        return new[] { path, $"{path}-wal", $"{path}-shm" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);
    }

    private async Task RemoveLegacyConfigurationIndexAsync(CancellationToken cancellationToken)
    {
        string[] commands =
        [
            "DROP TRIGGER IF EXISTS LogEntrySearch_Insert;",
            "DROP TRIGGER IF EXISTS LogEntrySearch_Delete;",
            "DROP TRIGGER IF EXISTS LogEntrySearch_Update;",
            "DROP TABLE IF EXISTS LogEntrySearch;",
            "DROP TABLE IF EXISTS LogEntrySearch_config;",
            "DROP TABLE IF EXISTS LogEntrySearch_data;",
            "DROP TABLE IF EXISTS LogEntrySearch_docsize;",
            "DROP TABLE IF EXISTS LogEntrySearch_idx;",
            "DROP TABLE IF EXISTS IndexedLogEntries;",
            "DROP TABLE IF EXISTS IndexedLogFiles;"
        ];
        foreach (var command in commands) await database.Database.ExecuteSqlRawAsync(command, cancellationToken);
        await database.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        await database.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
        await database.Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken);
    }

    public async Task<string?> GetMessageAsync(int systemId, long entryId, CancellationToken cancellationToken)
    {
        var sourceIds = await database.LogSources.AsNoTracking().Where(item => item.LogSystemId == systemId).Select(item => item.Id).ToListAsync(cancellationToken);
        return await indexDatabase.IndexedLogEntries.AsNoTracking().Where(item => item.Id == entryId && sourceIds.Contains(item.LogSourceId))
            .Select(item => item.Message).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<LogFileDownload?> GetLogFileAsync(int systemId, long entryId, CancellationToken cancellationToken)
    {
        var sourceIds = await database.LogSources.AsNoTracking().Where(item => item.LogSystemId == systemId).Select(item => item.Id).ToListAsync(cancellationToken);
        var file = await indexDatabase.IndexedLogEntries.AsNoTracking().Where(item => item.Id == entryId && sourceIds.Contains(item.LogSourceId))
            .Select(item => new { item.IndexedLogFile!.Path, item.IndexedLogFile.FileName }).SingleOrDefaultAsync(cancellationToken);
        return file is null ? null : new LogFileDownload(file.Path, file.FileName);
    }

    private IQueryable<IndexedLogEntry> CreateSearchQuery(string? search)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? string.Empty : search[..Math.Min(search.Length, 200)];
        var terms = normalizedSearch.Length == 0 ? [] : SearchTermPattern().Matches(normalizedSearch).Select(match => match.Value).Take(10).ToArray();
        if (terms.Length == 0) return indexDatabase.IndexedLogEntries.AsNoTracking();
        var ftsQuery = string.Join(" AND ", terms.Select(term => $"\"{term.Replace("\"", "\"\"")}\""));
        return indexDatabase.IndexedLogEntries.FromSqlRaw("SELECT e.* FROM IndexedLogEntries AS e INNER JOIN LogEntrySearch AS s ON s.rowid = e.Id WHERE LogEntrySearch MATCH {0}", ftsQuery).AsNoTracking();
    }

    private static async Task<IReadOnlyList<LogChartSeries>> BuildChartAsync(IQueryable<IndexedLogEntry> entries, string timeBucket, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var points = new List<(DateTime Date, string Level, int Count)>();
        if (timeBucket == "minute")
        {
            var buckets = await entries.GroupBy(item => new { item.TimestampUtc.Year, item.TimestampUtc.Month, item.TimestampUtc.Day, item.TimestampUtc.Hour, item.TimestampUtc.Minute, item.Level }).Select(group => new { group.Key, Count=group.Count() }).OrderByDescending(item=>item.Key.Year).ThenByDescending(item=>item.Key.Month).ThenByDescending(item=>item.Key.Day).ThenByDescending(item=>item.Key.Hour).ThenByDescending(item=>item.Key.Minute).Take(840).ToListAsync(cancellationToken);
            points.AddRange(buckets.Select(item => (new DateTime(item.Key.Year,item.Key.Month,item.Key.Day,item.Key.Hour,item.Key.Minute,0),item.Key.Level,item.Count)));
        }
        else if (timeBucket == "hour")
        {
            var buckets = await entries.GroupBy(item => new { item.TimestampUtc.Year, item.TimestampUtc.Month, item.TimestampUtc.Day, item.TimestampUtc.Hour, item.Level }).Select(group => new { group.Key, Count=group.Count() }).OrderByDescending(item=>item.Key.Year).ThenByDescending(item=>item.Key.Month).ThenByDescending(item=>item.Key.Day).ThenByDescending(item=>item.Key.Hour).Take(840).ToListAsync(cancellationToken);
            points.AddRange(buckets.Select(item => (new DateTime(item.Key.Year,item.Key.Month,item.Key.Day,item.Key.Hour,0,0),item.Key.Level,item.Count)));
        }
        else if (timeBucket is "month" or "year")
        {
            if (timeBucket == "year")
            {
                var buckets = await entries.GroupBy(item => new { item.TimestampUtc.Year, item.Level }).Select(group => new { group.Key, Count=group.Count() }).OrderByDescending(item=>item.Key.Year).Take(840).ToListAsync(cancellationToken);
                points.AddRange(buckets.Select(item => (new DateTime(item.Key.Year,1,1),item.Key.Level,item.Count)));
            }
            else
            {
                var buckets = await entries.GroupBy(item => new { item.TimestampUtc.Year, item.TimestampUtc.Month, item.Level }).Select(group => new { group.Key, Count=group.Count() }).OrderByDescending(item=>item.Key.Year).ThenByDescending(item=>item.Key.Month).Take(840).ToListAsync(cancellationToken);
                points.AddRange(buckets.Select(item => (new DateTime(item.Key.Year,item.Key.Month,1),item.Key.Level,item.Count)));
            }
        }
        else
        {
            var bucketLimit = timeBucket == "week" ? 6_000 : 840;
            var buckets = await entries.GroupBy(item => new { Date=item.TimestampUtc.Date, item.Level }).Select(group => new { group.Key, Count=group.Count() }).OrderByDescending(item=>item.Key.Date).Take(bucketLimit).ToListAsync(cancellationToken);
            points.AddRange(buckets.Select(item => (item.Key.Date,item.Key.Level,item.Count)));
            if (timeBucket == "week") points = points.GroupBy(item => new { Date=StartOfWeek(item.Date),item.Level }).Select(group => (group.Key.Date,group.Key.Level,group.Sum(item=>item.Count))).ToList();
        }
        if (points.Count == 0) return [];
        var firstDate = NormalizeBucketDate((from?.UtcDateTime ?? points.Min(item=>item.Date)),timeBucket);
        var lastDate = NormalizeBucketDate((to?.UtcDateTime ?? points.Max(item=>item.Date)),timeBucket);
        if (lastDate < firstDate) (firstDate,lastDate)=(lastDate,firstDate);
        var dates = new List<DateTime>();
        for (var date=firstDate;date<=lastDate&&dates.Count<10_000;date=NextBucket(date,timeBucket)) dates.Add(date);
        if (dates.Count>120) dates=dates.TakeLast(120).ToList();
        var dateSet=dates.ToHashSet();
        string Format(DateTime date)=>timeBucket switch{"minute"=>date.ToString("MMM d HH:mm",CultureInfo.InvariantCulture),"hour"=>date.ToString("MMM d HH:00",CultureInfo.InvariantCulture),"month"=>date.ToString("MMM yyyy",CultureInfo.InvariantCulture),"year"=>date.ToString("yyyy",CultureInfo.InvariantCulture),_=>date.ToString("MMM d",CultureInfo.InvariantCulture)};
        return points.Where(item=>dateSet.Contains(item.Date)).GroupBy(item=>item.Level).Select(series=>new LogChartSeries(series.Key,dates.Select(date=>new LogChartPoint(Format(date),series.Where(item=>item.Date==date).Sum(item=>item.Count))).ToList())).OrderBy(series=>series.Level).ToList();
    }

    private static DateTime NormalizeBucketDate(DateTime date,string bucket)=>bucket switch{"minute"=>new DateTime(date.Year,date.Month,date.Day,date.Hour,date.Minute,0),"hour"=>new DateTime(date.Year,date.Month,date.Day,date.Hour,0,0),"week"=>StartOfWeek(date),"month"=>new DateTime(date.Year,date.Month,1),"year"=>new DateTime(date.Year,1,1),_=>date.Date};
    private static DateTime NextBucket(DateTime date,string bucket)=>bucket switch{"minute"=>date.AddMinutes(1),"hour"=>date.AddHours(1),"week"=>date.AddDays(7),"month"=>date.AddMonths(1),"year"=>date.AddYears(1),_=>date.AddDays(1)};

    private static DateTime StartOfWeek(DateTime date)
    {
        var difference=(7+((int)date.DayOfWeek-(int)DayOfWeek.Monday))%7;
        return date.Date.AddDays(-difference);
    }

    private static async Task<string> ResolveTimeBucketAsync(string requested, IQueryable<IndexedLogEntry> entries, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (requested is "minute" or "hour" or "day" or "week" or "month" or "year") return requested;
        DateTimeOffset start;
        DateTimeOffset end;
        if (!from.HasValue && !to.HasValue)
        {
            var range = await entries.GroupBy(_ => 1).Select(group => new { Min = group.Min(item => item.TimestampUtc), Max = group.Max(item => item.TimestampUtc) }).SingleOrDefaultAsync(cancellationToken);
            if (range is null) return "day";
            start = new DateTimeOffset(DateTime.SpecifyKind(range.Min, DateTimeKind.Utc));
            end = new DateTimeOffset(DateTime.SpecifyKind(range.Max, DateTimeKind.Utc));
        }
        else
        {
            start = from ?? DateTimeOffset.UtcNow.AddDays(-1);
            end = to ?? DateTimeOffset.UtcNow;
        }
        var span = end - start;
        if (span <= TimeSpan.FromHours(12)) return "minute";
        if (span <= TimeSpan.FromDays(2)) return "hour";
        if (span <= TimeSpan.FromDays(45)) return "day";
        if (span <= TimeSpan.FromDays(730)) return "week";
        return span <= TimeSpan.FromDays(3650) ? "month" : "year";
    }

    private async Task<long> ReindexFileAsync(LogSource source, FileInfo info, IndexedLogFile? record, string? knownContentHash, CancellationToken cancellationToken)
    {
        var previousLength = record?.Length ?? 0;
        var append = record is not null && record.ParserVersion == CurrentParserVersion && info.Length > previousLength && await EndsWithLineBreakAsync(info.FullName, previousLength, cancellationToken) && await IsUtf8CompatibleAsync(info.FullName, cancellationToken);
        var contentHash = append ? null : knownContentHash ?? await ComputeContentHashAsync(info.FullName, cancellationToken);
        var lastWriteUtc = NormalizeUtc(info.LastWriteTimeUtc);
        await using var transaction = await indexDatabase.Database.BeginTransactionAsync(cancellationToken);
        if (record is null)
        {
            record = new IndexedLogFile { LogSourceId = source.Id, Path = info.FullName, FileName = info.Name, Length = info.Length, LastWriteUtc = lastWriteUtc, IndexedUtc = DateTime.UtcNow, ParserVersion = CurrentParserVersion, ContentHash = contentHash };
            indexDatabase.IndexedLogFiles.Add(record);
            await indexDatabase.SaveChangesAsync(cancellationToken);
        }
        else
        {
            if (!append) await indexDatabase.IndexedLogEntries.Where(item => item.IndexedLogFileId == record.Id).ExecuteDeleteAsync(cancellationToken);
            record.Length = info.Length; record.LastWriteUtc = lastWriteUtc; record.IndexedUtc = DateTime.UtcNow; record.ParserVersion = CurrentParserVersion; record.ContentHash = contentHash;
            await indexDatabase.SaveChangesAsync(cancellationToken);
        }

        var filenameTimestamp = ReadTimestampFromFileName(info.Name);
        var fileTimestamp = filenameTimestamp ?? lastWriteUtc;
        var batch = new List<IndexedLogEntry>(BatchSize);
        var carriedEntry = append
            ? await indexDatabase.IndexedLogEntries.Where(item => item.IndexedLogFileId == record.Id).OrderByDescending(item => item.LineNumber).ThenByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken)
            : null;
        var carriedEntryChanged = false;
        long lineNumber = carriedEntry?.LineNumber ?? 0;
        long indexed = 0;
        LogEntry? pending = null;
        long pendingLineNumber = 0;
        async Task FlushPendingAsync()
        {
            if (pending is null) return;
            var timestamp = ResolveTimestamp(pending.Timestamp, fileTimestamp, source.TimestampFormat, out var hasCompleteContentDate);
            batch.Add(new IndexedLogEntry { IndexedLogFileId = record.Id, LogSourceId = source.Id, TimestampUtc = timestamp, TimestampSource = hasCompleteContentDate ? "content" : filenameTimestamp.HasValue ? "filename" : "file", Level = pending.Level, Message = pending.Message, LineNumber = pendingLineNumber });
            pending = null;
            if (batch.Count < BatchSize) return;
            await InsertBatchAsync(batch, transaction.GetDbTransaction(), cancellationToken); indexed += batch.Count; batch.Clear();
        }
        await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (append) stream.Seek(previousLength, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parsed = parser.Parse(line, null, null, source.Format, source.CustomPattern).Entries.FirstOrDefault();
            if (parsed is null) continue;
            var previous = pending ?? (carriedEntry is null ? null : new LogEntry(string.Empty, carriedEntry.Level, carriedEntry.Message, carriedEntry.Message));
            if (previous is not null && LogContinuationHelper.IsContinuationLine(line, parsed, previous))
            {
                if (pending is not null)
                    pending = pending with { Message = $"{pending.Message}{Environment.NewLine}{line.TrimEnd()}", Raw = $"{pending.Raw}{Environment.NewLine}{line}" };
                else if (carriedEntry is not null)
                {
                    carriedEntry.Message = $"{carriedEntry.Message}{Environment.NewLine}{line.TrimEnd()}";
                    carriedEntryChanged = true;
                }
                continue;
            }
            if (carriedEntryChanged) await indexDatabase.SaveChangesAsync(cancellationToken);
            carriedEntry = null;
            carriedEntryChanged = false;
            await FlushPendingAsync();
            pending = parsed;
            pendingLineNumber = lineNumber;
        }
        if (carriedEntryChanged) await indexDatabase.SaveChangesAsync(cancellationToken);
        await FlushPendingAsync();
        if (batch.Count > 0) { await InsertBatchAsync(batch, transaction.GetDbTransaction(), cancellationToken); indexed += batch.Count; }
        var indexedLength = stream.Position;
        var indexedWriteTime = NormalizeUtc(File.GetLastWriteTimeUtc(info.FullName));
        var finalContentHash = append ? await ComputeContentHashAsync(info.FullName, cancellationToken) : contentHash;
        await indexDatabase.IndexedLogFiles.Where(item => item.Id == record.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Length, indexedLength)
            .SetProperty(item => item.LastWriteUtc, indexedWriteTime)
            .SetProperty(item => item.IndexedUtc, DateTime.UtcNow)
            .SetProperty(item => item.ContentHash, finalContentHash), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        indexDatabase.ChangeTracker.Clear();
        return indexed;
    }

    private static async Task<bool> EndsWithLineBreakAsync(string path, long length, CancellationToken cancellationToken)
    {
        if (length == 0) return true;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
        stream.Seek(length - 1, SeekOrigin.Begin);
        var buffer = new byte[1];
        return await stream.ReadAsync(buffer, cancellationToken) == 1 && buffer[0] == (byte)'\n';
    }

    private static async Task<bool> IsUtf8CompatibleAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 3, FileOptions.Asynchronous);
        var bom = new byte[3];
        var count = await stream.ReadAsync(bom, cancellationToken);
        return count < 2 || !(bom[0] == 0xFF && bom[1] == 0xFE) && !(bom[0] == 0xFE && bom[1] == 0xFF);
    }

    private async Task InsertBatchAsync(IReadOnlyList<IndexedLogEntry> batch, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = indexDatabase.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        var values = new List<string>(batch.Count);
        for (var index = 0; index < batch.Count; index++)
        {
            var entry = batch[index];
            values.Add($"($file{index},$source{index},$time{index},$origin{index},$level{index},$message{index},$line{index})");
            AddParameter(command, $"$file{index}", entry.IndexedLogFileId);
            AddParameter(command, $"$source{index}", entry.LogSourceId);
            AddParameter(command, $"$time{index}", entry.TimestampUtc);
            AddParameter(command, $"$origin{index}", entry.TimestampSource);
            AddParameter(command, $"$level{index}", entry.Level);
            AddParameter(command, $"$message{index}", entry.Message);
            AddParameter(command, $"$line{index}", entry.LineNumber);
        }
        command.CommandText = $"INSERT INTO IndexedLogEntries (IndexedLogFileId,LogSourceId,TimestampUtc,TimestampSource,Level,Message,LineNumber) VALUES {string.Join(',', values)}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }

    private static IEnumerable<string> DiscoverFiles(string configuredPath, bool includeSubdirectories)
    {
        if (File.Exists(configuredPath)) { yield return configuredPath; yield break; }
        if (!Directory.Exists(configuredPath)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = includeSubdirectories, IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(configuredPath, "*", options))
            if (SupportedExtensions.Contains(Path.GetExtension(file))) yield return Path.GetFullPath(file);
    }

    private static async Task<string> ComputeContentHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static bool SameUtc(DateTime left, DateTime right) => Math.Abs((NormalizeUtc(left) - NormalizeUtc(right)).TotalMilliseconds) < 2;

    private static DateTime ResolveTimestamp(string value, DateTime fallback, string? customFormat, out bool hasCompleteContentDate)
    {
        hasCompleteContentDate = false;
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var normalized = value.Replace(',', '.');
        var containsYear = Regex.IsMatch(normalized, @"\b(?:19|20)\d{2}\b");
        if (!string.IsNullOrWhiteSpace(customFormat) && TryParseExactTimestamp(normalized, customFormat.Trim(), fallback, out var customTimestamp, out hasCompleteContentDate)) return customTimestamp;
        if (TryParseKnownTimestamp(normalized, fallback, out var knownTimestamp, out hasCompleteContentDate)) return knownTimestamp;
        if (containsYear && DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var timestamp))
        {
            hasCompleteContentDate = true;
            return timestamp.UtcDateTime;
        }
        var timeMatch = TimeValuePattern().Match(value.Trim());
        if (timeMatch.Success)
        {
            var hour=int.Parse(timeMatch.Groups["hour"].Value,CultureInfo.InvariantCulture);
            var minute=int.Parse(timeMatch.Groups["minute"].Value,CultureInfo.InvariantCulture);
            var second=int.Parse(timeMatch.Groups["second"].Value,CultureInfo.InvariantCulture);
            var period=timeMatch.Groups["period"].Value.ToUpperInvariant();
            if(period=="AM"&&hour==12)hour=0;else if(period=="PM"&&hour<12)hour+=12;
            var fraction=timeMatch.Groups["fraction"].Value;
            var milliseconds=fraction.Length==0?0:int.Parse(fraction.PadRight(3,'0')[..3],CultureInfo.InvariantCulture);
            if(hour<24&&minute<60&&second<60)
            {
                var localFallback=DateTime.SpecifyKind(fallback,DateTimeKind.Utc).ToLocalTime();
                return CombineWithFallbackDate(fallback,localFallback.Month,localFallback.Day,hour,minute,second,milliseconds);
            }
        }
        if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var partial))
        {
            try
            {
                var hasMonthOrDay = Regex.IsMatch(normalized, @"[A-Za-z]{3}|\d{1,2}[-/]\d{1,2}");
                var localFallback=DateTime.SpecifyKind(fallback,DateTimeKind.Utc).ToLocalTime();
                var month = hasMonthOrDay ? partial.Month : localFallback.Month;
                var day = hasMonthOrDay ? partial.Day : localFallback.Day;
                return CombineWithFallbackDate(fallback,month,day,partial.Hour,partial.Minute,partial.Second,partial.Millisecond);
            }
            catch (ArgumentOutOfRangeException) { return fallback; }
        }
        return fallback;
    }

    private static bool TryParseKnownTimestamp(string value, DateTime fallback, out DateTime timestamp, out bool hasCompleteDate)
    {
        string[] fullFormats =
        [
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.fffffff",
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.fffffff",
            "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss.fff",
            "yyyyMMdd HHmmss", "yyyyMMddHHmmss", "yyyyMMdd_HHmmss", "yyyyMMdd-HHmmss",
            "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy hh:mm:ss tt",
            "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy hh:mm:ss tt",
            "dd-MMM-yyyy HH:mm:ss", "MMM dd yyyy HH:mm:ss", "MMM d yyyy HH:mm:ss"
        ];
        foreach (var format in fullFormats)
            if (TryParseExactTimestamp(value, format, fallback, out timestamp, out hasCompleteDate)) return true;
        timestamp = default;
        hasCompleteDate = false;
        return false;
    }

    private static bool TryParseExactTimestamp(string value, string format, DateTime fallback, out DateTime timestamp, out bool hasCompleteDate)
    {
        var hasYear = Regex.IsMatch(format, "y+", RegexOptions.IgnoreCase);
        var hasMonth = Regex.IsMatch(format, "M+");
        var hasDay = Regex.IsMatch(format, "d+");
        hasCompleteDate = hasYear && hasMonth && hasDay;
        if (hasCompleteDate && DateTimeOffset.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var offset))
        {
            timestamp = offset.UtcDateTime;
            return true;
        }
        if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            timestamp = hasCompleteDate
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime()
                : CombineWithFallbackDate(fallback, hasMonth ? parsed.Month : DateTime.SpecifyKind(fallback, DateTimeKind.Utc).ToLocalTime().Month, hasDay ? parsed.Day : DateTime.SpecifyKind(fallback, DateTimeKind.Utc).ToLocalTime().Day, parsed.Hour, parsed.Minute, parsed.Second, parsed.Millisecond);
            return true;
        }
        timestamp = default;
        return false;
    }

    private static DateTime CombineWithFallbackDate(DateTime fallbackUtc,int month,int day,int hour,int minute,int second,int millisecond)
    {
        var localFallback=DateTime.SpecifyKind(fallbackUtc,DateTimeKind.Utc).ToLocalTime();
        var local=new DateTime(localFallback.Year,month,day,hour,minute,second,millisecond,DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local,TimeZoneInfo.Local);
    }

    private static DateTime? ReadTimestampFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (Match candidate in Regex.Matches(name, @"(?<!\d)(?:20\d{2}[-_.]?\d{2}[-_.]?\d{2}(?:[-_. T]?\d{2}[-_.:]?\d{2}[-_.:]?\d{2})?|\d{2}[-_.]\d{2}[-_.]20\d{2}(?:[-_. T]?\d{2}[-_.:]?\d{2}[-_.:]?\d{2})?)(?!\d)"))
            if (TryParseFilenameTimestamp(candidate.Value, out var candidateTimestamp)) return candidateTimestamp;
        var match = FileTimestampPattern().Match(name);
        if (match.Success)
        {
            var digits = Regex.Replace(match.Groups["date"].Value + match.Groups["time"].Value, "[^0-9]", string.Empty);
            if (DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)) return value.ToUniversalTime();
        }
        var unixMatch = UnixTimestampPattern().Match(name);
        if (unixMatch.Success && long.TryParse(unixMatch.Groups["unix"].Value, out var unix))
        {
            try { return (unixMatch.Groups["unix"].Value.Length == 13 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix)).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { }
        }
        var dateMatch = FileDatePattern().Match(name);
        if (!dateMatch.Success) return null;
        var dateDigits = Regex.Replace(dateMatch.Groups["date"].Value, "[^0-9]", string.Empty);
        return DateTime.TryParseExact(dateDigits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date.ToUniversalTime() : null;
    }

    private static bool TryParseFilenameTimestamp(string value, out DateTime timestamp)
    {
        var normalized = value.Replace('.', '-').Replace('T', ' ');
        normalized = Regex.Replace(normalized, @"(?<=\d{4}-\d{2}-\d{2})[_-](?=\d{2})|(?<=\d{2}-\d{2}-\d{4})[_-](?=\d{2})|(?<=\d{8})[_-](?=\d{6})", " ");
        normalized = normalized.Replace('_', '-');
        string[] formats =
        [
            "yyyyMMdd", "yyyyMMdd HHmmss", "yyyyMMdd-HHmmss", "yyyy-MM-dd", "yyyy-MM-dd HH-mm-ss", "yyyy-MM-dd HH:mm:ss",
            "MM-dd-yyyy", "MM-dd-yyyy HH-mm-ss", "MM-dd-yyyy HH:mm:ss",
            "dd-MM-yyyy", "dd-MM-yyyy HH-mm-ss", "dd-MM-yyyy HH:mm:ss"
        ];
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(normalized, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                timestamp = parsed.ToUniversalTime();
                return true;
            }
        }
        timestamp = default;
        return false;
    }
}
