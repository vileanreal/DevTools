using System.Globalization;
using DevTools.Shared.Models;

namespace DevTools.Shared.Helpers;

/// <summary>An archive entry as listed from a package, before extraction.</summary>
public sealed record PackageEntry(string Path, long Size, bool IsDirectory);

/// <summary>
/// Maps the folders inside an uploaded package to a system's modules. Top-level folders are matched to module names
/// (case-insensitive). If the archive wraps everything in one extra folder (e.g. "release-1.2/Api/..."), that wrapper
/// is skipped automatically.
/// </summary>
public static class DeploymentPackageLayout
{
    /// <summary>Returns a safe '/'-separated relative path, or null for absolute paths, drive letters or '..' segments.</summary>
    public static string? NormalizeEntryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = path.Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains(':') || value.Contains('\0')) return null;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..")) return null;
        return string.Join('/', segments);
    }

    public static DeploymentPackageAnalysis Analyze(IEnumerable<PackageEntry> entries, IEnumerable<(int Id, string Name)> modules)
    {
        var files = new List<(string Path, long Size)>();
        foreach (var entry in entries)
        {
            var normalized = NormalizeEntryPath(entry.Path) ?? throw new InvalidOperationException($"The package contains an unsafe path: '{entry.Path}'.");
            if (!entry.IsDirectory) files.Add((normalized, entry.Size));
        }
        var moduleList = modules.ToList();
        var byName = moduleList.ToDictionary(module => module.Name, StringComparer.OrdinalIgnoreCase);
        var wrapper = FindWrapper(files.Select(file => file.Path), byName.Keys);

        var groups = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var rootFiles = 0;
        foreach (var (path, size) in files)
        {
            var relative = wrapper is null ? path : path[(wrapper.Length + 1)..];
            var slash = relative.IndexOf('/');
            if (slash < 0) { rootFiles++; continue; }
            var folder = relative[..slash];
            groups[folder] = groups.TryGetValue(folder, out var current) ? (current.Count + 1, current.Bytes + size) : (1, size);
        }

        var matched = groups
            .Where(group => byName.ContainsKey(group.Key))
            .Select(group => { var module = byName[group.Key]; return new DeploymentPackageModule(group.Key, module.Id, module.Name, group.Value.Count, group.Value.Bytes); })
            .OrderBy(module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unmatched = groups.Keys.Where(folder => !byName.ContainsKey(folder)).OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).ToList();
        return new DeploymentPackageAnalysis(wrapper, matched, unmatched, rootFiles, files.Sum(file => file.Size));
    }

    /// <summary>
    /// Splits a package entry path into its module folder and the path inside that module, honouring the wrapper folder.
    /// Returns null for entries outside any module folder.
    /// </summary>
    public static (string Folder, string RelativePath)? Locate(string entryPath, string? wrapperFolder)
    {
        var normalized = NormalizeEntryPath(entryPath);
        if (normalized is null) return null;
        if (wrapperFolder is not null)
        {
            if (!normalized.StartsWith(wrapperFolder + "/", StringComparison.OrdinalIgnoreCase)) return null;
            normalized = normalized[(wrapperFolder.Length + 1)..];
        }
        var slash = normalized.IndexOf('/');
        return slash <= 0 || slash == normalized.Length - 1 ? null : (normalized[..slash], normalized[(slash + 1)..]);
    }

    /// <summary>
    /// Backup names follow mmddyyyy.zip, mmddyyyy.7z or a folder named mmddyyyy; later backups on the same day get -2, -3, ...
    /// A number is taken when a backup in any format already uses it, so a day's backups stay in order after a format change.
    /// </summary>
    public static string BackupFileName(DateTime date, string format, Func<string, bool> exists)
    {
        var normalized = DeploymentBackupFormats.Normalize(format);
        var stem = date.ToString("MMddyyyy", CultureInfo.InvariantCulture);
        bool Taken(string candidate) => exists(candidate) || exists($"{candidate}.{DeploymentBackupFormats.Zip}") || exists($"{candidate}.{DeploymentBackupFormats.SevenZip}");
        var name = stem;
        for (var counter = 2; Taken(name); counter++) name = $"{stem}-{counter}";
        return normalized == DeploymentBackupFormats.Folder ? name : $"{name}.{normalized}";
    }

    private static string? FindWrapper(IEnumerable<string> files, IEnumerable<string> moduleNames)
    {
        var names = moduleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tops = files.Select(path => path.Split('/')).ToList();
        if (tops.Count == 0 || tops.Any(parts => parts.Length < 3)) return null;
        var first = tops[0][0];
        if (names.Contains(first) || tops.Any(parts => !parts[0].Equals(first, StringComparison.OrdinalIgnoreCase))) return null;
        return tops.Any(parts => names.Contains(parts[1])) ? first : null;
    }
}
