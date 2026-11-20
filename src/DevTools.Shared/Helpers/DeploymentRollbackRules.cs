namespace DevTools.Shared.Helpers;

/// <summary>A deployment as seen by the rollback rules.</summary>
/// <param name="InPlace">True while its changes are still on disk: deployed, failed without restore, or interrupted.</param>
/// <param name="Modules">Module ids it changed; null when unknown (treated as overlapping every module).</param>
public sealed record RollbackDeployment(
    int PackageId,
    string Label,
    DateTime DeployedAtUtc,
    bool InPlace,
    bool BackupExists,
    IReadOnlyCollection<int>? Modules,
    IReadOnlyDictionary<int, string> ModuleNames);

/// <param name="Reason">Why rollback isn't possible (only when <paramref name="CanRollback"/> is false).</param>
/// <param name="Warning">Shown before confirming: newer deployments of the same modules whose files will be overwritten.</param>
public sealed record RollbackAvailability(bool CanRollback, string? Reason, string? Warning = null);

/// <summary>
/// Any deployment can be rolled back while its changes are in place and its backup exists. When a newer deployment that
/// is still in place changed one of the same modules, rollback is still allowed, but it comes with a warning: restoring
/// the older backup overwrites the newer files in those modules.
/// </summary>
public static class DeploymentRollbackRules
{
    public static IReadOnlyDictionary<int, RollbackAvailability> Evaluate(IEnumerable<RollbackDeployment> deployments)
    {
        var list = deployments.ToList();
        var inPlace = list.Where(item => item.InPlace).ToList();
        var result = new Dictionary<int, RollbackAvailability>();
        foreach (var item in list)
        {
            if (!item.InPlace) { result[item.PackageId] = new(false, null); continue; }
            if (!item.BackupExists) { result[item.PackageId] = new(false, "The backup file for this deployment no longer exists."); continue; }
            var newerOverlapping = inPlace
                .Where(other => other.PackageId != item.PackageId && other.DeployedAtUtc > item.DeployedAtUtc && Overlap(item, other).Any())
                .OrderBy(other => other.DeployedAtUtc)
                .ToList();
            if (newerOverlapping.Count == 0) { result[item.PackageId] = new(true, null); continue; }
            var newer = newerOverlapping.Select(other =>
            {
                var shared = Overlap(item, other).ToList();
                var names = shared.Count == 0 ? "the same modules" : string.Join(", ", shared);
                return $"{other.Label} (changed {names})";
            });
            result[item.PackageId] = new(true, null,
                $"Newer deployments changed the same modules and their files will be overwritten: {string.Join("; ", newer)}.");
        }
        return result;
    }

    /// <summary>Names of the modules both deployments changed. Unknown modules overlap everything.</summary>
    private static IEnumerable<string> Overlap(RollbackDeployment first, RollbackDeployment second)
    {
        if (first.Modules is null || second.Modules is null)
        {
            yield return "modules that couldn't be determined";
            yield break;
        }
        foreach (var id in first.Modules.Intersect(second.Modules))
            yield return first.ModuleNames.TryGetValue(id, out var name) ? name : second.ModuleNames.GetValueOrDefault(id, $"module {id}");
    }
}
