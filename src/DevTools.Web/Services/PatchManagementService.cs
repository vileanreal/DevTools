using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
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
    Task<PatchSnapshotDto> SetCurrentAsLatestAsync(int systemId, string? label, string? actor, CancellationToken cancellationToken);
    Task<PatchSnapshotDto> SetSnapshotAsLatestAsync(int snapshotId, CancellationToken cancellationToken);
    Task DeleteSnapshotAsync(int snapshotId, CancellationToken cancellationToken);
    Task<PatchPackageDto> CompileAsync(int systemId, CompilePatchRequest request, string? actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<PatchPackageDto>> GetPackagesAsync(int systemId, CancellationToken cancellationToken);
    Task<(string Path, string FileName, string ContentType)> GetDownloadAsync(int packageId, CancellationToken cancellationToken);
    Task DeletePackageAsync(int packageId, CancellationToken cancellationToken);
}

/// <summary>
/// Tracks the files of a system's modules on this server so the changes since the version on UAT/production can be
/// listed and compiled into a patch archive (ADDED/, CHANGED/, DELETED/). Snapshots store each file's path, size,
/// last write time and SHA-256; file contents are only read when hashing or compiling.
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> SystemLocks = new();

    // ---------- Systems and modules ----------

    public async Task<IReadOnlyList<PatchSystemDto>> GetSystemsAsync(CancellationToken cancellationToken)
    {
        var systems = await database.PatchSystems.AsNoTracking().Include(item => item.Modules).OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var latest = await database.PatchSnapshots.AsNoTracking().Where(item => item.IsLatest)
            .Select(item => new { item.Id, item.PatchSystemId, item.CreatedAtUtc, item.Source, item.Label, item.CreatedBy, item.IsLatest, item.MarkedLatestAtUtc, item.FileCount, item.TotalBytes })
            .ToListAsync(cancellationToken);
        return systems.Select(system =>
        {
            var snapshot = latest.FirstOrDefault(item => item.PatchSystemId == system.Id);
            return ToDto(system, snapshot is null ? null : new PatchSnapshotDto(snapshot.Id, snapshot.PatchSystemId, Utc(snapshot.CreatedAtUtc), snapshot.Source, snapshot.Label,
                snapshot.CreatedBy, true, snapshot.MarkedLatestAtUtc is null ? null : Utc(snapshot.MarkedLatestAtUtc.Value), snapshot.FileCount, snapshot.TotalBytes, null));
        }).ToList();
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
        return ToDto(entity, null);
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
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or "..")
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
        return ToDto(entity);
    }

    public async Task DeleteModuleAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await database.PatchModules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) ?? throw new KeyNotFoundException("Module not found.");
        database.PatchModules.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
    }

    // ---------- Snapshots and changes ----------

    public async Task<IReadOnlyList<PatchSnapshotDto>> GetSnapshotsAsync(int systemId, CancellationToken cancellationToken)
    {
        var packages = await database.PatchPackages.AsNoTracking().Where(item => item.PatchSystemId == systemId)
            .Select(item => new { item.Id, item.SnapshotId }).ToListAsync(cancellationToken);
        var snapshots = await database.PatchSnapshots.AsNoTracking().Where(item => item.PatchSystemId == systemId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new { item.Id, item.PatchSystemId, item.CreatedAtUtc, item.Source, item.Label, item.CreatedBy, item.IsLatest, item.MarkedLatestAtUtc, item.FileCount, item.TotalBytes })
            .ToListAsync(cancellationToken);
        return snapshots.Select(item => new PatchSnapshotDto(item.Id, item.PatchSystemId, Utc(item.CreatedAtUtc), item.Source, item.Label, item.CreatedBy, item.IsLatest,
            item.MarkedLatestAtUtc is null ? null : Utc(item.MarkedLatestAtUtc.Value), item.FileCount, item.TotalBytes,
            packages.FirstOrDefault(package => package.SnapshotId == item.Id)?.Id)).ToList();
    }

    public async Task<PatchChangesDto> GetCurrentChangesAsync(int systemId, int? baseSnapshotId, CancellationToken cancellationToken)
    {
        var system = await LoadSystemAsync(systemId, cancellationToken);
        var baseline = await ResolveBaseAsync(systemId, baseSnapshotId, cancellationToken);
        var baseManifests = baseline is null ? [] : ReadManifest(baseline.ManifestGz);
        var current = await ScanAsync(system, baseManifests, cancellationToken);
        return BuildChanges(baseline, null, baseManifests, current);
    }

    public async Task<PatchChangesDto> GetSnapshotChangesAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == snapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("Snapshot not found.");
        var previous = await database.PatchSnapshots.AsNoTracking()
            .Where(item => item.PatchSystemId == snapshot.PatchSystemId && item.CreatedAtUtc < snapshot.CreatedAtUtc)
            .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        return BuildChanges(previous, snapshot, previous is null ? [] : ReadManifest(previous.ManifestGz), ReadManifest(snapshot.ManifestGz));
    }

    /// <summary>What a compiled patch contains: its snapshot compared with the snapshot it was measured from.</summary>
    public async Task<PatchChangesDto> GetPackageChangesAsync(int packageId, CancellationToken cancellationToken)
    {
        var package = await database.PatchPackages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken)
            ?? throw new KeyNotFoundException("Patch not found.");
        var snapshot = await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == package.SnapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("The snapshot of this patch no longer exists.");
        PatchSnapshot? baseline = null;
        if (package.BaseSnapshotId is { } baseId)
            baseline = await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == baseId, cancellationToken)
                ?? throw new KeyNotFoundException("The snapshot this patch was compared with has been deleted, so its file list can't be rebuilt. The patch file itself is unchanged.");
        return BuildChanges(baseline, snapshot, baseline is null ? [] : ReadManifest(baseline.ManifestGz), ReadManifest(snapshot.ManifestGz));
    }

    public async Task<PatchSnapshotDto> SetCurrentAsLatestAsync(int systemId, string? label, string? actor, CancellationToken cancellationToken)
    {
        var text = Optional(label, "Label", 500);
        return await WithSystemLockAsync(systemId, async () =>
        {
            var system = await LoadSystemAsync(systemId, cancellationToken);
            var previous = await database.PatchSnapshots.AsNoTracking().Where(item => item.PatchSystemId == systemId && item.IsLatest).FirstOrDefaultAsync(cancellationToken);
            var current = await ScanAsync(system, previous is null ? [] : ReadManifest(previous.ManifestGz), cancellationToken);
            var snapshot = NewSnapshot(systemId, SourceLatest, text ?? "Set as latest version", actor, current);
            database.PatchSnapshots.Add(snapshot);
            await database.SaveChangesAsync(cancellationToken);
            await MarkLatestAsync(snapshot, cancellationToken);
            return ToDto(snapshot, null);
        });
    }

    public async Task<PatchSnapshotDto> SetSnapshotAsLatestAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await database.PatchSnapshots.FirstOrDefaultAsync(item => item.Id == snapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("Snapshot not found.");
        await MarkLatestAsync(snapshot, cancellationToken);
        var patchId = await database.PatchPackages.Where(item => item.SnapshotId == snapshot.Id).Select(item => (int?)item.Id).FirstOrDefaultAsync(cancellationToken);
        return ToDto(snapshot, patchId);
    }

    public async Task DeleteSnapshotAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await database.PatchSnapshots.FirstOrDefaultAsync(item => item.Id == snapshotId, cancellationToken)
            ?? throw new KeyNotFoundException("Snapshot not found.");
        if (snapshot.IsLatest) throw new InvalidOperationException("This snapshot is the latest version. Set another snapshot as latest before deleting it.");
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
            var baseline = await ResolveBaseAsync(systemId, request.BaseSnapshotId, cancellationToken);
            var baseManifests = baseline is null ? [] : ReadManifest(baseline.ManifestGz);
            var current = await ScanAsync(system, baseManifests, cancellationToken);
            var (changes, summaries) = PatchDiff.Compare(baseManifests, current);
            if (changes.Count == 0) throw new InvalidOperationException("There are no changes to compile since the selected version.");

            var modulesByName = system.Modules.ToDictionary(module => module.Name, StringComparer.OrdinalIgnoreCase);
            var sources = changes
                .Where(change => change.Kind != PatchChangeKinds.Deleted)
                .Select(change => new ArchiveSource(PatchDiff.ArchiveEntry(change.Kind, change.Module, change.Path),
                    PackageArchiveService.ResolveInside(modulesByName[change.Module].SourcePath, change.Path)))
                .ToList();
            var textEntries = new Dictionary<string, string>
            {
                ["PATCH-NOTES.txt"] = PatchNotes(system, baseline, label, actor, changes, summaries)
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

            // The snapshot records exactly what this patch contains, so it can become the latest version once delivered.
            var snapshot = NewSnapshot(systemId, SourcePatch, label ?? $"Patch {Path.GetFileName(storedPath)}", actor, current);
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
                BaseSnapshotId = baseline?.Id,
                SnapshotId = snapshot.Id
            };
            database.PatchPackages.Add(package);
            await database.SaveChangesAsync(cancellationToken);
            return ToDto(package, baseline?.CreatedAtUtc, false);
        });
    }

    public async Task<IReadOnlyList<PatchPackageDto>> GetPackagesAsync(int systemId, CancellationToken cancellationToken)
    {
        var packages = await database.PatchPackages.AsNoTracking().Where(item => item.PatchSystemId == systemId).OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        var snapshots = await database.PatchSnapshots.AsNoTracking().Where(item => item.PatchSystemId == systemId)
            .Select(item => new { item.Id, item.CreatedAtUtc, item.IsLatest }).ToListAsync(cancellationToken);
        return packages.Select(package => ToDto(package,
            snapshots.FirstOrDefault(item => item.Id == package.BaseSnapshotId)?.CreatedAtUtc,
            snapshots.FirstOrDefault(item => item.Id == package.SnapshotId)?.IsLatest == true)).ToList();
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

    private async Task<PatchSystem> LoadSystemAsync(int systemId, CancellationToken cancellationToken)
    {
        var system = await database.PatchSystems.AsNoTracking().Include(item => item.Modules).FirstOrDefaultAsync(item => item.Id == systemId, cancellationToken)
            ?? throw new KeyNotFoundException("Patch system not found.");
        if (system.Modules.Count == 0) throw new InvalidOperationException("Add the system's modules first.");
        return system;
    }

    private async Task<PatchSnapshot?> ResolveBaseAsync(int systemId, int? snapshotId, CancellationToken cancellationToken) =>
        snapshotId is { } id
            ? await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.PatchSystemId == systemId, cancellationToken)
              ?? throw new KeyNotFoundException("The selected snapshot was not found.")
            : await database.PatchSnapshots.AsNoTracking().FirstOrDefaultAsync(item => item.PatchSystemId == systemId && item.IsLatest, cancellationToken);

    private async Task<List<PatchModuleManifest>> ScanAsync(PatchSystem system, IReadOnlyList<PatchModuleManifest> reuse, CancellationToken cancellationToken)
    {
        var known = reuse.GroupBy(module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyDictionary<string, PatchFileEntry>)group.SelectMany(module => module.Files)
                .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(file => file.Key, file => file.First(), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var result = new List<PatchModuleManifest>();
        foreach (var module in system.Modules.OrderBy(module => module.Name))
            result.Add(await scanner.ScanAsync(module.Id, module.Name, module.SourcePath, module.Exclusions, known.GetValueOrDefault(module.Name), cancellationToken));
        return result;
    }

    private async Task MarkLatestAsync(PatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        await database.PatchSnapshots.Where(item => item.PatchSystemId == snapshot.PatchSystemId && item.IsLatest && item.Id != snapshot.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLatest, false), cancellationToken);
        snapshot.IsLatest = true;
        snapshot.MarkedLatestAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    private static PatchChangesDto BuildChanges(PatchSnapshot? baseline, PatchSnapshot? target, IReadOnlyList<PatchModuleManifest> baseManifests, IReadOnlyList<PatchModuleManifest> current)
    {
        var (changes, summaries) = PatchDiff.Compare(baseManifests, current);
        return new PatchChangesDto(baseline?.Id, baseline is null ? null : Utc(baseline.CreatedAtUtc), baseline?.Label, target?.Id,
            target is null ? DateTimeOffset.UtcNow : Utc(target.CreatedAtUtc),
            changes.Count(change => change.Kind == PatchChangeKinds.Added),
            changes.Count(change => change.Kind == PatchChangeKinds.Changed),
            changes.Count(change => change.Kind == PatchChangeKinds.Deleted),
            summaries, changes.Take(MaxChangesReturned).ToList(), changes.Count > MaxChangesReturned);
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
        ManifestGz = WriteManifest(manifests)
    };

    private static string PatchNotes(PatchSystem system, PatchSnapshot? baseline, string? label, string? actor,
        IReadOnlyList<PatchFileChange> changes, IReadOnlyList<PatchModuleChangeSummary> summaries)
    {
        var text = new StringBuilder();
        text.AppendLine($"Patch for {system.Name}");
        if (!string.IsNullOrWhiteSpace(label)) text.AppendLine($"Description: {label}");
        text.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(string.IsNullOrWhiteSpace(actor) ? "" : $" by {actor}")}");
        text.AppendLine(baseline is null
            ? "Compared with: nothing (no latest version was set, so every file is ADDED)"
            : $"Compared with: {baseline.Label ?? "snapshot"} ({baseline.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
        text.AppendLine();
        text.AppendLine("How to apply (for each module folder in this patch):");
        text.AppendLine("  <module>/ADDED/...    copy these new files into the module folder.");
        text.AppendLine("  <module>/CHANGED/...  copy these over the existing files in the module folder.");
        text.AppendLine("  <module>/DELETED/DELETED-FILES.txt  delete the listed files from the module folder.");
        text.AppendLine();
        foreach (var summary in summaries.Where(item => item.Added + item.Changed + item.Deleted > 0))
        {
            text.AppendLine($"[{summary.Module}] {summary.Added} added, {summary.Changed} changed, {summary.Deleted} deleted");
            foreach (var change in changes.Where(item => item.Module.Equals(summary.Module, StringComparison.OrdinalIgnoreCase)))
                text.AppendLine($"  {change.Kind,-7} {change.Path}");
            text.AppendLine();
        }
        return text.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
    }

    private static byte[] WriteManifest(IReadOnlyList<PatchModuleManifest> manifests)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gzip, manifests, Json);
        return output.ToArray();
    }

    private static List<PatchModuleManifest> ReadManifest(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<PatchModuleManifest>>(input, Json) ?? [];
    }

    private static async Task<T> WithSystemLockAsync<T>(int systemId, Func<Task<T>> action)
    {
        var gate = SystemLocks.GetOrAdd(systemId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A snapshot or patch is already being created for this system. Try again when it finishes.");
        try { return await action(); }
        finally { gate.Release(); }
    }

    private PatchSystemDto ToDto(PatchSystem entity, PatchSnapshotDto? latest) =>
        new(entity.Id, entity.Name, entity.OutputDirectory, paths.PatchOutputDirectory(entity), entity.Modules.OrderBy(module => module.Name).Select(ToDto).ToList(), latest);

    private static PatchModuleDto ToDto(PatchModule entity) => new(entity.Id, entity.PatchSystemId, entity.Name, entity.SourcePath, entity.Exclusions);

    private static PatchSnapshotDto ToDto(PatchSnapshot entity, int? patchId) =>
        new(entity.Id, entity.PatchSystemId, Utc(entity.CreatedAtUtc), entity.Source, entity.Label, entity.CreatedBy, entity.IsLatest,
            entity.MarkedLatestAtUtc is null ? null : Utc(entity.MarkedLatestAtUtc.Value), entity.FileCount, entity.TotalBytes, patchId);

    private static PatchPackageDto ToDto(PatchPackage entity, DateTime? baseCreatedAtUtc, bool snapshotIsLatest) =>
        new(entity.Id, entity.PatchSystemId, Utc(entity.CreatedAtUtc), entity.Format, entity.FileName, entity.SizeBytes, entity.Label, entity.CreatedBy,
            entity.Added, entity.Changed, entity.Deleted, entity.BaseSnapshotId, baseCreatedAtUtc is null ? null : Utc(baseCreatedAtUtc.Value),
            entity.SnapshotId, snapshotIsLatest, File.Exists(entity.StoredPath));

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
