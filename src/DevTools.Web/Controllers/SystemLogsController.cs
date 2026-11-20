using DevTools.Shared.Helpers;
using DevTools.Application.Services;
using DevTools.Shared.Models;
using DevTools.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Controllers;

[ApiController]
[Route("api/system-logs")]
public sealed class SystemLogsController(ISystemLogService service, ILogIndexService indexService, ILogIndexJobService indexJobService, ILogIndexClearJobService clearJobService, ILogParserService parser) : Controller
{
    [HttpGet("systems")]
    public async Task<ApiResult<IReadOnlyList<LogSystemDto>>> GetSystems(CancellationToken cancellationToken) =>
        ApiResult<IReadOnlyList<LogSystemDto>>.Ok(await service.GetSystemsAsync(cancellationToken));

    [HttpPost("systems")]
    public async Task<ActionResult<ApiResult<LogSystemDto>>> CreateSystem(CreateSystemRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            return BadRequest(ApiResult<LogSystemDto>.Fail("System name is required and must be 120 characters or fewer."));
        try { return Ok(ApiResult<LogSystemDto>.Ok(await service.CreateSystemAsync(request.Name, cancellationToken))); }
        catch (DbUpdateException) { return Conflict(ApiResult<LogSystemDto>.Fail("A system with this name already exists.")); }
    }

