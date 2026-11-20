using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using DevTools.Web.Models;
using Microsoft.Extensions.Options;

namespace DevTools.Web.Controllers;

public class HomeController(IOptions<ToolOptions> toolOptions) : Controller
{
    private readonly ToolOptions _toolOptions = toolOptions.Value;

    [HttpGet("/")]
    public IActionResult Index()
    {
        ViewData["Title"] = "DevTools";
        return View("Index", "home");
    }

    [HttpGet("/tools/hash")]
    public IActionResult Hash() => Tool("hash", "Hash generator");

    [HttpGet("/tools/formatter")]
    public IActionResult Formatter() => Tool("formatter", "Code formatter");

    [HttpGet("/tools/logs")]
    public IActionResult Logs() => Tool("logs", "Logs viewer");

    [HttpGet("/tools/system-logs")]
    public IActionResult SystemLogs() => Tool("system-logs", "System logs");

    [HttpGet("/tools/sql-doc")]
    public IActionResult SqlDoc() => Tool("sql-doc", "SQL Doc");

    [HttpGet("/tools/database-compare")]
    public IActionResult DatabaseCompare() => Tool("database-compare", "Database Compare");

    [HttpGet("/tools/deployment")]
    public IActionResult Deployment() => Tool("deployment", "Deployment");

    [HttpGet("/tools/patch-management")]
    public IActionResult PatchManagement() => Tool("patch-management", "Releases");

    [HttpGet("/tools/system-services")]
    public IActionResult SystemServices() => Tool("system-services", "System Services");

    private IActionResult Tool(string tool, string title)
    {
        if (!_toolOptions.IsEnabled(tool)) return NotFound();
        ViewData["Title"] = title;
        return View("Index", tool);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? statusCode = null)
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
