using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DevTools.Application.Services.Deployment;
using DevTools.Application.Services.Patching;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;
using DevTools.Web.Data;
using DevTools.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Services;

public interface IPatchManagementService
{
    Task<IReadOnlyList<PatchSystemDto>> GetSystemsAsync(CancellationToken cancellationToken);
    Task<PatchSystemDto> SaveSystemAsync(int? id, SavePatchSystemRequest request, CancellationToken cancellationToken);
    Task DeleteSystemAsync(int id, CancellationToken cancellationToken);
    Task<PatchModuleDto> SaveModuleAsync(int systemId, int? id, SavePatchModuleRequest request, CancellationToken cancellationToken);
    Task DeleteModuleAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PatchSnapshotDto>> GetSnapshotsAsync(int systemId, CancellationToken cancellationToken);
    Task<PatchChangesDto> GetCurrentChangesAsync(int systemId, int? baseSnapshotId, CancellationToken cancellationToken);
    Task<PatchChangesDto> GetSnapshotChangesAsync(int snapshotId, CancellationToken cancellationToken);
    Task<PatchChangesDto> GetPackageChangesAsync(int packageId, CancellationToken cancellationToken);
    Task<PatchSnapshotDto> SetCurrentAsLatestAsync(int systemId, SetLatestRequest request, string? actor, CancellationToken cancellationToken);
    Task<PatchSnapshotDto> SetSnapshotAsLatestAsync(int snapshotId, CancellationToken cancellationToken);
    Task DeleteSnapshotAsync(int snapshotId, CancellationToken cancellationToken);
    Task<PatchPackageDto> CompileAsync(int systemId, CompilePatchRequest request, string? actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<PatchPackageDto>> GetPackagesAsync(int systemId, CancellationToken cancellationToken);
    Task<(string Path, string FileName, string ContentType)> GetDownloadAsync(int packageId, CancellationToken cancellationToken);
    Task DeletePackageAsync(int packageId, CancellationToken cancellationToken);
}

/// <summary>
/// Tracks the files of a system's modules on this server so the changes since the version on UAT/production can be
/// listed and compiled into a patch archive (&lt;module&gt;/ADDED, CHANGED, DELETED). Every module has its own latest
/// version (a pointer to the snapshot holding it), so a patch can contain any subset of modules and only those modules'
/// baselines move when it is marked as delivered. Snapshots store each file's path, size, last write time and SHA-256;
/// file contents are only read when hashing or compiling.
/// </summary>
public sealed class PatchManagementService(
    DevToolsDbContext database,
    IPatchScanner scanner,
    IPackageArchiveService archives,
    IDeploymentPaths paths) : IPatchManagementService
{
    public const int MaxChangesReturned = 5000;
    private const string SourceLatest = "latest";
    private const string SourcePatch = "patch";
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> SystemLocks = new();

    // ---------- Systems and modules ----------

    public async Task<IReadOnlyList<PatchSystemDto>> GetSystemsAsync(CancellationToken cancellationToken)
    {
        var systems = await database.PatchSystems.AsNoTracking().Include(item => item.Modules).OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var versions = await LatestVersionsAsync(systems.SelectMany(system => system.Modules), cancellationToken);
        return systems.Select(system => ToDto(system, versions)).ToList();
    }

    public async Task<PatchSystemDto> SaveSystemAsync(int? id, SavePatchSystemRequest request, CancellationToken cancellationToken)
    {
        var name = Required(request.Name, "System name", 120);
        var output = string.IsNullOrWhiteSpace(request.OutputDirectory) ? null : paths.Normalize(request.OutputDirectory, "Patch output folder");
        if (output is not null) paths.EnsureSafeStorageFolder(output, "patch output");
        if (await database.PatchSystems.AnyAsync(item => item.Id != (id ?? 0) && item.Name.ToLower() == name.ToLower(), cancellationToken))
            throw new ArgumentException("A patch system with this name already exists.");
        PatchSystem entity;
        if (id is null)
        {
            entity = new PatchSystem { Name = name, OutputDirectory = output };
            database.PatchSystems.Add(entity);
        }
        else
        {
            entity = await database.PatchSystems.Include(item => item.Modules).FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("Patch system not found.");
            entity.Name = name;
            entity.OutputDirectory = output;
            if (output is not null && entity.Modules.FirstOrDefault(module => paths.Overlaps(module.SourcePath, output)) is { } overlapping)
                throw new ArgumentException($"The patch output folder overlaps {overlapping.Name}'s folder. Keep patches outside the module folders.");
        }
        await database.SaveChangesAsync(cancellationToken);
        return ToDto(entity, await LatestVersionsAsync(entity.Modules, cancellationToken));
    }

    public async Task DeleteSystemAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await database.PatchSystems.Include(item => item.Packages).FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Patch system not found.");
        foreach (var package in entity.Packages) TryDelete(package.StoredPath);
        database.PatchSystems.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<PatchModuleDto> SaveModuleAsync(int systemId, int? id, SavePatchModuleRequest request, CancellationToken cancellationToken)
    {
        var name = Required(request.Name, "Module name", 120);
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\r', '\n', '\t']) >= 0 || name is "." or "..")
            throw new ArgumentException("Module name must be a valid folder name, because it becomes a folder inside the patch.");
        var source = paths.Normalize(Required(request.SourcePath, "Module folder", 2048), "Module folder");
        paths.EnsureSafeTargetFolder(source);
        var exclusionsError = DeploymentPathMatcher.Validate(request.Exclusions);
        if (exclusionsError is not null) throw new ArgumentException(exclusionsError);
        var system = await database.PatchSystems.AsNoTracking().FirstOrDefaultAsync(item => item.Id == systemId, cancellationToken)
            ?? throw new KeyNotFoundException("Patch system not found.");
        if (paths.Overlaps(source, paths.PatchOutputDirectory(system)))
            throw new ArgumentException("The module folder overlaps the patch output folder. Keep patches outside the module folders.");
        var siblings = await database.PatchModules.Where(item => item.PatchSystemId == systemId && item.Id != (id ?? 0)).ToListAsync(cancellationToken);
        if (siblings.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("This system already has a module with that name.");
        if (siblings.Any(item => paths.Overlaps(item.SourcePath, source))) throw new ArgumentException("Another module in this system already uses this folder or a folder inside it.");

        PatchModule entity;
        if (id is null)
        {
            entity = new PatchModule { PatchSystemId = systemId, Name = name, SourcePath = source };
            database.PatchModules.Add(entity);
        }
        else
        {
            entity = await database.PatchModules.FirstOrDefaultAsync(item => item.Id == id && item.PatchSystemId == systemId, cancellationToken)
                ?? throw new KeyNotFoundException("Module not found.");
        }
        entity.Name = name;
        entity.SourcePath = source;
        entity.Exclusions = DeploymentPathMatcher.Normalize(request.Exclusions);
        await database.SaveChangesAsync(cancellationToken);
        return ToDto(entity, await LatestVersionsAsync([entity], cancellationToken));
    }

    public async Task DeleteModuleAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await database.PatchModules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) ?? throw new KeyNotFoundException("Module not found.");
        database.PatchModules.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
        await SyncLatestFlagsAsync(entity.PatchSystemId, cancellationToken);
    }

