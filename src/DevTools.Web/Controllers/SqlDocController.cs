using DevTools.Shared.Models;
using DevTools.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DevTools.Web.Controllers;

[ApiController]
[Route("api/sql-doc")]
public sealed class SqlDocController(ISqlDocService sqlDocService) : Controller
{
    [HttpGet("sources")]
    public async Task<ApiResult<IReadOnlyList<SqlDocSourceDto>>> Sources(CancellationToken cancellationToken) =>
        ApiResult<IReadOnlyList<SqlDocSourceDto>>.Ok(await sqlDocService.ListSourcesAsync(cancellationToken));

    [HttpPost("sources")]
    public async Task<ActionResult<ApiResult<SqlDocSourceDto>>> CreateSource([FromBody] CreateSqlDocSourceRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(ApiResult<SqlDocSourceDto>.Ok(await sqlDocService.CreateSourceAsync(request, cancellationToken))); }
        catch (ArgumentException exception) { return BadRequest(ApiResult<SqlDocSourceDto>.Fail(exception.Message)); }
        catch (InvalidOperationException exception) { return BadRequest(ApiResult<SqlDocSourceDto>.Fail(exception.Message)); }
    }

    [HttpPut("sources/{id:int}")]
    public async Task<ActionResult<ApiResult<SqlDocSourceDto>>> UpdateSource(int id, [FromBody] UpdateSqlDocSourceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await sqlDocService.UpdateSourceAsync(id, request, cancellationToken);
            return updated is null ? NotFound(ApiResult<SqlDocSourceDto>.Fail("Source not found.")) : Ok(ApiResult<SqlDocSourceDto>.Ok(updated));
        }
        catch (ArgumentException exception) { return BadRequest(ApiResult<SqlDocSourceDto>.Fail(exception.Message)); }
        catch (InvalidOperationException exception) { return BadRequest(ApiResult<SqlDocSourceDto>.Fail(exception.Message)); }
    }

    [HttpDelete("sources/{id:int}")]
    public async Task<IActionResult> DeleteSource(int id, CancellationToken cancellationToken) =>
        await sqlDocService.DeleteSourceAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("databases")]
    public async Task<ActionResult<ApiResult<SqlDocDatabaseList>>> Databases([FromBody] SqlDocConnectionRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(ApiResult<SqlDocDatabaseList>.Ok(new SqlDocDatabaseList(await sqlDocService.ListDatabasesAsync(request, cancellationToken)))); }
        catch (ArgumentException exception) { return BadRequest(ApiResult<SqlDocDatabaseList>.Fail(exception.Message)); }
        catch (InvalidOperationException exception) { return BadRequest(ApiResult<SqlDocDatabaseList>.Fail(exception.Message)); }
        catch (System.Data.Common.DbException exception) { return BadRequest(ApiResult<SqlDocDatabaseList>.Fail($"Unable to list databases: {exception.Message}")); }
    }

    [HttpGet("sources/{id:int}/schema")]
    public async Task<ActionResult<ApiResult<SqlDocSchema>>> Schema(int id, CancellationToken cancellationToken)
    {
        try
        {
            var schema = await sqlDocService.InspectAsync(id, cancellationToken);
            return schema is null ? NotFound(ApiResult<SqlDocSchema>.Fail("Source not found.")) : Ok(ApiResult<SqlDocSchema>.Ok(schema));
        }
        catch (InvalidOperationException exception) { return BadRequest(ApiResult<SqlDocSchema>.Fail(exception.Message)); }
        catch (System.Data.Common.DbException exception) { return BadRequest(ApiResult<SqlDocSchema>.Fail($"Unable to inspect the database: {exception.Message}")); }
    }

    [HttpPost("sources/{id:int}/docx")]
    public async Task<IActionResult> DownloadDocx(int id, [FromBody] SqlDocGenerateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var document = await sqlDocService.GenerateDocxAsync(id, request, cancellationToken);
            return document is null ? NotFound() : File(document.Value.Content, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", document.Value.FileName);
        }
        catch (InvalidOperationException exception) { return BadRequest(exception.Message); }
        catch (System.Data.Common.DbException exception) { return BadRequest($"Unable to generate documentation: {exception.Message}"); }
    }

    [HttpGet("sources/{id:int}/diagrams")]
    public async Task<ApiResult<IReadOnlyList<SqlDocDiagramDto>>> Diagrams(int id, CancellationToken cancellationToken) =>
        ApiResult<IReadOnlyList<SqlDocDiagramDto>>.Ok(await sqlDocService.ListDiagramsAsync(id, cancellationToken));

    [HttpGet("sources/{id:int}/diagrams/{diagramId:int}")]
    public async Task<ActionResult<ApiResult<SqlDocDiagramDto>>> Diagram(int id, int diagramId, CancellationToken cancellationToken)
    {
        var diagram = await sqlDocService.GetDiagramAsync(id, diagramId, cancellationToken);
        return diagram is null ? NotFound(ApiResult<SqlDocDiagramDto>.Fail("Diagram not found.")) : Ok(ApiResult<SqlDocDiagramDto>.Ok(diagram));
    }

    [HttpPost("sources/{id:int}/diagrams")]
    public async Task<ActionResult<ApiResult<SqlDocDiagramDto>>> SaveDiagram(int id, [FromBody] SaveSqlDocDiagramRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var diagram = await sqlDocService.SaveDiagramAsync(id, request, cancellationToken);
            return diagram is null ? NotFound(ApiResult<SqlDocDiagramDto>.Fail("Source not found.")) : Ok(ApiResult<SqlDocDiagramDto>.Ok(diagram));
        }
        catch (ArgumentException exception) { return BadRequest(ApiResult<SqlDocDiagramDto>.Fail(exception.Message)); }
    }
}