    [HttpPost("systems/{systemId:int}/sources")]
    public async Task<ActionResult<ApiResult<LogSourceDto>>> AddSource(int systemId, CreateLogSourceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ModuleName) || request.ModuleName.Trim().Length > 120)
            return BadRequest(ApiResult<LogSourceDto>.Fail("Module name is required and must be 120 characters or fewer."));
        if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Trim().Length > 2048)
            return BadRequest(ApiResult<LogSourceDto>.Fail("A valid log file path is required."));
        var formatError = ValidateFormat(request.Format, request.CustomPattern, request.TimestampFormat);
        if (formatError is not null) return BadRequest(ApiResult<LogSourceDto>.Fail(formatError));
        var filterError = LogFileNameFilter.Validate(request.FileNameFilter);
        if (filterError is not null) return BadRequest(ApiResult<LogSourceDto>.Fail(filterError));
        try { return Ok(ApiResult<LogSourceDto>.Ok(await service.AddSourceAsync(systemId, request.ModuleName, request.Path, request.Format.ToLowerInvariant(), request.CustomPattern, request.TimestampFormat, request.IncludeSubdirectories, request.FileNameFilter, cancellationToken))); }
        catch (KeyNotFoundException ex) { return NotFound(ApiResult<LogSourceDto>.Fail(ex.Message)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return BadRequest(ApiResult<LogSourceDto>.Fail("The log path is not valid.")); }
    }

    [HttpPut("sources/{id:int}")]
    public async Task<ActionResult<ApiResult<LogSourceDto>>> UpdateSource(int id, UpdateLogSourceRequest request, CancellationToken cancellationToken)
    {
        var validationError = ValidateSource(request.ModuleName, request.Path);
        if (validationError is not null) return BadRequest(ApiResult<LogSourceDto>.Fail(validationError));
        try
        {
            var formatError = ValidateFormat(request.Format, request.CustomPattern, request.TimestampFormat);
            if (formatError is not null) return BadRequest(ApiResult<LogSourceDto>.Fail(formatError));
            var filterError = LogFileNameFilter.Validate(request.FileNameFilter);
            if (filterError is not null) return BadRequest(ApiResult<LogSourceDto>.Fail(filterError));
            var source = await service.UpdateSourceAsync(id, request.ModuleName!, request.Path!, request.Format.ToLowerInvariant(), request.CustomPattern, request.TimestampFormat, request.IncludeSubdirectories, request.FileNameFilter, cancellationToken);
            return source is null ? NotFound(ApiResult<LogSourceDto>.Fail("Module not found.")) : Ok(ApiResult<LogSourceDto>.Ok(source));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return BadRequest(ApiResult<LogSourceDto>.Fail("The log path is not valid.")); }
    }

    [HttpDelete("systems/{id:int}")]
    public async Task<IActionResult> DeleteSystem(int id, CancellationToken cancellationToken) =>
        await service.DeleteSystemAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("sources/{id:int}")]
    public async Task<IActionResult> DeleteSource(int id, CancellationToken cancellationToken) =>
        await service.DeleteSourceAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("systems/{systemId:int}/refresh")]
    public ActionResult<ApiResult<LogRefreshStatus>> RefreshIndex(int systemId, [FromQuery] int? sourceId)
    {
        if (clearJobService.GetLatestForSystem(systemId)?.Status == "running")
            return Conflict(ApiResult<LogRefreshStatus>.Fail("The index is currently being cleared and compacted."));
        try { return Ok(ApiResult<LogRefreshStatus>.Ok(indexJobService.Start(systemId, sourceId))); }
        catch (KeyNotFoundException ex) { return NotFound(ApiResult<LogRefreshStatus>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(ApiResult<LogRefreshStatus>.Fail(ex.Message)); }
    }

    [HttpGet("systems/{systemId:int}/refresh/status")]
    public ActionResult<ApiResult<LogRefreshStatus?>> SystemRefreshStatus(int systemId) =>
        Ok(ApiResult<LogRefreshStatus?>.Ok(indexJobService.GetLatestForSystem(systemId)));

    [HttpPost("systems/{systemId:int}/clear-index")]
    public ActionResult<ApiResult<LogIndexClearStatus>> ClearIndex(int systemId, [FromQuery] int? sourceId)
    {
        if (indexJobService.GetLatestForSystem(systemId)?.Status == "running")
            return Conflict(ApiResult<LogIndexClearStatus>.Fail("Cancel the active indexing job before clearing this index."));
        try { return Ok(ApiResult<LogIndexClearStatus>.Ok(clearJobService.Start(systemId, sourceId))); }
        catch (InvalidOperationException ex) { return Conflict(ApiResult<LogIndexClearStatus>.Fail(ex.Message)); }
    }

    [HttpGet("systems/{systemId:int}/clear-index/status")]
    public ActionResult<ApiResult<LogIndexClearStatus?>> SystemClearStatus(int systemId) =>
        Ok(ApiResult<LogIndexClearStatus?>.Ok(clearJobService.GetLatestForSystem(systemId)));

    [HttpGet("clear-index/{jobId}")]
    public ActionResult<ApiResult<LogIndexClearStatus>> ClearStatus(string jobId)
    {
        var status = clearJobService.Get(jobId);
        return status is null ? NotFound(ApiResult<LogIndexClearStatus>.Fail("Clear-index job not found.")) : Ok(ApiResult<LogIndexClearStatus>.Ok(status));
    }

    [HttpPost("parse-preview")]
    public ActionResult<ApiResult<LogParseResult>> ParsePreview([FromBody] LogPreviewRequest request)
    {
        var formatError = ValidateFormat(request.Format, request.CustomPattern, request.TimestampFormat);
        if (formatError is not null) return BadRequest(ApiResult<LogParseResult>.Fail(formatError));
        var input = string.IsNullOrWhiteSpace(request.Sample) ? string.Empty : request.Sample.Trim();
        if (input.Length > 20_000) input = input[..20_000];
        return Ok(ApiResult<LogParseResult>.Ok(parser.Parse(input, null, null, request.Format.ToLowerInvariant(), request.CustomPattern, pageSize: 25)));
    }

    [HttpGet("refresh/{jobId}")]
    public ActionResult<ApiResult<LogRefreshStatus>> RefreshStatus(string jobId)
    {
        var status = indexJobService.Get(jobId);
        return status is null ? NotFound(ApiResult<LogRefreshStatus>.Fail("Index job not found.")) : Ok(ApiResult<LogRefreshStatus>.Ok(status));
    }

    [HttpPost("refresh/{jobId}/cancel")]
    public ActionResult<ApiResult<LogRefreshStatus>> CancelRefresh(string jobId)
    {
        if (!indexJobService.Cancel(jobId)) return NotFound(ApiResult<LogRefreshStatus>.Fail("Index job not found."));
        return Ok(ApiResult<LogRefreshStatus>.Ok(indexJobService.Get(jobId)!));
    }

    [HttpGet("systems/{systemId:int}/analysis")]
    public async Task<IActionResult> Analysis(int systemId, [FromQuery] SystemLogQuery query, CancellationToken cancellationToken)
    {
        var searchError = ValidateSearch(query);
        if (searchError is not null) return BadRequest(new { error = searchError });
        var page = await indexService.QueryAsync(systemId, query, cancellationToken);
        return page is null ? NotFound() : PartialView("~/Views/Home/_SystemLogAnalysisResults.cshtml", page);
    }

    [HttpGet("systems/{systemId:int}/entries/{entryId:long}/message")]
    public async Task<IActionResult> FullMessage(int systemId, long entryId, CancellationToken cancellationToken)
    {
        var message = await indexService.GetMessageAsync(systemId, entryId, cancellationToken);
        return message is null ? NotFound() : PartialView("~/Views/Home/_LogMessage.cshtml", message);
    }

    [HttpGet("systems/{systemId:int}/entries/{entryId:long}/file")]
    public async Task<IActionResult> DownloadLogFile(int systemId, long entryId, CancellationToken cancellationToken)
    {
        var file = await indexService.GetLogFileAsync(systemId, entryId, cancellationToken);
        return file is null || !System.IO.File.Exists(file.Path)
            ? NotFound()
            : PhysicalFile(file.Path, "application/octet-stream", file.FileName, enableRangeProcessing: true);
    }

    private static string? ValidateSource(string? moduleName, string? path)
    {
        if (string.IsNullOrWhiteSpace(moduleName) || moduleName.Trim().Length > 120) return "Module name is required and must be 120 characters or fewer.";
        if (string.IsNullOrWhiteSpace(path) || path.Trim().Length > 2048) return "A valid log file or folder path is required.";
        return null;
    }

    private static string? ValidateSearch(SystemLogQuery query)
    {
        string[] supportedModes = ["contains", "notcontains", "regex"];
        if (!supportedModes.Contains(query.SearchMode, StringComparer.OrdinalIgnoreCase))
            return "Choose a supported message filter mode.";
        if (query.Search?.Length > 200) return "Message filters must be 200 characters or fewer.";
        if (!query.SearchMode.Equals("regex", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(query.Search)) return null;
        try
        {
            _ = new System.Text.RegularExpressions.Regex(
                query.Search,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException) { return "The message regex pattern is invalid."; }
        return null;
    }

    private static string? ValidateFormat(string? format, string? customPattern, string? timestampFormat)
    {
        string[] supported = ["auto", "text", "json", "custom"];
        if (string.IsNullOrWhiteSpace(format) || !supported.Contains(format, StringComparer.OrdinalIgnoreCase)) return "Choose a supported log reader format.";
        if (!string.IsNullOrWhiteSpace(timestampFormat) && timestampFormat.Trim().Length > 100) return "Timestamp format must be 100 characters or fewer.";
        if (!format.Equals("custom", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(customPattern)) return "A regex pattern is required for the custom reader.";
        if (customPattern.Length > 2000) return "The custom regex must be 2,000 characters or fewer.";
        try
        {
            var regex = new System.Text.RegularExpressions.Regex(customPattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(250));
            string[] requiredGroups = ["timestamp", "level", "message"];
            if (!requiredGroups.All(group => regex.GetGroupNames().Contains(group, StringComparer.Ordinal)))
                return "Custom regex must contain named timestamp, level, and message groups.";
        }
        catch (ArgumentException) { return "The custom regex pattern is invalid."; }
        return null;
    }
}