    // ---------- Snapshots and changes ----------

    public async Task<IReadOnlyList<PatchSnapshotDto>> GetSnapshotsAsync(int systemId, CancellationToken cancellationToken)
    {
        var modules = await database.PatchModules.AsNoTracking().Where(item => item.PatchSystemId == systemId)
            .Select(item => new { item.Name, item.LatestSnapshotId }).ToListAsync(cancellationToken);
        var packages = await database.PatchPackages.AsNoTracking().Where(item => item.PatchSystemId == systemId)
            .Select(item => new { item.Id, item.SnapshotId }).ToListAsync(cancellationToken);
        var snapshots = await database.PatchSnapshots.AsNoTracking().Where(item => item.PatchSystemId == systemId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new { item.Id, item.PatchSystemId, item.CreatedAtUtc, item.Source, item.Label, item.CreatedBy, item.MarkedLatestAtUtc, item.FileCount, item.TotalBytes, item.Modules })
            .ToListAsync(cancellationToken);
        return snapshots.Select(item =>
        {
            var latestFor = modules.Where(module => module.LatestSnapshotId == item.Id).Select(module => module.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return new PatchSnapshotDto(item.Id, item.PatchSystemId, Utc(item.CreatedAtUtc), item.Source, item.Label, item.CreatedBy, latestFor.Count > 0,
                item.MarkedLatestAtUtc is null ? null : Utc(item.MarkedLatestAtUtc.Value), item.FileCount, item.TotalBytes,
                packages.FirstOrDefault(package => package.SnapshotId == item.Id)?.Id, PatchCodec.ReadNames(item.Modules), latestFor);
        }).ToList();
    }

    /// <summary>
    /// Changes of every module since its own latest version, or since <paramref name="baseSnapshotId"/> for the modules
    /// that snapshot contains (the others still use their latest version).
    /// </summary>
    public async Task<PatchChangesDto> GetCurrentChangesAsync(int systemId, int? baseSnapshotId, CancellationToken cancellationToken)
    {
        var system = await LoadSystemAsync(systemId, cancellationToken);
        var chosen = await LoadChosenBaseAsync(systemId, baseSnapshotId, cancellationToken);
        var bases = await ResolveBasesAsync(system.Modules, chosen, cancellationToken);
        var comparisons = await CompareCurrentAsync(system.Modules, bases, cancellationToken);
        return ToChangesDto(chosen, null, Combine(comparisons));
    }

    /// <summary>What a snapshot recorded: each of its modules compared with the previous snapshot containing that module.</summary>
    public async Task<PatchChangesDto> GetSnapshotChangesAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == snapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("Snapshot not found.");
        var targets = PatchCodec.ReadManifest(snapshot.ManifestGz);
        var previous = new Dictionary<PatchModuleManifest, (PatchSnapshot Snapshot, PatchModuleManifest Files)>();
        var earlier = database.PatchSnapshots.AsNoTracking()
            .Where(item => item.PatchSystemId == snapshot.PatchSystemId && item.CreatedAtUtc < snapshot.CreatedAtUtc)
            .OrderByDescending(item => item.CreatedAtUtc).AsAsyncEnumerable();
        await foreach (var candidate in earlier.WithCancellation(cancellationToken))
        {
            var manifests = PatchCodec.ReadManifest(candidate.ManifestGz);
            foreach (var target in targets.Where(item => !previous.ContainsKey(item)))
                if (Find(manifests, target.ModuleId, target.ModuleName) is { } match) previous[target] = (candidate, match);
            if (previous.Count == targets.Count) break;
        }
        var comparisons = targets.Select(target => previous.TryGetValue(target, out var found)
            ? new ModuleComparison(null, target.ModuleName, found.Snapshot, found.Files, target)
            : new ModuleComparison(null, target.ModuleName, null, null, target)).ToList();
        return ToChangesDto(null, snapshot, Combine(comparisons));
    }

