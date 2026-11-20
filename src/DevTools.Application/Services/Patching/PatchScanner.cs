using System.Security.Cryptography;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;

namespace DevTools.Application.Services.Patching;

public interface IPatchScanner
{
    /// <summary>
    /// Lists every file under <paramref name="root"/> (minus exclusions) with its size, last write time and SHA-256.
    /// When a file's size and last write time match <paramref name="previous"/>, its known hash is reused instead of
    /// reading the file again, which keeps repeated scans fast.
    /// </summary>
    Task<PatchModuleManifest> ScanAsync(int moduleId, string moduleName, string root, string? exclusions,
        IReadOnlyDictionary<string, PatchFileEntry>? previous, CancellationToken cancellationToken);
}

public sealed class PatchScanner : IPatchScanner
{
    public async Task<PatchModuleManifest> ScanAsync(int moduleId, string moduleName, string root, string? exclusions,
        IReadOnlyDictionary<string, PatchFileEntry>? previous, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The folder for module {moduleName} doesn't exist: {root}");
        var matcher = DeploymentPathMatcher.Parse(exclusions);
        var files = new List<PatchFileEntry>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            if (matcher.IsExcluded(relative)) continue;
            var lastWrite = DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc);
            string hash;
            if (previous is not null && previous.TryGetValue(relative, out var known) && known.Size == info.Length && SameSecond(known.LastWriteUtc, lastWrite))
                hash = known.Sha256;
            else
                hash = await HashAsync(info.FullName, cancellationToken);
            files.Add(new PatchFileEntry(relative, info.Length, lastWrite, hash));
        }
        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
        return new PatchModuleManifest(moduleId, moduleName, files);
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool SameSecond(DateTime first, DateTime second) =>
        Math.Abs((DateTime.SpecifyKind(first, DateTimeKind.Utc) - second).TotalSeconds) < 1;
}
