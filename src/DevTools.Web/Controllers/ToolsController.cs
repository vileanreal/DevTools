using DevTools.Application.Services;
using DevTools.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace DevTools.Web.Controllers;

[ApiController]
[Route("api/tools")]
public sealed class ToolsController(
    IHashService hashService,
    ICodeFormatterService codeFormatter,
    ILogParserService logParser) : ControllerBase
{
    private const int MaxInputLength = 1_000_000;

    [HttpPost("hash")]
    public ActionResult<ApiResult<HashResult>> Hash(HashRequest request)
    {
        if (!TryValidateInput(request.Input, out var error)) return BadRequest(ApiResult<HashResult>.Fail(error));
        if (request.Key?.Length > 100_000) return BadRequest(ApiResult<HashResult>.Fail("The HMAC key cannot exceed 100,000 characters."));
        try { return Ok(ApiResult<HashResult>.Ok(hashService.Compute(request.Input!, request.Algorithm, request.Key))); }
        catch (ArgumentException ex) { return BadRequest(ApiResult<HashResult>.Fail(ex.Message)); }
        catch (PlatformNotSupportedException ex) { return BadRequest(ApiResult<HashResult>.Fail(ex.Message)); }
    }

    [HttpPost("format")]
    public ActionResult<ApiResult<FormatResult>> FormatCode(FormatRequest request)
    {
        if (!TryValidateInput(request.Input, out var error)) return BadRequest(ApiResult<FormatResult>.Fail(error));
        var result = codeFormatter.Format(request.Input!, request.Language, request.IndentSize, request.UseTabs);
        return result.IsValid ? Ok(ApiResult<FormatResult>.Ok(result)) : BadRequest(ApiResult<FormatResult>.Fail(result.Error!));
    }

    [HttpPost("logs")]
    [DisableRequestSizeLimit]
    public ActionResult<ApiResult<LogParseResult>> ParseLogs(LogParseRequest request)
    {
        if (string.IsNullOrEmpty(request.Input)) return BadRequest(ApiResult<LogParseResult>.Fail("Input is required."));
        return Ok(ApiResult<LogParseResult>.Ok(logParser.Parse(request.Input, request.Level, request.Search, page: request.Page, pageSize: request.PageSize)));
    }

    private static bool TryValidateInput(string? input, out string error)
    {
        error = input switch
        {
            null or "" => "Input is required.",
            { Length: > MaxInputLength } => "Input must be 1 MB or smaller.",
            _ => string.Empty
        };
        return error.Length == 0;
    }
}
