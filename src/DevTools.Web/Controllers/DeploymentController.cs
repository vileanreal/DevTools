using DevTools.Shared.Models;
using DevTools.Web.Filters;
using DevTools.Web.Models;
using DevTools.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DevTools.Web.Controllers;

[ApiController]
[Route("api/deployment")]
public sealed class DeploymentController(
    IDeploymentService service,
    IDeploymentJobService jobs,
    IOptions<DeploymentOptions> options,
    ILogger<DeploymentController> logger) : ControllerBase
{
    [HttpGet("systems")]
    public Task<ActionResult<ApiResult<IReadOnlyList<DeploymentSystemDto>>>> Systems(CancellationToken cancellationToken) =>
        Run(() => service.GetSystemsAsync(cancellationToken));

    [HttpPost("systems")]
    public Task<ActionResult<ApiResult<DeploymentSystemDto>>> CreateSystem(SaveDeploymentSystemRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveSystemAsync(null, request, cancellationToken));

    [HttpPut("systems/{id:int}")]
    public Task<ActionResult<ApiResult<DeploymentSystemDto>>> UpdateSystem(int id, SaveDeploymentSystemRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveSystemAsync(id, request, cancellationToken));

    [HttpDelete("systems/{id:int}")]
    public Task<ActionResult<ApiResult<bool>>> DeleteSystem(int id, CancellationToken cancellationToken) =>
        Run(async () => { await service.DeleteSystemAsync(id, cancellationToken); return true; });

    [HttpPost("systems/{systemId:int}/modules")]
    public Task<ActionResult<ApiResult<DeploymentModuleDto>>> CreateModule(int systemId, SaveDeploymentModuleRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveModuleAsync(systemId, null, request, cancellationToken));

    [HttpPut("systems/{systemId:int}/modules/{id:int}")]
    public Task<ActionResult<ApiResult<DeploymentModuleDto>>> UpdateModule(int systemId, int id, SaveDeploymentModuleRequest request, CancellationToken cancellationToken) =>
        Run(() => service.SaveModuleAsync(systemId, id, request, cancellationToken));

    [HttpDelete("modules/{id:int}")]
    public Task<ActionResult<ApiResult<bool>>> DeleteModule(int id, CancellationToken cancellationToken) =>
        Run(async () => { await service.DeleteModuleAsync(id, cancellationToken); return true; });

    [HttpGet("systems/{systemId:int}/packages")]
    public Task<ActionResult<ApiResult<IReadOnlyList<DeploymentPackageDto>>>> Packages(int systemId, CancellationToken cancellationToken) =>
        Run(() => service.GetPackagesAsync(systemId, cancellationToken));

    /// <summary>
    /// Streams a multipart upload straight to disk. Form binding is disabled so ASP.NET Core never buffers the file in
    /// memory, and the request size limit is raised for this endpoint only (IIS also needs maxAllowedContentLength in web.config).
    /// </summary>
    [HttpPost("systems/{systemId:int}/packages")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<ActionResult<ApiResult<DeploymentPackageDto>>> Upload(int systemId, CancellationToken cancellationToken)
    {
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = options.Value.MaxUploadBytes + 1024 * 1024;
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType) || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiResult<DeploymentPackageDto>.Fail("Send the package as multipart/form-data."));
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary)) return BadRequest(ApiResult<DeploymentPackageDto>.Fail("The upload is missing its multipart boundary."));

        var reader = new MultipartReader(boundary, Request.Body) { BodyLengthLimit = options.Value.MaxUploadBytes };
        return await Run(async () =>
        {
            // The description field must come before the file, so the file can still be streamed straight to disk.
            string? description = null;
            for (var section = await reader.ReadNextSectionAsync(cancellationToken); section is not null; section = await reader.ReadNextSectionAsync(cancellationToken))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
                if (disposition.IsFormDisposition() && HeaderUtilities.RemoveQuotes(disposition.Name).Value == "description")
                {
                    using var text = new StreamReader(section.Body, System.Text.Encoding.UTF8);
                    var buffer = new char[DeploymentService.MaxDescriptionLength + 1];
                    var read = await text.ReadBlockAsync(buffer, cancellationToken);
                    description = new string(buffer, 0, read);
                    continue;
                }
                if (!disposition.IsFileDisposition()) continue;
                if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Enter a package description before uploading.");
                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? string.Empty;
                return await service.SavePackageAsync(systemId, fileName, description, section.Body, Actor, cancellationToken);
            }
            throw new ArgumentException("No file was found in the upload.");
        });
    }

    [HttpDelete("packages/{id:int}")]
    public Task<ActionResult<ApiResult<bool>>> DeletePackage(int id, CancellationToken cancellationToken) =>
        Run(async () => { await service.DeletePackageAsync(id, cancellationToken); return true; });

    [HttpGet("packages/{id:int}/contents")]
    public Task<ActionResult<ApiResult<DeploymentPackageContents>>> Contents(int id, CancellationToken cancellationToken) =>
        Run(() => service.GetContentsAsync(id, cancellationToken));

    [HttpPost("packages/{id:int}/preview")]
    public Task<ActionResult<ApiResult<DeploymentFilePreview>>> Preview(int id, DeploymentFilePreviewRequest request, CancellationToken cancellationToken) =>
        Run(() => service.PreviewFilesAsync(id, request, cancellationToken));

    [HttpPost("packages/{id:int}/deploy")]
    public Task<ActionResult<ApiResult<DeploymentJobStatus>>> Deploy(int id, DeployPackageRequest request, CancellationToken cancellationToken) =>
        Run(() => jobs.StartDeployAsync(id, request, Actor, cancellationToken));

    [HttpPost("packages/{id:int}/rollback")]
    public Task<ActionResult<ApiResult<DeploymentJobStatus>>> Rollback(int id, CancellationToken cancellationToken) =>
        Run(() => jobs.StartRollbackAsync(id, Actor, cancellationToken));

    [HttpGet("systems/{systemId:int}/job")]
    public ActionResult<ApiResult<DeploymentJobStatus?>> LatestJob(int systemId) =>
        Ok(ApiResult<DeploymentJobStatus?>.Ok(jobs.GetLatestForSystem(systemId)));

    [HttpGet("jobs/{jobId}")]
    public ActionResult<ApiResult<DeploymentJobStatus>> Job(string jobId) =>
        jobs.Get(jobId) is { } status ? Ok(ApiResult<DeploymentJobStatus>.Ok(status)) : NotFound(ApiResult<DeploymentJobStatus>.Fail("Job not found."));

    /// <summary>The signed-in username for history entries; empty when login is turned off.</summary>
    private string? Actor => User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;

    /// <summary>Maps expected failures to safe messages; unexpected errors are logged without user content and hidden.</summary>
    private async Task<ActionResult<ApiResult<T>>> Run<T>(Func<Task<T>> action)
    {
        try { return Ok(ApiResult<T>.Ok(await action())); }
        catch (KeyNotFoundException exception) { return NotFound(ApiResult<T>.Fail(exception.Message)); }
        catch (ArgumentException exception) { return BadRequest(ApiResult<T>.Fail(exception.Message)); }
        catch (InvalidOperationException exception) { return Conflict(ApiResult<T>.Fail(exception.Message)); }
        catch (InvalidDataException) { return BadRequest(ApiResult<T>.Fail($"The upload is larger than the {options.Value.MaxUploadMegabytes:N0} MB limit or is malformed.")); }
        catch (BadHttpRequestException) { return BadRequest(ApiResult<T>.Fail($"The upload is larger than the {options.Value.MaxUploadMegabytes:N0} MB limit or was interrupted.")); }
        catch (IOException exception) when (exception is not FileNotFoundException)
        {
            logger.LogWarning("Deployment file operation failed: {Type}: {Message}", exception.GetType().Name, exception.Message);
            return Conflict(ApiResult<T>.Fail("A file operation failed. Check that DevTools can read and write the deployment folders."));
        }
        catch (UnauthorizedAccessException)
        {
            return Conflict(ApiResult<T>.Fail("Access was denied to a deployment folder. Give the DevTools app pool identity permission to it."));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(499, ApiResult<T>.Fail("The request was cancelled.")); }
    }
}
