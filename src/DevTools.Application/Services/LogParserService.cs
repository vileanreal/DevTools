using System.Text.Json;
using System.Text.RegularExpressions;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public sealed partial class LogParserService : ILogParserService
{
    private static readonly string[] TimestampProperties = ["timestamp", "time", "datetime", "date", "@t", "ts"];
    private static readonly string[] LevelProperties = ["level", "severity", "loglevel", "@l"];
    private static readonly string[] MessageProperties = ["message", "msg", "renderedmessage", "@m"];

    [GeneratedRegex(@"^(?<timestamp>\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?)\s*(?:[-|:]\s*)?(?:\[?(?<level>TRACE|VERBOSE|DEBUG|DBG|INFORMATION|INFO|INF|WARNING|WARN|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|CRIT)\]?\s*(?:[-|:]\s*)?)?(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TimestampFirstPattern();

    [GeneratedRegex(@"^\[?(?<level>TRACE|VERBOSE|DEBUG|DBG|INFORMATION|INFO|INF|WARNING|WARN|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|CRIT)\]?\s*(?:[-|:]\s*)?(?<timestamp>\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?)?\s*(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LevelFirstPattern();

    [GeneratedRegex(@"^\[(?<timestamp>\d{4}-\d{2}-\d{2}[T\s][^\]]+?)\s+(?<level>TRC|DBG|INF|WRN|ERR|FTL|VRB)\]\s*(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SerilogPattern();

    [GeneratedRegex(@"^(?<timestamp>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})\s+(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SyslogPattern();

    [GeneratedRegex(@"^(?<timestamp>\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?\s*(?:AM|PM)?)\s*(?:\(\d+(?:\.\d+)?\s*ms\)\s*)?\[?(?<level>TRACE|VERBOSE|DEBUG|DBG|INFORMATION|INFO|INF|WARNING|WARN|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|CRIT)\]?\s*(?:[-|:]\s*)?(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeOnlyPattern();

    public LogParseResult Parse(string input, string? level, string? search, string format = "auto", string? customPattern = null, int page = 1, int pageSize = 100)
    {
        var normalizedFormat = format.Trim().ToLowerInvariant();
        Regex? customRegex = null;
        if (normalizedFormat == "custom" && !string.IsNullOrWhiteSpace(customPattern))
            customRegex = new Regex(customPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        var entries = ParseEntries(TextHelper.NormalizeLineEndings(input).Split('\n', StringSplitOptions.RemoveEmptyEntries), normalizedFormat, customRegex);
        var filtered = entries.Where(entry =>
            (string.IsNullOrWhiteSpace(level) || level.Equals("ALL", StringComparison.OrdinalIgnoreCase) || entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase)) &&
            TextHelper.ContainsIgnoreCase(entry.Raw, search)).ToList();
        pageSize = Math.Clamp(pageSize, 10, 500);
        var totalPages = filtered.Count == 0 ? 0 : (int)Math.Ceiling(filtered.Count / (double)pageSize);
        page = Math.Min(Math.Max(1, page), Math.Max(1, totalPages));
        var statistics = filtered.GroupBy(entry => entry.Level).Select(group => new LogLevelCount(group.Key, group.Count())).OrderByDescending(item => item.Count).ToList();
        var pageEntries = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new LogParseResult(pageEntries, entries.Count, filtered.Count, statistics, page, pageSize, totalPages);
    }

    private static List<LogEntry> ParseEntries(IEnumerable<string> lines, string format, Regex? customRegex)
    {
        var entries = new List<LogEntry>();
        foreach (var line in lines)
        {
            var parsed = ParseLine(line, format, customRegex);
            if (entries.Count > 0 && LogContinuationHelper.IsContinuationLine(line, parsed, entries[^1]))
            {
                var previous = entries[^1];
                entries[^1] = previous with
                {
                    Message = $"{previous.Message}{Environment.NewLine}{line.TrimEnd()}",
                    Raw = $"{previous.Raw}{Environment.NewLine}{line}"
                };
                continue;
            }
            entries.Add(parsed);
        }
        return entries;
    }

    private static LogEntry ParseLine(string line, string format, Regex? customRegex)
    {
        var trimmed = line.Trim();
        if (format == "custom" && customRegex is not null)
        {
            try { return FromMatch(customRegex.Match(trimmed), trimmed); }
            catch (RegexMatchTimeoutException) { return new LogEntry(string.Empty, InferLevel(trimmed), trimmed, line); }
        }
        if (format is "json" or "auto" && trimmed.StartsWith('{') && TryParseJson(trimmed, out var jsonEntry)) return jsonEntry;
        if (format is "text" or "auto")
        {
            foreach (var pattern in new[] { SerilogPattern(), TimestampFirstPattern(), TimeOnlyPattern(), LevelFirstPattern(), SyslogPattern() })
            {
                var match = pattern.Match(trimmed);
                if (match.Success) return FromMatch(match, trimmed);
            }
        }
        return new LogEntry(string.Empty, InferLevel(trimmed), trimmed, line);
    }

    private static LogEntry FromMatch(Match match, string raw)
    {
        if (!match.Success) return new LogEntry(string.Empty, InferLevel(raw), raw, raw);
        var timestamp = match.Groups["timestamp"].Value;
        var message = match.Groups["message"].Success ? match.Groups["message"].Value.Trim() : raw;
        var level = match.Groups["level"].Success ? NormalizeLevel(match.Groups["level"].Value) : InferLevel(message);
        return new LogEntry(timestamp, level, message, raw);
    }

    private static bool TryParseJson(string line, out LogEntry entry)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var timestamp = FindProperty(root, TimestampProperties);
            var message = FindProperty(root, MessageProperties);
            var level = FindProperty(root, LevelProperties);
            message = string.IsNullOrWhiteSpace(message) ? line : message;
            entry = new LogEntry(timestamp, string.IsNullOrWhiteSpace(level) ? InferLevel(message) : NormalizeLevel(level), message, line);
            return true;
        }
        catch (JsonException) { entry = default!; return false; }
    }

    private static string FindProperty(JsonElement element, IEnumerable<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var property in element.EnumerateObject())
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return property.Value.ToString();
        return string.Empty;
    }

    private static string InferLevel(string message)
    {
        if (Regex.IsMatch(message, @"\b(fatal|critical|panic)\b", RegexOptions.IgnoreCase)) return "FATAL";
        if (Regex.IsMatch(message, @"\b(error|exception|failed|failure|fault|timeout|denied)\b", RegexOptions.IgnoreCase)) return "ERROR";
        if (Regex.IsMatch(message, @"\b(warn(?:ing)?|slow|retry|degraded)\b", RegexOptions.IgnoreCase)) return "WARN";
        if (Regex.IsMatch(message, @"\b(debug|diagnostic)\b", RegexOptions.IgnoreCase)) return "DEBUG";
        if (Regex.IsMatch(message, @"\b(info|started|starting|completed|complete|success|ready|connected|stopped)\b", RegexOptions.IgnoreCase)) return "INFO";
        return "OTHER";
    }

    private static string NormalizeLevel(string level) => level.Trim().ToUpperInvariant() switch
    {
        "TRC" or "VERBOSE" or "VRB" => "TRACE",
        "DBG" => "DEBUG",
        "INFORMATION" or "INF" => "INFO",
        "WARNING" or "WRN" => "WARN",
        "ERR" => "ERROR",
        "FTL" or "CRITICAL" or "CRIT" => "FATAL",
        var normalized => normalized
    };
}
