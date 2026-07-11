using System.Text.RegularExpressions;
using DevTools.Shared.Models;

namespace DevTools.Shared.Helpers;

public static partial class LogContinuationHelper
{
    [GeneratedRegex(@"^\s*(?:at\s+|---\s+End of|Caused by:|System\.|Microsoft\.|[A-Za-z_][A-Za-z0-9_.`+]*Exception\b|\.\.\.\s+\d+\s+more|ClientConnectionId\s*:|Error\s+Number\s*:|Inner\s+Exception\s*:|Server\s+stack\s+trace\s*:|Exception\s+rethrown\s+at|A\s+severe\s+error\s+occurred|The\s+(?:statement|results?)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex StackContinuationPattern();

    [GeneratedRegex(@"^\s*(?:\[(?:TRACE|VERBOSE|DEBUG|DBG|INFORMATION|INFO|INF|WARNING|WARN|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|CRIT)\]|(?:TRACE|VERBOSE|DEBUG|DBG|INFORMATION|INFO|INF|WARNING|WARN|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|CRIT)\b(?:\s*[-|:]|\s+))", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitLevelPattern();

    public static bool IsContinuationLine(string line, LogEntry parsed, LogEntry previous)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Timestamp)) return false;
        if (line.StartsWith(' ') || line.StartsWith('\t')) return true;
        if (StackContinuationPattern().IsMatch(line)) return true;
        if (ExplicitLevelPattern().IsMatch(line)) return false;

        return previous.Level is "ERROR" or "FATAL" ||
               previous.Message.Contains("Exception", StringComparison.OrdinalIgnoreCase);
    }
}
