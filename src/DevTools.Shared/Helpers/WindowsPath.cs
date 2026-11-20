namespace DevTools.Shared.Helpers;

/// <summary>
/// Windows path handling that behaves the same on any OS. DevTools is a Windows application, so deployment folders
/// such as <c>C:\Backups\MyApp</c> or <c>\\server\share\backups</c> are treated as Windows paths even when the code
/// runs elsewhere (for example during development on macOS). Comparisons are case-insensitive, like NTFS.
/// </summary>
public static class WindowsPath
{
    /// <summary>True for drive paths (<c>C:\x</c>, <c>C:/x</c>) and UNC paths (<c>\\server\share</c>).</summary>
    public static bool IsFullyQualified(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var value = path.Trim().Replace('/', '\\');
        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\') return true;
        if (value.Length == 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') return false;
        return value.StartsWith(@"\\", StringComparison.Ordinal) && value.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    /// <summary>
    /// Normalizes a fully qualified Windows path: '/' becomes '\', repeated separators collapse, '.' and '..' are
    /// resolved (never above the root), the drive letter is upper-cased and trailing separators are removed
    /// (except for a drive root such as <c>C:\</c>).
    /// </summary>
    public static string Normalize(string path)
    {
        var value = path.Trim().Replace('/', '\\');
        string root;
        string rest;
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = value.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) throw new ArgumentException("A UNC path needs a server and a share, such as \\\\server\\share.");
            root = $@"\\{parts[0]}\{parts[1]}";
            rest = string.Join('\\', parts.Skip(2));
        }
        else if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            root = $"{char.ToUpperInvariant(value[0])}:\\";
            rest = value[2..];
        }
        else throw new ArgumentException("Not a fully qualified Windows path.");

        var segments = new List<string>();
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            // Windows ignores trailing spaces and dots in folder names.
            segments.Add(segment.TrimEnd(' ', '.') is { Length: > 0 } trimmed ? trimmed : segment);
        }
        if (segments.Count == 0) return root;
        return root.EndsWith('\\') ? root + string.Join('\\', segments) : root + "\\" + string.Join('\\', segments);
    }

    /// <summary>True for a drive root (<c>C:\</c>) or a bare UNC share (<c>\\server\share</c>).</summary>
    public static bool IsRoot(string normalizedPath) =>
        normalizedPath.Length == 3 && normalizedPath[1] == ':'
        || normalizedPath.StartsWith(@"\\", StringComparison.Ordinal) && normalizedPath.TrimStart('\\').Split('\\').Length == 2;

    /// <summary>True when <paramref name="path"/> equals <paramref name="parent"/> or sits inside it (both normalized).</summary>
    public static bool IsSameOrInside(string path, string parent)
    {
        if (path.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = parent.EndsWith('\\') ? parent : parent + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
