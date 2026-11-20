using DevTools.Shared.Models;

namespace DevTools.Shared.Helpers;

/// <summary>
/// Compares two sets of module manifests. A file is ADDED when it exists only in the current set, DELETED when it exists
/// only in the base, and CHANGED when its SHA-256 differs. Modules are matched by name (case-insensitive), paths
/// case-insensitively, as on Windows.
/// </summary>
public static class PatchDiff
{
    public static (IReadOnlyList<PatchFileChange> Files, IReadOnlyList<PatchModuleChangeSummary> Modules) Compare(
        IEnumerable<PatchModuleManifest> baseline,
        IEnumerable<PatchModuleManifest> current)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var baseByModule = baseline.GroupBy(module => module.ModuleName, comparer)
            .ToDictionary(group => group.Key, group => Index(group.SelectMany(module => module.Files)), comparer);
        var currentByModule = current.GroupBy(module => module.ModuleName, comparer)
            .ToDictionary(group => group.Key, group => Index(group.SelectMany(module => module.Files)), comparer);

        var changes = new List<PatchFileChange>();
        var summaries = new List<PatchModuleChangeSummary>();
        foreach (var module in currentByModule.Keys.Union(baseByModule.Keys, comparer).OrderBy(name => name, comparer))
        {
            var before = baseByModule.GetValueOrDefault(module) ?? new Dictionary<string, PatchFileEntry>(comparer);
            var after = currentByModule.GetValueOrDefault(module) ?? new Dictionary<string, PatchFileEntry>(comparer);
            int added = 0, changed = 0, deleted = 0;
            foreach (var (path, file) in after)
            {
                if (!before.TryGetValue(path, out var old)) { changes.Add(new(module, file.Path, PatchChangeKinds.Added, file.Size, null)); added++; }
                else if (!string.Equals(old.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) { changes.Add(new(module, file.Path, PatchChangeKinds.Changed, file.Size, old.Size)); changed++; }
            }
            foreach (var (path, old) in before)
                if (!after.ContainsKey(path)) { changes.Add(new(module, old.Path, PatchChangeKinds.Deleted, null, old.Size)); deleted++; }
            summaries.Add(new PatchModuleChangeSummary(module, added, changed, deleted));
        }
        var ordered = changes
            .OrderBy(change => change.Module, comparer)
            .ThenBy(change => KindOrder(change.Kind))
            .ThenBy(change => change.Path, comparer)
            .ToList();
        return (ordered, summaries);
    }

    /// <summary>Entry name inside a patch archive: module first, then the kind, e.g. "WebApi/ADDED/bin/App.dll".</summary>
    public static string ArchiveEntry(string kind, string module, string path) => $"{module}/{kind}/{path.Replace('\\', '/').TrimStart('/')}";

    private static Dictionary<string, PatchFileEntry> Index(IEnumerable<PatchFileEntry> files)
    {
        var index = new Dictionary<string, PatchFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) index[file.Path.Replace('\\', '/')] = file;
        return index;
    }

    private static int KindOrder(string kind) => kind switch
    {
        PatchChangeKinds.Added => 0,
        PatchChangeKinds.Changed => 1,
        _ => 2
    };
}
