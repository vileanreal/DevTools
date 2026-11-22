using DevTools.Shared.Helpers;
using DevTools.Shared.Models;

namespace DevTools.Application.Services.Deployment;

/// <summary>One module to check: its main file (relative to the module folder), its folder in the package and its target folder.</summary>
public sealed record VersionCheckModule(int ModuleId, string ModuleName, string VersionFile, string PackageFolder, string TargetPath);

public interface IDeploymentVersionChecker
{
    /// <summary>Reads each module's main-file version from the package and from the server, and applies the system's rule.</summary>
    IReadOnlyList<DeploymentVersionCheck> Check(string archivePath, string? wrapperFolder, IReadOnlyList<VersionCheckModule> modules, bool requireChange, CancellationToken cancellationToken);
}

public sealed class DeploymentVersionChecker(IPackageArchiveService archives) : IDeploymentVersionChecker
{
    public IReadOnlyList<DeploymentVersionCheck> Check(string archivePath, string? wrapperFolder, IReadOnlyList<VersionCheckModule> modules, bool requireChange, CancellationToken cancellationToken)
    {
        if (modules.Count == 0) return [];
        var temporary = Path.Combine(Path.GetTempPath(), "devtools-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            // Only the main files are read out of the archive (one pass), into a temporary folder, to read their versions.
            var wanted = modules.ToDictionary(module => Key(module.PackageFolder, module.VersionFile), module => module, StringComparer.OrdinalIgnoreCase);
            var extracted = new Dictionary<int, string>();
            archives.ReadEntries(archivePath,
                entry => DeploymentPackageLayout.Locate(entry, wrapperFolder) is { } located && wanted.ContainsKey(Key(located.Folder, located.RelativePath)),
                (entry, stream) =>
                {
                    var located = DeploymentPackageLayout.Locate(entry, wrapperFolder)!.Value;
                    var module = wanted[Key(located.Folder, located.RelativePath)];
                    var path = Path.Combine(temporary, $"{module.ModuleId}{Path.GetExtension(module.VersionFile)}");
                    using (var output = File.Create(path)) stream.CopyTo(output);
                    extracted[module.ModuleId] = path;
                }, cancellationToken);

            return modules.Select(module =>
            {
                var inPackage = extracted.TryGetValue(module.ModuleId, out var packageCopy);
                var packageVersion = inPackage ? FileVersionReader.Read(packageCopy!) : null;
                var serverPath = PackageArchiveService.ResolveInside(module.TargetPath, module.VersionFile);
                var onServer = File.Exists(serverPath);
                var serverVersion = onServer ? FileVersionReader.Read(serverPath) : null;
                var (state, blocking, message) = DeploymentVersionRules.Evaluate(module.VersionFile, inPackage, packageVersion, onServer, serverVersion, requireChange);
                return new DeploymentVersionCheck(module.ModuleId, module.ModuleName, module.VersionFile, packageVersion, serverVersion, state, blocking, message);
            }).ToList();
        }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Key(string folder, string path) => $"{folder}/{path.Replace('\\', '/').TrimStart('/')}";
}
