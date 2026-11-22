using DevTools.Shared.Models;
using DevTools.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DevTools.Web.Controllers;

[ApiController]
[Route("api/patch-management")]
public sealed class PatchManagementController(IPatchManagementService service, ILogger<PatchManagementController> logger) : ControllerBase
{
    [HttpGet("systems")]
    public Task<ActionResult<ApiResult<IReadOnlyList<PatchSystemDto>>>> Systems(CancellationToken cancellationToken) =>
        Run(() => service.GetSystemsAsync(cancellationToken));

    [HttpPost("systems")]
    public Task<ActionResult<ApiResult<PatchSystemDto>>> CreateSystem(SavePatchSystemRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveSystemAsync(null, request, cancellationToken));

    [HttpPut("systems/{id:int}")]
    public Task<ActionResult<ApiResult<PatchSystemDto>>> UpdateSystem(int id, SavePatchSystemRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveSystemAsync(id, request, cancellationToken));

    [HttpDelete("systems/{id:int}")]
    public Task<ActionResult<ApiResult<bool>>> DeleteSystem(int id, CancellationToken cancellationToken) =>
        Run(async () => { await service.DeleteSystemAsync(id, cancellationToken); return true; });

    [HttpPost("systems/{systemId:int}/modules")]
    public Task<ActionResult<ApiResult<PatchModuleDto>>> CreateModule(int systemId, SavePatchModuleRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveModuleAsync(systemId, null, request, cancellationToken));

    [HttpPut("systems/{systemId:int}/modules/{id:int}")]
    public Task<ActionResult<ApiResult<PatchModuleDto>>> UpdateModule(int systemId, int id, SavePatchModuleRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveModuleAsync(systemId, id, request, cancellationToken));

    [HttpDelete("modules/{id:int}")]
    public Task<ActionResult<ApiResult<bool>>> DeleteModule(int id, CancellationToken cancellationToken) =>
        Run(async () => { await service.DeleteModuleAsync(id, cancellationToken); return true; });

    [HttpGet("systems/{systemId:int}/snapshots")]
    public Task<ActionResult<ApiResult<IReadOnlyList<PatchSnapshotDto>>>> Snapshots(int systemId, CancellationToken cancellationToken) =>
        Run(() => service.GetSnapshotsAsync(systemId, cancellationToken));

    /// <summary>Changes between a snapshot (the latest version by default) and the files on disk now.</summary>
    [HttpGet("systems/{systemId:int}/changes")]
    public Task<ActionResult<ApiResult<PatchChangesDto>>> Changes(int systemId, [FromQuery] int? baseSnapshotId, CancellationToken cancellationToken) =>
        Run(() => service.GetCurrentChangesAsync(systemId, baseSnapshotId, cancellationToken));

    /// <summary>Changes a snapshot recorded compared with the snapshot before it.</summary>
    [HttpGet("snapshots/{id:int}/changes")]
    public Task<ActionResult<ApiResult<PatchChangesDto>>> SnapshotChanges(int id, CancellationToken cancellationToken) =>
        Run(() => service.GetSnapshotChangesAsync(id, cancellationToken));

    /// <summary>What a compiled patch contains, grouped by module.</summary>
    [HttpGet("packages/{id:int}/changes")]
    public Task<ActionResult<ApiResult<PatchChangesDto>>> PackageChanges(int id, CancellationToken cancellationToken) =>
        Run(() => service.GetPackageChangesAsync(id, cancellationToken));

    [HttpPost("systems/{systemId:int}/latest")]
    public Task<ActionResult<ApiResult<PatchSnapshotDto>>> SetCurrentAsLatest(int systemId, SetLatestRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SetCurrentAsLatestAsync(systemId, request, Actor, cancellationToken));

    [HttpPost("snapshots/{id:int}/latest")]
    public Task<ActionResult<ApiResult<PatchSnapshotDto>>> SetSnapshotAsLatest(int id, SetSnapshotLatestRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SetSnapshotAsLatestAsync(id, request, Actor, cancellationToken));

    /// <summary>Every change of the modules' latest versions, newest first, with the remarks explaining each.</summary>
    [HttpGet("systems/{systemId:int}/latest-history")]
    public Task<ActionResult<ApiResult<IReadOnlyList<PatchLatestEventDto>>>> LatestHistory(int systemId, CancellationToken cancellationToken) =>
        Run(() => service.GetLatestHistoryAsync(systemId, cancellationToken));

    [HttpPost("modules/{id:int}/latest/undo")]
    public Task<ActionResult<ApiResult<PatchModuleDto>>> UndoLatest(int id, UndoLatestRequest request, CancellationToken cancellationToken) =>
        Run(() => service.UndoLatestAsync(id, request, Actor, cancellationToken));

    [HttpDelete("snapshots/{id:int}")]
    public Task<ActionResult<ApiResult<bool>>> DeleteSnapshot(int id, CancellationToken cancellationToken) =>
        Run(async () => { await service.DeleteSnapshotAsync(id, cancellationToken); return true; });

    [HttpPost("systems/{systemId:int}/compile")]
    public Task<ActionResult<ApiResult<PatchPackageDto>>> Compile(int systemId, CompilePatchRequest request, CancellationToken cancellationToken) =>
        Run(() => service.CompileAsync(systemId, request, Actor, cancellationToken));

    [HttpGet("systems/{systemId:int}/packages")]
    public Task<ActionResult<ApiResult<IReadOnlyList<PatchPackageDto>>>> Packages(int systemId, CancellationToken cancellationToken) =>
        Run(() => service.GetPackagesAsync(systemId, cancellationToken));

    /// <summary>Streams a compiled patch from disk (supports resuming with range requests).</summary>
    [HttpGet("packages/{id:int}/download")]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        try
        {
            var (path, fileName, contentType) = await service.GetDownloadAsync(id, cancellationToken);
            return PhysicalFile(path, contentType, fileName, enableRangeProcessing: true);
        }
        catch (KeyNotFoundException exception) { return NotFound(ApiResult<bool>.Fail(exception.Message)); }
    }

    [HttpDelete("packages/{id:int}")]
    public Task<ActionResult<ApiResult<bool>>> DeletePackage(int id, CancellationToken cancellationToken) =>
        Run(async () => { await service.DeletePackageAsync(id, cancellationToken); return true; });

    /// <summary>The signed-in username for snapshot and patch records; empty when login is turned off.</summary>
    private string? Actor => User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;

    private async Task<ActionResult<ApiResult<T>>> Run<T>(Func<Task<T>> action)
    {
        try { return Ok(ApiResult<T>.Ok(await action())); }
        catch (KeyNotFoundException exception) { return NotFound(ApiResult<T>.Fail(exception.Message)); }
        catch (DirectoryNotFoundException exception) { return Conflict(ApiResult<T>.Fail(exception.Message)); }
        catch (ArgumentException exception) { return BadRequest(ApiResult<T>.Fail(exception.Message)); }
        catch (InvalidOperationException exception) { return Conflict(ApiResult<T>.Fail(exception.Message)); }
        catch (UnauthorizedAccessException) { return Conflict(ApiResult<T>.Fail("Access was denied to a module or output folder. Give the DevTools app pool identity permission to it.")); }
        catch (IOException exception)
        {
            logger.LogWarning("Patch file operation failed: {Type}: {Message}", exception.GetType().Name, exception.Message);
            return Conflict(ApiResult<T>.Fail("A file operation failed. Check that DevTools can read the module folders and write the output folder."));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(499, ApiResult<T>.Fail("The request was cancelled.")); }
    }
}
