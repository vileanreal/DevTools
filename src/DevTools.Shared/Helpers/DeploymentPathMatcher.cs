using System.Text;
using System.Text.RegularExpressions;

namespace DevTools.Shared.Helpers;

/// <summary>
/// Exclusion list for a deployment module. One pattern per line (';' also separates), relative to the module folder,
/// with '/' as separator and case-insensitive matching:
/// <list type="bullet">
/// <item><c>web.config</c> — a name without '/' matches that file or folder name at any depth.</item>
/// <item><c>appsettings*.json</c> — '*' matches within one name, '?' one character.</item>
/// <item><c>logs/</c> — a trailing '/' excludes the folder and everything in it.</item>
/// <item><c>App_Data/**</c> or <c>config/*.xml</c> — a pattern containing '/' is anchored to the module root; '**' spans folders.</item>
/// </list>
/// Lines starting with '#' are comments.
/// </summary>
public sealed class DeploymentPathMatcher
{
    public const int MaxLength = 4000;
    public const int MaxPatterns = 200;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private readonly IReadOnlyList<Regex> _patterns;

    private DeploymentPathMatcher(IReadOnlyList<Regex> patterns, IReadOnlyList<string> source)
    {
        _patterns = patterns;
        Patterns = source;
    }

    public static DeploymentPathMatcher Empty { get; } = new([], []);
    public IReadOnlyList<string> Patterns { get; }
    public bool IsEmpty => _patterns.Count == 0;

    public static string? Normalize(string? value)
    {
        var patterns = Split(value);
        return patterns.Count == 0 ? null : string.Join('\n', patterns);
    }

    public static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > MaxLength) return $"The exclusion list must be {MaxLength} characters or fewer.";
        var patterns = Split(value);
        if (patterns.Count > MaxPatterns) return $"The exclusion list can contain at most {MaxPatterns} patterns.";
        foreach (var pattern in patterns)
        {
            if (pattern.Contains('\0') || pattern.Contains(':') || pattern.StartsWith('/') && pattern.Length == 1)
                return $"'{pattern}' is not a valid exclusion pattern.";
            if (pattern.Split('/').Any(segment => segment is ".." or "."))
                return $"'{pattern}' cannot contain '.' or '..' folders.";
        }
        return null;
    }

    public static DeploymentPathMatcher Parse(string? value)
    {
        var patterns = Split(value);
        return patterns.Count == 0 ? Empty : new DeploymentPathMatcher(patterns.Select(ToRegex).ToList(), patterns);
    }

    /// <summary>True when the relative path ('/' or '\' separated) is excluded, or sits inside an excluded folder.</summary>
    public bool IsExcluded(string relativePath, bool isDirectory = false)
    {
        if (_patterns.Count == 0) return false;
        var path = NormalizeRelative(relativePath);
        if (path.Length == 0) return false;
        var segments = path.Split('/');
        for (var length = 1; length <= segments.Length; length++)
        {
            var candidate = string.Join('/', segments, 0, length);
            var candidateIsDirectory = length < segments.Length || isDirectory;
            foreach (var pattern in _patterns)
                if (pattern.IsMatch(candidateIsDirectory ? candidate + "/" : candidate)) return true;
        }
        return false;
    }

    public static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#'))
                .Select(line => line.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static Regex ToRegex(string pattern)
    {
        var directoryOnly = pattern.EndsWith('/');
        var body = pattern.Trim('/');
        var anchored = body.Contains('/');
        var builder = new StringBuilder(anchored ? "^" : "^(?:.*/)?");
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (character == '*' && index + 1 < body.Length && body[index + 1] == '*')
            {
                var followedBySlash = index + 2 < body.Length && body[index + 2] == '/';
                builder.Append(followedBySlash ? "(?:.*/)?" : ".*");
                index += followedBySlash ? 2 : 1;
            }
            else if (character == '*') builder.Append("[^/]*");
            else if (character == '?') builder.Append("[^/]");
            else builder.Append(Regex.Escape(character.ToString()));
        }
        builder.Append(directoryOnly ? "/$" : "/?$");
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }
}
