using DevTools.Shared.Models;
using DevTools.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DevTools.Web.Controllers;

[ApiController]
[Route("api/database-compare")]
public sealed class DatabaseCompareController(IDatabaseCompareService compareService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResult<DatabaseCompareResult>>> Compare([FromBody] DatabaseCompareRequest request, CancellationToken cancellationToken)
    {
        if (request.LeftSourceId <= 0 || request.RightSourceId <= 0 || request.LeftSourceId == request.RightSourceId)
            return BadRequest(ApiResult<DatabaseCompareResult>.Fail("Choose two different saved database connections."));
        try { return Ok(ApiResult<DatabaseCompareResult>.Ok(await compareService.CompareAsync(request, cancellationToken))); }
        catch (KeyNotFoundException exception) { return NotFound(ApiResult<DatabaseCompareResult>.Fail(exception.Message)); }
        catch (InvalidOperationException exception) { return BadRequest(ApiResult<DatabaseCompareResult>.Fail(exception.Message)); }
        catch (System.Data.Common.DbException exception) { return BadRequest(ApiResult<DatabaseCompareResult>.Fail($"Unable to compare databases: {exception.Message}")); }
    }

    [HttpPost("apply")]
    public async Task<ActionResult<ApiResult<DatabaseCompareApplyResult>>> Apply([FromBody] DatabaseCompareApplyRequest request, CancellationToken cancellationToken)
    {
        if (request.LeftSourceId <= 0 || request.RightSourceId <= 0 || request.LeftSourceId == request.RightSourceId)
            return BadRequest(ApiResult<DatabaseCompareApplyResult>.Fail("Choose two different saved database connections."));
        if (request.ObjectKeys is null || request.ObjectKeys.Count == 0)
            return BadRequest(ApiResult<DatabaseCompareApplyResult>.Fail("Select at least one applicable schema change."));
        try { return Ok(ApiResult<DatabaseCompareApplyResult>.Ok(await compareService.ApplyAsync(request, cancellationToken))); }
        catch (KeyNotFoundException exception) { return NotFound(ApiResult<DatabaseCompareApplyResult>.Fail(exception.Message)); }
        catch (InvalidOperationException exception) { return BadRequest(ApiResult<DatabaseCompareApplyResult>.Fail(exception.Message)); }
        catch (System.Data.Common.DbException exception) { return BadRequest(ApiResult<DatabaseCompareApplyResult>.Fail($"The right database was not updated: {exception.Message}")); }
    }
}
