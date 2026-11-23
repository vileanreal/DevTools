using System.Text.Json;
using DevTools.Application.Services.Deployment;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;
using DevTools.Web.Data;
using DevTools.Web.Data.Entities;
using DevTools.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevTools.Web.Services;

public interface IDeploymentService
{
    Task<IReadOnlyList<DeploymentSystemDto>> GetSystemsAsync(CancellationToken cancellationToken);
    Task<DeploymentSystemDto> SaveSystemAsync(int? id, SaveDeploymentSystemRequest request, CancellationToken cancellationToken);
    Task DeleteSystemAsync(int id, CancellationToken cancellationToken);
    Task<DeploymentModuleDto> SaveModuleAsync(int systemId, int? id, SaveDeploymentModuleRequest request, CancellationToken cancellationToken);
    Task DeleteModuleAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeploymentPackageDto>> GetPackagesAsync(int systemId, CancellationToken cancellationToken);
    Task<DeploymentPackageDto> SavePackageAsync(int systemId, string fileName, string? description, Stream content, string? actor, CancellationToken cancellationToken);
    Task DeletePackageAsync(int id, CancellationToken cancellationToken);
    Task<DeploymentFilePreview> PreviewFilesAsync(int packageId, DeploymentFilePreviewRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeploymentVersionCheck>> CheckVersionsAsync(int packageId, CancellationToken cancellationToken);
    Task<DeploymentPackageContents> GetContentsAsync(int packageId, CancellationToken cancellationToken);
}

/// <summary>Deployment configuration, package uploads and package history. Running deployments live in <see cref="DeploymentJobService"/>.</summary>
public sealed class DeploymentService(
    DevToolsDbContext database,
    IPackageArchiveService archives,
    IDeploymentPaths paths,
    IDeploymentJobRegistry jobs,
    IDeploymentVersionChecker versions,
    IOptions<DeploymentOptions> options) : IDeploymentService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DeploymentSystemDto>> GetSystemsAsync(CancellationToken cancellationToken)
    {
        var systems = await database.DeploymentSystems.AsNoTracking().Include(item => item.Modules).OrderBy(item => item.Name).ToListAsync(cancellationToken);
        return systems.Select(ToDto).ToList();
    }

