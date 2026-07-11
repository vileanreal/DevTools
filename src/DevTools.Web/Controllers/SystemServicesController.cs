using DevTools.Shared.Models;
using DevTools.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Controllers;

[ApiController]
[Route("api/system-services")]
public sealed class SystemServicesController(ISystemServicesService service) : ControllerBase
{
    [HttpGet("systems")]
    public async Task<ApiResult<IReadOnlyList<ServiceSystemDto>>> List(CancellationToken cancellationToken) =>
        ApiResult<IReadOnlyList<ServiceSystemDto>>.Ok(await service.ListAsync(cancellationToken));

    [HttpGet("systems/{systemId:int}")]
    public async Task<ActionResult<ApiResult<ServiceSystemDto>>> Get(int systemId, CancellationToken cancellationToken, [FromQuery] bool refresh = false)
    {
        var system = await service.GetAsync(systemId, refresh, cancellationToken);
        return system is null ? NotFound(ApiResult<ServiceSystemDto>.Fail("System not found.")) : Ok(ApiResult<ServiceSystemDto>.Ok(system));
    }

    [HttpPost("systems")]
    public async Task<ActionResult<ApiResult<ServiceSystemDto>>> CreateSystem(CreateServiceSystemRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            return BadRequest(ApiResult<ServiceSystemDto>.Fail("System name is required and must be 120 characters or fewer."));
        try { return Ok(ApiResult<ServiceSystemDto>.Ok(await service.CreateSystemAsync(request.Name, cancellationToken))); }
        catch (DbUpdateException) { return Conflict(ApiResult<ServiceSystemDto>.Fail("A system with this name already exists.")); }
    }

    [HttpPost("systems/{systemId:int}/services")]
    public async Task<ActionResult<ApiResult<ManagedServiceDto>>> AddService(int systemId, CreateManagedServiceRequest request, CancellationToken cancellationToken)
    {
        var error = ValidateService(request);
        if (error is not null) return BadRequest(ApiResult<ManagedServiceDto>.Fail(error));
        try
        {
            return Ok(ApiResult<ManagedServiceDto>.Ok(await service.AddServiceAsync(systemId, request.Name!, request.Kind!, request.TargetName!, cancellationToken)));
        }
        catch (KeyNotFoundException exception) { return NotFound(ApiResult<ManagedServiceDto>.Fail(exception.Message)); }
        catch (ArgumentException exception) { return BadRequest(ApiResult<ManagedServiceDto>.Fail(exception.Message)); }
        catch (DbUpdateException) { return Conflict(ApiResult<ManagedServiceDto>.Fail("A service with this display name already exists in the system.")); }
    }

    [HttpPost("services/{serviceId:int}/{actionName}")]
    public async Task<ActionResult<ApiResult<ManagedServiceDto>>> Execute(int serviceId, string actionName, CancellationToken cancellationToken)
    {
        var action = actionName.Trim().ToLowerInvariant();
        if (action is not ("start" or "stop" or "restart"))
            return BadRequest(ApiResult<ManagedServiceDto>.Fail("Choose start, stop, or restart."));
        try
        {
            var result = await service.ExecuteAsync(serviceId, action, cancellationToken);
            return result is null ? NotFound(ApiResult<ManagedServiceDto>.Fail("Service not found.")) : Ok(ApiResult<ManagedServiceDto>.Ok(result));
        }
        catch (InvalidOperationException exception) { return BadRequest(ApiResult<ManagedServiceDto>.Fail(exception.Message)); }
    }

    [HttpDelete("systems/{systemId:int}")]
    public async Task<IActionResult> DeleteSystem(int systemId, CancellationToken cancellationToken) =>
        await service.DeleteSystemAsync(systemId, cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("services/{serviceId:int}")]
    public async Task<IActionResult> DeleteService(int serviceId, CancellationToken cancellationToken) =>
        await service.DeleteServiceAsync(serviceId, cancellationToken) ? NoContent() : NotFound();

    private static string? ValidateService(CreateManagedServiceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) return "Display name is required and must be 120 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.TargetName) || request.TargetName.Trim().Length > 260) return "Service or app-pool name is required and must be 260 characters or fewer.";
        if (request.Kind?.Trim().ToLowerInvariant() is not ("windows-service" or "app-pool")) return "Choose Windows service or IIS application pool.";
        return null;
    }
}
