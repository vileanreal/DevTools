using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public interface ILogParserService
{
    LogParseResult Parse(string input, string? level, string? search, string format = "auto", string? customPattern = null, int page = 1, int pageSize = 100);
}