    /// <summary>What a compiled patch contains, grouped by module.</summary>
    public async Task<PatchChangesDto> GetPackageChangesAsync(int packageId, CancellationToken cancellationToken)
    {
        var package = await database.PatchPackages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken)
            ?? throw new KeyNotFoundException("Patch not found.");
        if (package.ChangesGz is { } stored && PatchCodec.Read<PatchChangeSet>(stored) is { } changeSet)
            return ToChangesDto(null, null, changeSet) with { TargetSnapshotId = package.SnapshotId, ComparedAt = Utc(package.CreatedAtUtc) };

        // Patches compiled before v7 kept no change list: rebuild it from the two whole-system snapshots.
        var snapshot = await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == package.SnapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("The snapshot of this patch no longer exists.");
        PatchSnapshot? baseline = null;
        if (package.BaseSnapshotId is { } baseId)
            baseline = await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == baseId, cancellationToken)
                ?? throw new KeyNotFoundException("The snapshot this patch was compared with has been deleted, so its file list can't be rebuilt. The patch file itself is unchanged.");
        var baseManifests = baseline is null ? [] : PatchCodec.ReadManifest(baseline.ManifestGz);
        var comparisons = PatchCodec.ReadManifest(snapshot.ManifestGz).Select(target =>
            new ModuleComparison(null, target.ModuleName, baseline, Find(baseManifests, target.ModuleId, target.ModuleName), target)).ToList();
        return ToChangesDto(null, snapshot, Combine(comparisons));
    }

    public async Task<PatchSnapshotDto> SetCurrentAsLatestAsync(int systemId, SetLatestRequest request, string? actor, CancellationToken cancellationToken)
    {
        var text = Optional(request.Label, "Label", 500);
        return await WithSystemLockAsync(systemId, async () =>
        {
            var system = await LoadSystemAsync(systemId, cancellationToken);
            var selected = SelectModules(system, request.ModuleIds);
            var bases = await ResolveBasesAsync(selected, null, cancellationToken);
            var current = await ScanAsync(selected, bases, cancellationToken);
            var snapshot = NewSnapshot(systemId, SourceLatest, text ?? "Set as latest version", actor, current);
            database.PatchSnapshots.Add(snapshot);
            await database.SaveChangesAsync(cancellationToken);
            await PointModulesAsync(systemId, selected.Select(module => module.Id), snapshot.Id, cancellationToken);
            return await SnapshotDtoAsync(snapshot.Id, cancellationToken);
        });
    }

    /// <summary>Makes a snapshot the latest version of every module it contains (other modules keep theirs).</summary>
    public async Task<PatchSnapshotDto> SetSnapshotAsLatestAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == snapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("Snapshot not found.");
        var manifests = PatchCodec.ReadManifest(snapshot.ManifestGz);
        var modules = await database.PatchModules.AsNoTracking().Where(item => item.PatchSystemId == snapshot.PatchSystemId).ToListAsync(cancellationToken);
        var covered = modules.Where(module => Find(manifests, module.Id, module.Name) is not null).Select(module => module.Id).ToList();
        if (covered.Count == 0) throw new InvalidOperationException("None of the modules in this snapshot exist in the system any more.");
        await PointModulesAsync(snapshot.PatchSystemId, covered, snapshot.Id, cancellationToken);
        return await SnapshotDtoAsync(snapshot.Id, cancellationToken);
    }

    public async Task DeleteSnapshotAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await database.PatchSnapshots.FirstOrDefaultAsync(item => item.Id == snapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("Snapshot not found.");
        var latestFor = await database.PatchModules.Where(item => item.LatestSnapshotId == snapshot.Id).Select(item => item.Name).ToListAsync(cancellationToken);
        if (latestFor.Count > 0)
            throw new InvalidOperationException($"This snapshot is the latest version of {string.Join(", ", latestFor)}. Set another version as latest for {(latestFor.Count == 1 ? "it" : "them")} first.");
        if (await database.PatchPackages.AnyAsync(item => item.SnapshotId == snapshot.Id || item.BaseSnapshotId == snapshot.Id, cancellationToken))
            throw new InvalidOperationException("A compiled patch uses this snapshot. Delete the patch first.");
        database.PatchSnapshots.Remove(snapshot);
        await database.SaveChangesAsync(cancellationToken);
    }

    // ---------- Compiling patches ----------

    public async Task<PatchPackageDto> CompileAsync(int systemId, CompilePatchRequest request, string? actor, CancellationToken cancellationToken)
    {
        var format = (request.Format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (!PatchFormats.IsValid(format)) throw new ArgumentException("Choose .zip or .7z for the patch.");
        var label = Optional(request.Label, "Description", 500);
        return await WithSystemLockAsync(systemId, async () =>
        {
            var system = await LoadSystemAsync(systemId, cancellationToken);
            var selected = SelectModules(system, request.ModuleIds);
            var chosen = await LoadChosenBaseAsync(systemId, request.BaseSnapshotId, cancellationToken);
            var bases = await ResolveBasesAsync(selected, chosen, cancellationToken);
            var comparisons = await CompareCurrentAsync(selected, bases, cancellationToken);
            var changeSet = Combine(comparisons);
            var changes = changeSet.Files;
            if (changes.Count == 0)
                throw new InvalidOperationException(selected.Count == 1 ? $"{selected[0].Name} has no changes to compile." : "The selected modules have no changes to compile.");

            var modulesByName = selected.ToDictionary(module => module.Name, StringComparer.OrdinalIgnoreCase);
            var sources = changes
                .Where(change => change.Kind != PatchChangeKinds.Deleted)
                .Select(change => new ArchiveSource(PatchDiff.ArchiveEntry(change.Kind, change.Module, change.Path),
                    PackageArchiveService.ResolveInside(modulesByName[change.Module].SourcePath, change.Path)))
                .ToList();
            var textEntries = new Dictionary<string, string>
            {
                ["PATCH-NOTES.txt"] = PatchNotes(system, label, actor, changeSet)
            };
            foreach (var group in changes.Where(change => change.Kind == PatchChangeKinds.Deleted).GroupBy(change => change.Module, StringComparer.OrdinalIgnoreCase))
                textEntries[$"{group.Key}/{PatchChangeKinds.Deleted}/DELETED-FILES.txt"] = string.Join("\r\n", group.Select(change => change.Path)) + "\r\n";

            var directory = paths.PatchOutputDirectory(system);
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var fileName = $"{SafeFileName(system.Name)}-patch-{stamp}.{format}";
            var storedPath = Path.Combine(directory, fileName);
            for (var counter = 2; File.Exists(storedPath); counter++) storedPath = Path.Combine(directory, $"{SafeFileName(system.Name)}-patch-{stamp}-{counter}.{format}");
            await Task.Run(() => archives.CreateArchive(storedPath, format, sources, textEntries, null, cancellationToken), cancellationToken);

            // The snapshot records the selected modules' files exactly as compiled, so it can become their latest version.
            var snapshot = NewSnapshot(systemId, SourcePatch, label ?? $"Patch {Path.GetFileName(storedPath)}", actor, comparisons.Select(item => item.Current).ToList());
            database.PatchSnapshots.Add(snapshot);
            await database.SaveChangesAsync(cancellationToken);
            var package = new PatchPackage
            {
                PatchSystemId = systemId,
                CreatedAtUtc = DateTime.UtcNow,
                Format = format,
                FileName = Path.GetFileName(storedPath),
                StoredPath = storedPath,
                SizeBytes = new FileInfo(storedPath).Length,
                Label = label,
                CreatedBy = actor,
                Added = changes.Count(change => change.Kind == PatchChangeKinds.Added),
                Changed = changes.Count(change => change.Kind == PatchChangeKinds.Changed),
                Deleted = changes.Count(change => change.Kind == PatchChangeKinds.Deleted),
                BaseSnapshotId = chosen?.Id,
                SnapshotId = snapshot.Id,
                Modules = PatchCodec.WriteNames(changeSet.Modules.Where(HasChanges).Select(item => item.Module)),
                ChangesGz = PatchCodec.Write(changeSet)
            };
            database.PatchPackages.Add(package);
            await database.SaveChangesAsync(cancellationToken);
            if (request.SetAsLatest) await PointModulesAsync(systemId, selected.Select(module => module.Id), snapshot.Id, cancellationToken);
            return ToDto(package, chosen?.CreatedAtUtc, request.SetAsLatest, null);
        });
    }

    public async Task<IReadOnlyList<PatchPackageDto>> GetPackagesAsync(int systemId, CancellationToken cancellationToken)
    {
        var packages = await database.PatchPackages.AsNoTracking().Where(item => item.PatchSystemId == systemId).OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new PatchPackage
            {
                Id = item.Id, PatchSystemId = item.PatchSystemId, CreatedAtUtc = item.CreatedAtUtc, Format = item.Format, FileName = item.FileName,
                StoredPath = item.StoredPath, SizeBytes = item.SizeBytes, Label = item.Label, CreatedBy = item.CreatedBy, Added = item.Added,
                Changed = item.Changed, Deleted = item.Deleted, BaseSnapshotId = item.BaseSnapshotId, SnapshotId = item.SnapshotId, Modules = item.Modules
            }).ToListAsync(cancellationToken);
        var snapshots = await database.PatchSnapshots.AsNoTracking().Where(item => item.PatchSystemId == systemId)
            .Select(item => new { item.Id, item.CreatedAtUtc, item.Modules }).ToListAsync(cancellationToken);
        var pointers = await database.PatchModules.AsNoTracking().Where(item => item.PatchSystemId == systemId && item.LatestSnapshotId != null)
            .Select(item => item.LatestSnapshotId!.Value).ToListAsync(cancellationToken);
        return packages.Select(package => ToDto(package,
            snapshots.FirstOrDefault(item => item.Id == package.BaseSnapshotId)?.CreatedAtUtc,
            pointers.Contains(package.SnapshotId),
            snapshots.FirstOrDefault(item => item.Id == package.SnapshotId)?.Modules)).ToList();
    }

    public async Task<(string Path, string FileName, string ContentType)> GetDownloadAsync(int packageId, CancellationToken cancellationToken)
    {
        var package = await database.PatchPackages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken)
            ?? throw new KeyNotFoundException("Patch not found.");
        if (!File.Exists(package.StoredPath)) throw new KeyNotFoundException("The patch file no longer exists on the server.");
        return (package.StoredPath, package.FileName, package.Format == PatchFormats.Zip ? "application/zip" : "application/x-7z-compressed");
    }

    public async Task DeletePackageAsync(int packageId, CancellationToken cancellationToken)
    {
        var package = await database.PatchPackages.FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken)
            ?? throw new KeyNotFoundException("Patch not found.");
        TryDelete(package.StoredPath);
        database.PatchPackages.Remove(package);
        await database.SaveChangesAsync(cancellationToken);
    }

    // ---------- Helpers ----------

    /// <summary>One module's base (the snapshot its changes are measured from) and its files now or in a later snapshot.</summary>
    private sealed record ModuleComparison(PatchModule? Module, string Name, PatchSnapshot? Base, PatchModuleManifest? BaseFiles, PatchModuleManifest Current);

    private async Task<PatchSystem> LoadSystemAsync(int systemId, CancellationToken cancellationToken)
    {
        var system = await database.PatchSystems.AsNoTracking().Include(item => item.Modules).FirstOrDefaultAsync(item => item.Id == systemId, cancellationToken)
            ?? throw new KeyNotFoundException("Patch system not found.");
        if (system.Modules.Count == 0) throw new InvalidOperationException("Add the system's modules first.");
        return system;
    }

    private static List<PatchModule> SelectModules(PatchSystem system, IReadOnlyList<int>? moduleIds)
    {
        if (moduleIds is null || moduleIds.Count == 0) return system.Modules.OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = moduleIds.Distinct().ToList();
        var selected = system.Modules.Where(module => wanted.Contains(module.Id)).OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count != wanted.Count) throw new KeyNotFoundException("A selected module no longer exists. Reload the page and choose again.");
        return selected;
    }

    private async Task<PatchSnapshot?> LoadChosenBaseAsync(int systemId, int? snapshotId, CancellationToken cancellationToken) =>
        snapshotId is { } id
            ? await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.PatchSystemId == systemId, cancellationToken)
              ?? throw new KeyNotFoundException("The selected snapshot was not found.")
            : null;

    /// <summary>Each module's base: the chosen snapshot when it contains the module, otherwise the module's own latest version.</summary>
    private async Task<Dictionary<int, (PatchSnapshot Snapshot, PatchModuleManifest Files)>> ResolveBasesAsync(
        IReadOnlyCollection<PatchModule> modules, PatchSnapshot? chosen, CancellationToken cancellationToken)
    {
        var pointerIds = modules.Where(module => module.LatestSnapshotId is not null).Select(module => module.LatestSnapshotId!.Value).Distinct().ToList();
        var loaded = await database.PatchSnapshots.AsNoTracking().Where(item => pointerIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var manifests = loaded.ToDictionary(item => item.Id, item => (Snapshot: item, Files: PatchCodec.ReadManifest(item.ManifestGz)));
        var chosenFiles = chosen is null ? null : PatchCodec.ReadManifest(chosen.ManifestGz);
        var result = new Dictionary<int, (PatchSnapshot, PatchModuleManifest)>();
        foreach (var module in modules)
        {
            if (chosen is not null && Find(chosenFiles!, module.Id, module.Name) is { } fromChosen) result[module.Id] = (chosen, fromChosen);
            else if (module.LatestSnapshotId is { } pointer && manifests.TryGetValue(pointer, out var latest) && Find(latest.Files, module.Id, module.Name) is { } fromLatest)
                result[module.Id] = (latest.Snapshot, fromLatest);
        }
        return result;
    }

    private async Task<List<ModuleComparison>> CompareCurrentAsync(IReadOnlyCollection<PatchModule> modules,
        Dictionary<int, (PatchSnapshot Snapshot, PatchModuleManifest Files)> bases, CancellationToken cancellationToken)
    {
        var current = await ScanAsync(modules, bases, cancellationToken);
        return modules.Zip(current, (module, files) => bases.TryGetValue(module.Id, out var found)
            ? new ModuleComparison(module, module.Name, found.Snapshot, found.Files, files)
            : new ModuleComparison(module, module.Name, null, null, files)).ToList();
    }

    /// <summary>Scans the modules (in the given order), reusing a base file's hash when its size and write time are unchanged.</summary>
    private async Task<List<PatchModuleManifest>> ScanAsync(IReadOnlyCollection<PatchModule> modules,
        Dictionary<int, (PatchSnapshot Snapshot, PatchModuleManifest Files)> bases, CancellationToken cancellationToken)
    {
        var result = new List<PatchModuleManifest>();
        foreach (var module in modules)
        {
            IReadOnlyDictionary<string, PatchFileEntry>? known = bases.TryGetValue(module.Id, out var found)
                ? found.Files.Files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(file => file.Key, file => file.First(), StringComparer.OrdinalIgnoreCase)
                : null;
            result.Add(await scanner.ScanAsync(module.Id, module.Name, module.SourcePath, module.Exclusions, known, cancellationToken));
        }
        return result;
    }

    private static PatchChangeSet Combine(IEnumerable<ModuleComparison> comparisons) =>
        PatchDiff.CompareEach(comparisons.Select(item => new PatchModuleComparison(item.Module?.Id ?? item.Current.ModuleId, item.Name, item.Base?.Id,
            item.Base is null ? null : Utc(item.Base.CreatedAtUtc), item.Base?.Label, item.BaseFiles, item.Current)));

    private static bool HasChanges(PatchModuleChangeSummary summary) => summary.Added + summary.Changed + summary.Deleted > 0;

    private static PatchModuleManifest? Find(IEnumerable<PatchModuleManifest> manifests, int moduleId, string name)
    {
        var list = manifests as IReadOnlyCollection<PatchModuleManifest> ?? manifests.ToList();
        return list.FirstOrDefault(item => item.ModuleId == moduleId && moduleId != 0)
            ?? list.FirstOrDefault(item => item.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Moves the given modules' latest version to a snapshot and keeps the snapshot "is latest" flags in step.</summary>
    private async Task PointModulesAsync(int systemId, IEnumerable<int> moduleIds, int snapshotId, CancellationToken cancellationToken)
    {
        var ids = moduleIds.ToList();
        await database.PatchModules.Where(item => item.PatchSystemId == systemId && ids.Contains(item.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LatestSnapshotId, snapshotId), cancellationToken);
        await database.PatchSnapshots.Where(item => item.Id == snapshotId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.MarkedLatestAtUtc, DateTime.UtcNow), cancellationToken);
        await SyncLatestFlagsAsync(systemId, cancellationToken);
    }

    private async Task SyncLatestFlagsAsync(int systemId, CancellationToken cancellationToken)
    {
        var ids = await database.PatchModules.Where(item => item.PatchSystemId == systemId && item.LatestSnapshotId != null)
            .Select(item => item.LatestSnapshotId!.Value).Distinct().ToListAsync(cancellationToken);
        await database.PatchSnapshots.Where(item => item.PatchSystemId == systemId && item.IsLatest && !ids.Contains(item.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLatest, false), cancellationToken);
        await database.PatchSnapshots.Where(item => item.PatchSystemId == systemId && !item.IsLatest && ids.Contains(item.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLatest, true), cancellationToken);
    }

    private async Task<Dictionary<int, (DateTime CreatedAtUtc, string? Label)>> LatestVersionsAsync(IEnumerable<PatchModule> modules, CancellationToken cancellationToken)
    {
        var ids = modules.Where(module => module.LatestSnapshotId is not null).Select(module => module.LatestSnapshotId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await database.PatchSnapshots.AsNoTracking().Where(item => ids.Contains(item.Id))
            .Select(item => new { item.Id, item.CreatedAtUtc, item.Label })
            .ToDictionaryAsync(item => item.Id, item => (item.CreatedAtUtc, item.Label), cancellationToken);
    }

    private async Task<PatchSnapshotDto> SnapshotDtoAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var systemId = await database.PatchSnapshots.Where(item => item.Id == snapshotId).Select(item => item.PatchSystemId).FirstAsync(cancellationToken);
        return (await GetSnapshotsAsync(systemId, cancellationToken)).First(item => item.Id == snapshotId);
    }

    private static PatchChangesDto ToChangesDto(PatchSnapshot? chosen, PatchSnapshot? target, PatchChangeSet changeSet)
    {
        var changes = changeSet.Files;
        return new PatchChangesDto(chosen?.Id, chosen is null ? null : Utc(chosen.CreatedAtUtc), chosen?.Label, target?.Id,
            target is null ? DateTimeOffset.UtcNow : Utc(target.CreatedAtUtc),
            changes.Count(change => change.Kind == PatchChangeKinds.Added),
            changes.Count(change => change.Kind == PatchChangeKinds.Changed),
            changes.Count(change => change.Kind == PatchChangeKinds.Deleted),
            changeSet.Modules, changes.Take(MaxChangesReturned).ToList(), changes.Count > MaxChangesReturned);
    }

    private static PatchSnapshot NewSnapshot(int systemId, string source, string? label, string? actor, IReadOnlyList<PatchModuleManifest> manifests) => new()
    {
        PatchSystemId = systemId,
        CreatedAtUtc = DateTime.UtcNow,
        Source = source,
        Label = label,
        CreatedBy = actor,
        FileCount = manifests.Sum(module => module.Files.Count),
        TotalBytes = manifests.Sum(module => module.Files.Sum(file => file.Size)),
        ManifestGz = PatchCodec.Write(manifests),
        Modules = PatchCodec.WriteNames(manifests.Select(module => module.ModuleName))
    };

    private static string PatchNotes(PatchSystem system, string? label, string? actor, PatchChangeSet changeSet)
    {
        var text = new StringBuilder();
        text.AppendLine($"Patch for {system.Name}");
        if (!string.IsNullOrWhiteSpace(label)) text.AppendLine($"Description: {label}");
        text.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(string.IsNullOrWhiteSpace(actor) ? "" : $" by {actor}")}");
        text.AppendLine($"Modules: {string.Join(", ", changeSet.Modules.Where(HasChanges).Select(item => item.Module))}");
        text.AppendLine();
        text.AppendLine("How to apply (for each module folder in this patch):");
        text.AppendLine("  <module>/ADDED/...    copy these new files into the module folder.");
        text.AppendLine("  <module>/CHANGED/...  copy these over the existing files in the module folder.");
        text.AppendLine("  <module>/DELETED/DELETED-FILES.txt  delete the listed files from the module folder.");
        text.AppendLine();
        foreach (var summary in changeSet.Modules.Where(HasChanges))
        {
            text.AppendLine($"[{summary.Module}] {summary.Added} added, {summary.Changed} changed, {summary.Deleted} deleted");
            text.AppendLine(summary.BaseCreatedAt is null
                ? "  Compared with: nothing (no latest version was set, so every file is ADDED)"
                : $"  Compared with: {summary.BaseLabel ?? "snapshot"} ({summary.BaseCreatedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
            foreach (var change in changeSet.Files.Where(item => item.Module.Equals(summary.Module, StringComparison.OrdinalIgnoreCase)))
                text.AppendLine($"  {change.Kind,-7} {change.Path}");
            text.AppendLine();
        }
        return text.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
    }

    private static async Task<T> WithSystemLockAsync<T>(int systemId, Func<Task<T>> action)
    {
        var gate = SystemLocks.GetOrAdd(systemId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A snapshot or patch is already being created for this system. Try again when it finishes.");
        try { return await action(); }
        finally { gate.Release(); }
    }

    private PatchSystemDto ToDto(PatchSystem entity, Dictionary<int, (DateTime CreatedAtUtc, string? Label)> versions) =>
        new(entity.Id, entity.Name, entity.OutputDirectory, paths.PatchOutputDirectory(entity),
            entity.Modules.OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase).Select(module => ToDto(module, versions)).ToList());

    private static PatchModuleDto ToDto(PatchModule entity, Dictionary<int, (DateTime CreatedAtUtc, string? Label)> versions)
    {
        var version = entity.LatestSnapshotId is { } id && versions.TryGetValue(id, out var found) ? found : ((DateTime, string?)?)null;
        return new(entity.Id, entity.PatchSystemId, entity.Name, entity.SourcePath, entity.Exclusions,
            version is null ? null : entity.LatestSnapshotId, version is null ? null : Utc(version.Value.Item1), version?.Item2);
    }

    private static PatchPackageDto ToDto(PatchPackage entity, DateTime? baseCreatedAtUtc, bool snapshotIsLatest, string? snapshotModules)
    {
        var modules = PatchCodec.ReadNames(entity.Modules);
        if (modules.Count == 0) modules = PatchCodec.ReadNames(snapshotModules);
        return new(entity.Id, entity.PatchSystemId, Utc(entity.CreatedAtUtc), entity.Format, entity.FileName, entity.SizeBytes, entity.Label, entity.CreatedBy,
            entity.Added, entity.Changed, entity.Deleted, entity.BaseSnapshotId, baseCreatedAtUtc is null ? null : Utc(baseCreatedAtUtc.Value),
            entity.SnapshotId, snapshotIsLatest, File.Exists(entity.StoredPath), modules);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string Required(string? value, string label, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ArgumentException($"{label} is required.");
        if (trimmed.Length > max) throw new ArgumentException($"{label} must be {max} characters or fewer.");
        return trimmed;
    }

    private static string? Optional(string? value, string label, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > max) throw new ArgumentException($"{label} must be {max} characters or fewer.");
        return trimmed;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat([' ', '/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim('-', '.');
        return string.IsNullOrEmpty(cleaned) ? "system" : cleaned;
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
