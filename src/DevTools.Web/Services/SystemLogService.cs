using DevTools.Shared.Models;
using DevTools.Web.Data;
using DevTools.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Services;

public interface ISystemLogService
{
    Task<IReadOnlyList<LogSystemDto>> GetSystemsAsync(CancellationToken cancellationToken);
    Task<LogSystemDto> CreateSystemAsync(string name, CancellationToken cancellationToken);
    Task<LogSourceDto> AddSourceAsync(int systemId, string moduleName, string path, string format, string? customPattern, string? timestampFormat, bool includeSubdirectories, CancellationToken cancellationToken);
    Task<LogSourceDto?> UpdateSourceAsync(int id, string moduleName, string path, string format, string? customPattern, string? timestampFormat, bool includeSubdirectories, CancellationToken cancellationToken);
    Task<bool> DeleteSystemAsync(int id, CancellationToken cancellationToken);
    Task<bool> DeleteSourceAsync(int id, CancellationToken cancellationToken);
}

public sealed class SystemLogService(DevToolsDbContext database, LogIndexDbContext indexDatabase) : ISystemLogService
{

    public async Task<IReadOnlyList<LogSystemDto>> GetSystemsAsync(CancellationToken cancellationToken) =>
        await database.LogSystems.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new LogSystemDto(item.Id, item.Name, item.Sources.OrderBy(source => source.ModuleName)
                .Select(source => new LogSourceDto(source.Id, source.ModuleName, source.Path, source.Format, source.CustomPattern, source.TimestampFormat, source.IncludeSubdirectories)).ToList()))
            .ToListAsync(cancellationToken);

    public async Task<LogSystemDto> CreateSystemAsync(string name, CancellationToken cancellationToken)
    {
        var entity = new LogSystem { Name = name.Trim() };
        database.LogSystems.Add(entity);
        await database.SaveChangesAsync(cancellationToken);
        return new LogSystemDto(entity.Id, entity.Name, []);
    }

    public async Task<LogSourceDto> AddSourceAsync(int systemId, string moduleName, string path, string format, string? customPattern, string? timestampFormat, bool includeSubdirectories, CancellationToken cancellationToken)
    {
        if (!await database.LogSystems.AnyAsync(item => item.Id == systemId, cancellationToken))
            throw new KeyNotFoundException("System not found.");
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        var entity = new LogSource { LogSystemId = systemId, ModuleName = moduleName.Trim(), Path = fullPath, Format = format, CustomPattern = customPattern, TimestampFormat = NormalizeTimestampFormat(timestampFormat), IncludeSubdirectories = includeSubdirectories };
        database.LogSources.Add(entity);
        await database.SaveChangesAsync(cancellationToken);
        return new LogSourceDto(entity.Id, entity.ModuleName, entity.Path, entity.Format, entity.CustomPattern, entity.TimestampFormat, entity.IncludeSubdirectories);
    }

    public async Task<LogSourceDto?> UpdateSourceAsync(int id, string moduleName, string path, string format, string? customPattern, string? timestampFormat, bool includeSubdirectories, CancellationToken cancellationToken)
    {
        var entity = await database.LogSources.FindAsync([id], cancellationToken);
        if (entity is null) return null;
        await indexDatabase.IndexedLogFiles.Where(item => item.LogSourceId == id).ExecuteDeleteAsync(cancellationToken);
        entity.ModuleName = moduleName.Trim();
        entity.Path = NormalizePath(path);
        entity.Format = format;
        entity.CustomPattern = customPattern;
        entity.TimestampFormat = NormalizeTimestampFormat(timestampFormat);
        entity.IncludeSubdirectories = includeSubdirectories;
        await database.SaveChangesAsync(cancellationToken);
        return new LogSourceDto(entity.Id, entity.ModuleName, entity.Path, entity.Format, entity.CustomPattern, entity.TimestampFormat, entity.IncludeSubdirectories);
    }

    public async Task<bool> DeleteSystemAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await database.LogSystems.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        var sourceIds = await database.LogSources.Where(item => item.LogSystemId == id).Select(item => item.Id).ToListAsync(cancellationToken);
        await indexDatabase.IndexedLogFiles.Where(item => sourceIds.Contains(item.LogSourceId)).ExecuteDeleteAsync(cancellationToken);
        database.Remove(entity); await database.SaveChangesAsync(cancellationToken); return true;
    }

    public async Task<bool> DeleteSourceAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await database.LogSources.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        await indexDatabase.IndexedLogFiles.Where(item => item.LogSourceId == id).ExecuteDeleteAsync(cancellationToken);
        database.Remove(entity); await database.SaveChangesAsync(cancellationToken); return true;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    private static string? NormalizeTimestampFormat(string? format) => string.IsNullOrWhiteSpace(format) ? null : format.Trim();
}
