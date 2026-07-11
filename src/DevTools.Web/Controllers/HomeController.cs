using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using DevTools.Web.Models;

namespace DevTools.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

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

    [HttpGet("/tools/system-services")]
    public IActionResult SystemServices() => Tool("system-services", "System Services");

    private IActionResult Tool(string tool, string title)
    {
        ViewData["Title"] = title;
        return View("Index", tool);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? statusCode = null)
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