    public async Task<DeploymentSystemDto> SaveSystemAsync(int? id, SaveDeploymentSystemRequest request, CancellationToken cancellationToken)
    {
        var name = Required(request.Name, "System name", 120);
        var backup = string.IsNullOrWhiteSpace(request.BackupDirectory) ? null : paths.Normalize(request.BackupDirectory, "Backup folder");
        if (backup is not null) paths.EnsureSafeStorageFolder(backup, "backup");
        var packageFolder = string.IsNullOrWhiteSpace(request.PackageDirectory) ? null : paths.Normalize(request.PackageDirectory, "Package folder");
        if (packageFolder is not null) paths.EnsureSafeStorageFolder(packageFolder, "package");
        if (!string.IsNullOrWhiteSpace(request.BackupFormat) && !DeploymentBackupFormats.IsValid(request.BackupFormat.Trim().ToLowerInvariant()))
            throw new ArgumentException("Choose a folder, .zip or .7z for the backup.");
        var backupFormat = DeploymentBackupFormats.Normalize(request.BackupFormat);
        if (await database.DeploymentSystems.AnyAsync(item => item.Id != (id ?? 0) && item.Name.ToLower() == name.ToLower(), cancellationToken))
            throw new ArgumentException("A deployment system with this name already exists.");
        DeploymentSystem entity;
        if (id is null)
        {
            entity = new DeploymentSystem { Name = name, BackupDirectory = backup, PackageDirectory = packageFolder, RequireVersionChange = request.RequireVersionChange, BackupFormat = backupFormat };
            database.DeploymentSystems.Add(entity);
        }
        else
        {
            entity = await database.DeploymentSystems.Include(item => item.Modules).FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("Deployment system not found.");
            entity.Name = name;
            entity.BackupDirectory = backup;
            entity.PackageDirectory = packageFolder;
            entity.RequireVersionChange = request.RequireVersionChange;
            entity.BackupFormat = backupFormat;
            // Backups and packages must never sit inside a module folder: a whole-package deployment would delete them.
            EnsureStorageOutsideModules(entity, entity.Modules);
        }
        await database.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task DeleteSystemAsync(int id, CancellationToken cancellationToken)
    {
        EnsureIdle(id);
        var entity = await database.DeploymentSystems.Include(item => item.Packages).FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Deployment system not found.");
        foreach (var package in entity.Packages) TryDelete(package.StoredPath);
        database.DeploymentSystems.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeploymentModuleDto> SaveModuleAsync(int systemId, int? id, SaveDeploymentModuleRequest request, CancellationToken cancellationToken)
    {
        EnsureIdle(systemId);
        var name = Required(request.Name, "Module name", 120);
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or "..")
            throw new ArgumentException("Module name must be a valid folder name, because it is matched against the folders in the package.");
        var target = paths.Normalize(Required(request.TargetPath, "Target folder", 2048), "Target folder");
        paths.EnsureSafeTargetFolder(target);
        var exclusionsError = DeploymentPathMatcher.Validate(request.DefaultExclusions);
        if (exclusionsError is not null) throw new ArgumentException(exclusionsError);
        var appPool = Optional(request.AppPoolName, "App pool name", 260);
        var service = Optional(request.ServiceName, "Windows service name", 260);
        var versionFile = DeploymentVersionFile.Normalize(request.VersionFile);

        var system = await database.DeploymentSystems.AsNoTracking().FirstOrDefaultAsync(item => item.Id == systemId, cancellationToken)
            ?? throw new KeyNotFoundException("Deployment system not found.");
        EnsureStorageOutsideModules(system, [new DeploymentModule { Name = name, TargetPath = target }]);
        var siblings = await database.DeploymentModules.Where(item => item.DeploymentSystemId == systemId && item.Id != (id ?? 0)).ToListAsync(cancellationToken);
        if (siblings.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("This system already has a module with that name.");
        if (siblings.Any(item => paths.Overlaps(item.TargetPath, target))) throw new ArgumentException("Another module in this system already uses this folder or a folder inside it.");

        DeploymentModule entity;
        if (id is null)
        {
            entity = new DeploymentModule { DeploymentSystemId = systemId, Name = name, TargetPath = target };
            database.DeploymentModules.Add(entity);
        }
        else
        {
            entity = await database.DeploymentModules.FirstOrDefaultAsync(item => item.Id == id && item.DeploymentSystemId == systemId, cancellationToken)
                ?? throw new KeyNotFoundException("Module not found.");
        }
        entity.Name = name;
        entity.TargetPath = target;
        entity.AppPoolName = appPool;
        entity.ServiceName = service;
        entity.DefaultExclusions = DeploymentPathMatcher.Normalize(request.DefaultExclusions);
        entity.VersionFile = versionFile;
        await database.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task DeleteModuleAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await database.DeploymentModules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) ?? throw new KeyNotFoundException("Module not found.");
        EnsureIdle(entity.DeploymentSystemId);
        database.DeploymentModules.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeploymentPackageDto>> GetPackagesAsync(int systemId, CancellationToken cancellationToken)
    {
        var packages = await database.DeploymentPackages.AsNoTracking().Include(item => item.Events).Where(item => item.DeploymentSystemId == systemId).ToListAsync(cancellationToken);
        var rollback = RollbackAvailability(packages);
        var busy = jobs.IsBusy(systemId);
        return packages.OrderByDescending(item => item.UploadedAtUtc)
            .Select(item => ToDto(item, rollback.TryGetValue(item.Id, out var state) && state.CanRollback && !busy, rollback.GetValueOrDefault(item.Id)?.Reason, rollback.GetValueOrDefault(item.Id)?.Warning, busy))
            .ToList();
    }

    public const int MaxDescriptionLength = 500;

    public async Task<DeploymentPackageDto> SavePackageAsync(int systemId, string fileName, string? description, Stream content, string? actor, CancellationToken cancellationToken)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Enter a package description before uploading.");
        if (text.Length > MaxDescriptionLength) throw new ArgumentException($"The package description must be {MaxDescriptionLength} characters or fewer.");
        var safeName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeName) || safeName.Length > 260) throw new ArgumentException("The uploaded file needs a valid name.");
        if (!PackageArchiveService.IsSupported(safeName)) throw new ArgumentException("Upload a .zip or .7z package.");
        var system = await database.DeploymentSystems.AsNoTracking().Include(item => item.Modules).FirstOrDefaultAsync(item => item.Id == systemId, cancellationToken)
            ?? throw new KeyNotFoundException("Deployment system not found.");
        if (system.Modules.Count == 0) throw new ArgumentException("Add the system's modules before uploading a package.");

