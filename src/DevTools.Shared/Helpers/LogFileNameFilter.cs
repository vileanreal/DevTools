using System.IO.Enumeration;

namespace DevTools.Shared.Helpers;

/// <summary>
/// File-name filter for a System Logs folder module. Patterns are separated by ';', ',' or new lines and use
/// '*' and '?' wildcards against the file name only (case-insensitive). A pattern starting with '!' excludes files.
/// Examples: "api-*.log", "app.log;app.log.*", "*.log;!audit-*.log".
/// </summary>
public sealed class LogFileNameFilter
{
    public const int MaxLength = 500;
    public const int MaxPatterns = 20;
    private static readonly char[] Separators = [';', ',', '\n', '\r'];
    private static readonly char[] InvalidPatternChars = Path.GetInvalidFileNameChars()
        .Where(character => character is not '*' and not '?')
        .Concat(['/', '\\', ':', '"', '<', '>', '|'])
        .Distinct()
        .ToArray();

    private LogFileNameFilter(IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        Includes = includes;
        Excludes = excludes;
    }

    public static LogFileNameFilter Empty { get; } = new([], []);
    public IReadOnlyList<string> Includes { get; }
    public IReadOnlyList<string> Excludes { get; }
    public bool HasIncludes => Includes.Count > 0;

    /// <summary>Returns the stored form ("a;b;!c"), or null when the value has no patterns.</summary>
    public static string? Normalize(string? value)
    {
        var patterns = Split(value);
        return patterns.Count == 0 ? null : string.Join(';', patterns);
    }

    /// <summary>Returns a safe, user-facing error message, or null when the value is valid.</summary>
    public static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Length > MaxLength) return $"File name filter must be {MaxLength} characters or fewer.";
        var patterns = Split(value);
        if (patterns.Count > MaxPatterns) return $"File name filter can contain at most {MaxPatterns} patterns.";
        foreach (var pattern in patterns)
        {
            var body = pattern.StartsWith('!') ? pattern[1..].Trim() : pattern;
            if (body.Length == 0) return "A '!' exclusion must be followed by a file name pattern.";
            if (body.IndexOfAny(InvalidPatternChars) >= 0) return $"'{pattern}' is not a valid file name pattern. Use names only (no folders) with * and ? wildcards.";
        }
        return null;
    }

    public static LogFileNameFilter Parse(string? value)
    {
        var patterns = Split(value);
        if (patterns.Count == 0) return Empty;
        var excludes = patterns.Where(pattern => pattern.StartsWith('!')).Select(pattern => pattern[1..].Trim()).Where(pattern => pattern.Length > 0).ToList();
        var includes = patterns.Where(pattern => !pattern.StartsWith('!')).ToList();
        return new LogFileNameFilter(includes, excludes);
    }

    /// <summary>
    /// True when the file name is selected. With include patterns, a file must match one of them (any extension);
    /// without include patterns, <paramref name="defaultInclude"/> decides (normally the supported-extension check).
    /// Exclusions always win.
    /// </summary>
    public bool IsMatch(string fileName, Func<string, bool> defaultInclude)
    {
        if (Excludes.Any(pattern => Matches(pattern, fileName))) return false;
        return HasIncludes ? Includes.Any(pattern => Matches(pattern, fileName)) : defaultInclude(fileName);
    }

    private static bool Matches(string pattern, string fileName) =>
        FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true);

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
}
