using DevTools.Shared.Helpers;
using DevTools.Web.Data.Entities;
using DevTools.Web.Models;
using Microsoft.Extensions.Options;

namespace DevTools.Web.Services;

public interface IDeploymentPaths
{
    string WorkingDirectory { get; }
    string StagingDirectory { get; }
    string BackupDirectory(DeploymentSystem system);
    string PackageDirectory(DeploymentSystem system);
    string Normalize(string path, string label);
    bool Overlaps(string first, string second);
    void EnsureSafeTargetFolder(string path);
    void EnsureSafeStorageFolder(string path, string label);
}

/// <summary>
/// Resolves deployment folders and refuses dangerous targets: drive roots, DevTools' own folder (a whole-package
/// deployment would delete the running application) and its working folders.
/// </summary>
public sealed class DeploymentPaths : IDeploymentPaths
{
    private readonly string _contentRoot;
    private readonly string _baseDirectory;

    public DeploymentPaths(IOptions<DeploymentOptions> options, IWebHostEnvironment environment)
    {
        _contentRoot = Full(environment.ContentRootPath);
        _baseDirectory = Full(AppContext.BaseDirectory);
        var configured = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(options.Value.WorkingDirectory) ? "deployments" : options.Value.WorkingDirectory.Trim());
        WorkingDirectory = Full(Path.IsPathRooted(configured) ? configured : Path.Combine(_contentRoot, configured));
    }

    public string WorkingDirectory { get; }
    public string StagingDirectory => Path.Combine(WorkingDirectory, "staging");

    /// <summary>Where uploaded packages are stored: the system's own folder, or the working folder by default.</summary>
    public string PackageDirectory(DeploymentSystem system) =>
        string.IsNullOrWhiteSpace(system.PackageDirectory)
            ? Path.Combine(WorkingDirectory, "packages", SafeFolderName(system.Name))
            : system.PackageDirectory;

    public string BackupDirectory(DeploymentSystem system) =>
        string.IsNullOrWhiteSpace(system.BackupDirectory)
            ? Path.Combine(WorkingDirectory, "backups", SafeFolderName(system.Name))
            : system.BackupDirectory;

    /// <summary>
    /// DevTools is Windows-only, so folders are read as Windows paths on every OS: <c>C:\Backups\MyApp</c> and
    /// <c>\\server\share</c> are accepted as typed. A relative path resolves under the DevTools content root.
    /// </summary>
    public string Normalize(string path, string label)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (WindowsPath.IsFullyQualified(expanded)) return WindowsPath.Normalize(expanded);
        try { return Full(Path.IsPathRooted(expanded) ? expanded : Path.Combine(_contentRoot, expanded)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            throw new ArgumentException($"{label} is not a valid path.");
        }
    }

    public bool Overlaps(string first, string second)
    {
        var (a, b) = (Canonical(first), Canonical(second));
        return WindowsPath.IsSameOrInside(a, b) || WindowsPath.IsSameOrInside(b, a);
    }

    public void EnsureSafeTargetFolder(string path)
    {
        if (IsRoot(path)) throw new ArgumentException("A module cannot deploy to the root of a drive.");
        if (Overlaps(path, _contentRoot) || Overlaps(path, _baseDirectory))
            throw new ArgumentException("A module cannot deploy into DevTools' own folder, or a folder that contains it.");
        if (Overlaps(path, WorkingDirectory))
            throw new ArgumentException("A module cannot use DevTools' deployment working folder.");
    }

    public void EnsureSafeStorageFolder(string path, string label)
    {
        if (IsRoot(path)) throw new ArgumentException($"Choose a folder for {label}, not the root of a drive.");
        var folder = Canonical(path);
        if (WindowsPath.IsSameOrInside(Canonical(_contentRoot), folder) || WindowsPath.IsSameOrInside(Canonical(_baseDirectory), folder))
            throw new ArgumentException($"The {label} folder cannot contain DevTools' own folder.");
    }

    /// <summary>One comparable form for Windows and local paths: '\' separators, compared case-insensitively.</summary>
    private static string Canonical(string path) =>
        (WindowsPath.IsFullyQualified(path) ? WindowsPath.Normalize(path) : Full(path)).Replace('/', '\\');

    private static bool IsRoot(string path)
    {
        if (WindowsPath.IsFullyQualified(path)) return WindowsPath.IsRoot(WindowsPath.Normalize(path));
        var full = Full(path);
        return Path.GetPathRoot(full) is { } root && Full(root).Equals(full, StringComparison.OrdinalIgnoreCase);
    }

    private static string Full(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        return string.IsNullOrEmpty(cleaned) ? "system" : cleaned;
    }
}