        var directory = paths.PackageDirectory(system);
        Directory.CreateDirectory(directory);
        var storedPath = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(safeName).ToLowerInvariant()}");
        var partial = storedPath + ".partial";
        long size;
        try
        {
            // Stream straight to disk in chunks; the upload is never held in memory.
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 256, FileOptions.Asynchronous))
            {
                var buffer = new byte[1024 * 256];
                var limit = options.Value.MaxUploadBytes;
                size = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > limit) throw new ArgumentException($"The package is larger than the {options.Value.MaxUploadMegabytes:N0} MB limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (size == 0) throw new ArgumentException("The uploaded file is empty.");
            File.Move(partial, storedPath);

            var entries = await Task.Run(() => archives.ListEntries(storedPath, cancellationToken), cancellationToken);
            var analysis = DeploymentPackageLayout.Analyze(entries, system.Modules.Select(module => (module.Id, module.Name)));
            if (analysis.TotalUncompressedBytes > options.Value.MaxExtractedBytes)
                throw new ArgumentException($"The package expands to more than the {options.Value.MaxExtractedMegabytes:N0} MB limit.");
            if (analysis.Modules.Count == 0)
            {
                var found = analysis.UnmatchedFolders.Count == 0 ? "no folders" : string.Join(", ", analysis.UnmatchedFolders.Take(10));
                throw new ArgumentException($"No folder in the package matches a module name. Expected: {string.Join(", ", system.Modules.Select(module => module.Name))}. Found: {found}.");
            }

            var entity = new DeploymentPackage
            {
                DeploymentSystemId = systemId,
                FileName = safeName,
                Description = text,
                StoredPath = storedPath,
                SizeBytes = size,
                UploadedAtUtc = DateTime.UtcNow,
                Status = DeploymentPackageStatuses.Uploaded,
                AnalysisJson = JsonSerializer.Serialize(analysis, Json)
            };
            var uploadLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(actor)) uploadLines.Add($"By: {actor}");
            uploadLines.Add($"File: {safeName} ({FormatBytes(size)}, {FormatBytes(analysis.TotalUncompressedBytes)} unpacked)");
            uploadLines.AddRange(analysis.Modules.Select(module => $"{module.ModuleName}: {module.FileCount:N0} file(s), {FormatBytes(module.TotalBytes)}"));
            entity.Events.Add(new DeploymentEvent { Kind = DeploymentEventKinds.Uploaded, AtUtc = entity.UploadedAtUtc, Detail = string.Join('\n', uploadLines) });
            database.DeploymentPackages.Add(entity);
            await database.SaveChangesAsync(cancellationToken);
            return ToDto(entity, canRollback: false, rollbackBlockedReason: null, rollbackWarning: null, busy: jobs.IsBusy(systemId));
        }
        catch
        {
            TryDelete(partial);
            TryDelete(storedPath);
            throw;
        }
    }

    public async Task DeletePackageAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await database.DeploymentPackages.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) ?? throw new KeyNotFoundException("Package not found.");
        EnsureIdle(entity.DeploymentSystemId);
        if (entity.Status == DeploymentPackageStatuses.Deployed)
            throw new InvalidOperationException("This package is deployed. Roll it back before deleting it.");
        TryDelete(entity.StoredPath);
        // Backups are kept on disk; only the uploaded package and its history entry are removed.
        database.DeploymentPackages.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
    }

    public const int MaxPreviewFiles = 5000;

    /// <summary>Lists the files in each matched module folder of a package (read from the archive, nothing extracted).</summary>
    public async Task<DeploymentPackageContents> GetContentsAsync(int packageId, CancellationToken cancellationToken)
    {
        var package = await database.DeploymentPackages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken)
            ?? throw new KeyNotFoundException("Package not found.");
        if (!File.Exists(package.StoredPath)) throw new InvalidOperationException("The uploaded package file is missing.");
        var analysis = JsonSerializer.Deserialize<DeploymentPackageAnalysis>(package.AnalysisJson, Json)!;
        return await Task.Run(() =>
        {
            var files = analysis.Modules.ToDictionary(module => module.FolderName, _ => new List<DeploymentPackageFile>(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archives.ListEntries(package.StoredPath, cancellationToken))
            {
                if (entry.IsDirectory) continue;
                if (DeploymentPackageLayout.Locate(entry.Path, analysis.WrapperFolder) is { } located && files.TryGetValue(located.Folder, out var list))
                    list.Add(new DeploymentPackageFile(located.RelativePath, entry.Size));
            }
            return new DeploymentPackageContents(package.Id, analysis.Modules.Select(module =>
            {
                var list = files[module.FolderName].OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return new DeploymentPackageModuleContents(module.FolderName, module.ModuleName, list.Take(MaxPreviewFiles).ToList(), list.Count > MaxPreviewFiles);
            }).ToList());
        }, cancellationToken);
    }

    /// <summary>
    /// Lists what deploying one module would do, without changing anything: package files that are new, replace an
    /// existing file, or are excluded; and for a whole-package update, existing files that would be deleted or kept.
    /// </summary>
    /// <summary>Each module's main-file version in the package and on the server, for modules that have a main file set.</summary>
    public async Task<IReadOnlyList<DeploymentVersionCheck>> CheckVersionsAsync(int packageId, CancellationToken cancellationToken)
    {
        var package = await database.DeploymentPackages.AsNoTracking().Include(item => item.DeploymentSystem!).ThenInclude(item => item.Modules)
            .FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken) ?? throw new KeyNotFoundException("Package not found.");
        if (!File.Exists(package.StoredPath)) throw new InvalidOperationException("The uploaded package file is missing. Upload it again.");
        var analysis = JsonSerializer.Deserialize<DeploymentPackageAnalysis>(package.AnalysisJson, Json)!;
        var modules = DeploymentVersionFile.Modules(package.DeploymentSystem!.Modules, analysis);
        return await Task.Run(() => versions.Check(package.StoredPath, analysis.WrapperFolder, modules, package.DeploymentSystem.RequireVersionChange, cancellationToken), cancellationToken);
    }

    public async Task<DeploymentFilePreview> PreviewFilesAsync(int packageId, DeploymentFilePreviewRequest request, CancellationToken cancellationToken)
    {
        var package = await database.DeploymentPackages.AsNoTracking().Include(item => item.DeploymentSystem!).ThenInclude(item => item.Modules)
            .FirstOrDefaultAsync(item => item.Id == packageId, cancellationToken) ?? throw new KeyNotFoundException("Package not found.");
        var module = package.DeploymentSystem!.Modules.FirstOrDefault(item => item.Id == request.ModuleId) ?? throw new KeyNotFoundException("Module not found.");
        if (request.Mode is not null && !DeploymentModes.IsValid(request.Mode)) throw new ArgumentException("Unknown update type.");
        var exclusionsError = DeploymentPathMatcher.Validate(request.Exclusions);
        if (exclusionsError is not null) throw new ArgumentException(exclusionsError);
        if (!File.Exists(package.StoredPath)) throw new InvalidOperationException("The uploaded package file is missing. Upload it again.");
        var analysis = JsonSerializer.Deserialize<DeploymentPackageAnalysis>(package.AnalysisJson, Json)!;
        var folder = analysis.Modules.FirstOrDefault(item => item.ModuleId == module.Id)?.FolderName ?? throw new ArgumentException($"The package has no '{module.Name}' folder.");
        var exclusions = DeploymentPathMatcher.Parse(request.Exclusions);

        return await Task.Run(() =>
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var packageFiles = new Dictionary<string, long>(comparer);
            foreach (var entry in archives.ListEntries(package.StoredPath, cancellationToken))
            {
                if (entry.IsDirectory) continue;
                var located = DeploymentPackageLayout.Locate(entry.Path, analysis.WrapperFolder);
                if (located is { } value && value.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) packageFiles[value.RelativePath] = entry.Size;
            }
            var targetFiles = new Dictionary<string, long>(comparer);
            if (Directory.Exists(module.TargetPath))
                foreach (var file in new DirectoryInfo(module.TargetPath).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    targetFiles[Path.GetRelativePath(module.TargetPath, file.FullName).Replace('\\', '/')] = file.Length;

            var changes = new List<DeploymentFileChange>();
            foreach (var (path, size) in packageFiles)
                changes.Add(new DeploymentFileChange(path, exclusions.IsExcluded(path) ? "excluded" : targetFiles.ContainsKey(path) ? "replace" : "new", size));
            if (request.Mode == DeploymentModes.Full)
                // Existing files missing from the package: deleted, unless excluded (then kept as they are).
                foreach (var (path, size) in targetFiles.Where(item => !packageFiles.ContainsKey(item.Key)))
                    changes.Add(new DeploymentFileChange(path, exclusions.IsExcluded(path) ? "keep" : "delete", size));
            int Count(string action) => changes.Count(change => change.Action == action);
            var ordered = changes.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return new DeploymentFilePreview(module.Id, request.Mode, Count("new"), Count("replace"), Count("excluded"), Count("delete"), Count("keep"),
                ordered.Count > MaxPreviewFiles, ordered.Take(MaxPreviewFiles).ToList());
        }, cancellationToken);
    }

    /// <summary>
    /// Which deployments can be rolled back: any deployment whose changes are still in place (deployed, failed without
    /// restore, or interrupted) while its backup exists, with a warning when newer deployments changed the same modules.
    /// See <see cref="DeploymentRollbackRules"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, RollbackAvailability> RollbackAvailability(IEnumerable<DeploymentPackage> packages) =>
        DeploymentRollbackRules.Evaluate(packages.Select(item =>
        {
            var manifest = string.IsNullOrWhiteSpace(item.ManifestJson) ? null : TryReadManifest(item.ManifestJson);
            var inPlace = item.DeployedAtUtc is not null
                && item.Status is DeploymentPackageStatuses.Deployed or DeploymentPackageStatuses.Failed or DeploymentPackageStatuses.Interrupted;
            var label = string.IsNullOrWhiteSpace(item.Description) ? item.FileName : $"{item.FileName} — {item.Description}";
            return new RollbackDeployment(item.Id, label, item.DeployedAtUtc ?? item.UploadedAtUtc, inPlace,
                DeploymentBackupFormats.Exists(item.BackupPath),
                manifest?.Modules.Select(module => module.ModuleId).ToList(),
                manifest?.Modules.GroupBy(module => module.ModuleId).ToDictionary(group => group.Key, group => group.First().ModuleName) ?? new Dictionary<int, string>());
        }));

    private static DeploymentManifest? TryReadManifest(string json)
    {
        try { return JsonSerializer.Deserialize<DeploymentManifest>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Marks deployments that were running when the application stopped, and records it in their history.</summary>
    public static void MarkInterrupted(DevToolsDbContext database)
    {
        var running = database.DeploymentPackages
            .Where(item => item.Status == DeploymentPackageStatuses.Deploying || item.Status == DeploymentPackageStatuses.RollingBack)
            .ToList();
        if (running.Count == 0) return;
        foreach (var package in running)
        {
            var wasRollback = package.Status == DeploymentPackageStatuses.RollingBack;
            package.Status = DeploymentPackageStatuses.Interrupted;
            package.Error = "DevTools stopped while this job was running. Check the module folders, then roll back if needed.";
            database.DeploymentEvents.Add(new DeploymentEvent
            {
                DeploymentPackageId = package.Id,
                Kind = DeploymentEventKinds.Interrupted,
                AtUtc = DateTime.UtcNow,
                Detail = wasRollback ? "Rollback interrupted by an application restart." : "Deployment interrupted by an application restart."
            });
        }
        database.SaveChanges();
    }

    private DeploymentSystemDto ToDto(DeploymentSystem entity) =>
        new(entity.Id, entity.Name, entity.BackupDirectory, paths.BackupDirectory(entity), entity.PackageDirectory, paths.PackageDirectory(entity),
            entity.Modules.OrderBy(module => module.Name).Select(ToDto).ToList(), entity.RequireVersionChange, DeploymentBackupFormats.Normalize(entity.BackupFormat));

    private static DeploymentModuleDto ToDto(DeploymentModule entity) =>
        new(entity.Id, entity.DeploymentSystemId, entity.Name, entity.TargetPath, entity.AppPoolName, entity.ServiceName, entity.DefaultExclusions, entity.VersionFile);

    private static DeploymentPackageDto ToDto(DeploymentPackage entity, bool canRollback, string? rollbackBlockedReason, string? rollbackWarning, bool busy)
    {
        var analysis = JsonSerializer.Deserialize<DeploymentPackageAnalysis>(entity.AnalysisJson, Json) ?? new DeploymentPackageAnalysis(null, [], [], 0, 0);
        // A deployed package is live: its only action is Rollback.
        var canDeploy = !busy && File.Exists(entity.StoredPath) && analysis.Modules.Count > 0
            && entity.Status is not DeploymentPackageStatuses.Deploying and not DeploymentPackageStatuses.RollingBack and not DeploymentPackageStatuses.Deployed;
        // Packages uploaded before history existed get their upload date from the package row.
        var events = entity.Events.OrderBy(item => item.AtUtc).ThenBy(item => item.Id)
            .Select(item => new DeploymentEventDto(item.Kind, Utc(item.AtUtc), item.Detail)).ToList();
        if (!events.Any(item => item.Kind == DeploymentEventKinds.Uploaded))
            events.Insert(0, new DeploymentEventDto(DeploymentEventKinds.Uploaded, Utc(entity.UploadedAtUtc), null));
        return new DeploymentPackageDto(entity.Id, entity.DeploymentSystemId, entity.FileName, entity.Description, entity.SizeBytes,
            new DateTimeOffset(DateTime.SpecifyKind(entity.UploadedAtUtc, DateTimeKind.Utc)), entity.Status, analysis,
            entity.BackupPath is null ? null : Path.GetFileName(entity.BackupPath),
            entity.DeployedAtUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(entity.DeployedAtUtc.Value, DateTimeKind.Utc)),
            entity.RolledBackAtUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(entity.RolledBackAtUtc.Value, DateTimeKind.Utc)),
            entity.Error, canDeploy, canRollback, rollbackBlockedReason, rollbackWarning, events);
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{value:N0} B" : $"{size:0.#} {units[unit]}";
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private void EnsureStorageOutsideModules(DeploymentSystem system, IEnumerable<DeploymentModule> modules)
    {
        foreach (var module in modules)
        {
            if (paths.Overlaps(module.TargetPath, paths.BackupDirectory(system)))
                throw new ArgumentException($"The backup folder and {module.Name}'s folder overlap. Keep backups outside every module folder.");
            if (paths.Overlaps(module.TargetPath, paths.PackageDirectory(system)))
                throw new ArgumentException($"The package folder and {module.Name}'s folder overlap. Keep packages outside every module folder.");
        }
    }

    private void EnsureIdle(int systemId)
    {
        if (jobs.IsBusy(systemId)) throw new InvalidOperationException("A deployment or rollback is running for this system. Wait for it to finish.");
    }

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
        if (trimmed.IndexOfAny(['"', '\0', '\r', '\n']) >= 0) throw new ArgumentException($"{label} contains characters that are not allowed.");
        return trimmed;
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Validation of a module's main-file path, and which modules of a package can be version-checked.</summary>
public static class DeploymentVersionFile
{
    public static string? Normalize(string? value)
    {
        var text = value?.Trim().Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > 500) throw new ArgumentException("The main file path must be 500 characters or fewer.");
        if (text.Contains(':') || text.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Enter the main file as a path inside the module folder, without a drive or “..”.");
        if (!text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The main file must be an .exe or .dll, because its file version is read.");
        return text;
    }

    public static IReadOnlyList<VersionCheckModule> Modules(IEnumerable<DeploymentModule> modules, DeploymentPackageAnalysis analysis, IReadOnlyCollection<int>? only = null) =>
        modules.Where(module => !string.IsNullOrWhiteSpace(module.VersionFile) && (only is null || only.Contains(module.Id)))
            .Select(module => (module, folder: analysis.Modules.FirstOrDefault(item => item.ModuleId == module.Id)))
            .Where(item => item.folder is not null)
            .Select(item => new VersionCheckModule(item.module.Id, item.module.Name, item.module.VersionFile!, item.folder!.FolderName, item.module.TargetPath))
            .OrderBy(item => item.ModuleName, StringComparer.OrdinalIgnoreCase).ToList();
}
