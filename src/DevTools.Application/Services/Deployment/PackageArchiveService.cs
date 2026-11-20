using DevTools.Shared.Helpers;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;

namespace DevTools.Application.Services.Deployment;

/// <summary>A file to add to a backup archive: the entry name inside the archive and the source on disk.</summary>
public sealed record ArchiveSource(string EntryName, string SourcePath);

public interface IPackageArchiveService
{
    /// <summary>Lists entries without extracting them. Supports .zip and .7z.</summary>
    IReadOnlyList<PackageEntry> ListEntries(string archivePath, CancellationToken cancellationToken);

    /// <summary>
    /// Extracts entries sequentially (one pass, so solid .7z archives are not re-decompressed per entry).
    /// <paramref name="map"/> returns the destination path relative to <paramref name="destinationRoot"/>, or null to skip.
    /// Returns the number of files written.
    /// </summary>
    int Extract(string archivePath, string destinationRoot, Func<string, string?> map, Action<string>? onFile, CancellationToken cancellationToken);

    /// <summary>Writes a .7z archive (LZMA2) from files on disk plus optional in-memory text entries. Streams each file; nothing is buffered whole.</summary>
    void CreateSevenZip(string archivePath, IEnumerable<ArchiveSource> files, IReadOnlyDictionary<string, string>? textEntries, Action<string>? onFile, CancellationToken cancellationToken);

    /// <summary>Reads one small text entry (such as a manifest), or null when it is missing.</summary>
    string? ReadText(string archivePath, string entryName, CancellationToken cancellationToken);
}

public sealed class PackageArchiveService : IPackageArchiveService
{
    private static readonly string[] SupportedExtensions = [".zip", ".7z"];

    public static bool IsSupported(string fileName) =>
        SupportedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PackageEntry> ListEntries(string archivePath, CancellationToken cancellationToken)
    {
        using var archive = Open(archivePath);
        var entries = new List<PackageEntry>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsEncrypted) throw new InvalidOperationException("Password-protected packages are not supported.");
            if (!string.IsNullOrEmpty(entry.LinkTarget)) throw new InvalidOperationException($"The package contains a link, which is not allowed: '{entry.Key}'.");
            entries.Add(new PackageEntry(entry.Key ?? string.Empty, entry.Size, entry.IsDirectory));
        }
        return entries;
    }

    public int Extract(string archivePath, string destinationRoot, Func<string, string?> map, Action<string>? onFile, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        using var archive = Open(archivePath);
        var written = 0;

        // Solid archives (most .7z files) must be read in one sequential pass; reading entries one by one would
        // re-decompress the solid block for every file. Zip entries are independent, so they are read directly
        // (SharpCompress only allows the sequential reader for solid and 7z archives).
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is null) continue;
                if (WriteEntry(root, entry, map, output => reader.WriteEntryTo(output), onFile)) written++;
            }
            return written;
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.Key is null) continue;
            if (WriteEntry(root, entry, map, output => { using var input = entry.OpenEntryStream(); input.CopyTo(output, 1024 * 128); }, onFile)) written++;
        }
        return written;
    }

    private static bool WriteEntry(string root, IEntry entry, Func<string, string?> map, Action<Stream> copy, Action<string>? onFile)
    {
        var relative = map(entry.Key!);
        if (relative is null) return false;
        var destination = ResolveInside(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128))
            copy(output);
        if (entry.LastModifiedTime is { } modified) File.SetLastWriteTime(destination, modified);
        onFile?.Invoke(relative);
        return true;
    }

    public void CreateSevenZip(string archivePath, IEnumerable<ArchiveSource> files, IReadOnlyDictionary<string, string>? textEntries, Action<string>? onFile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);
        var temporary = archivePath + ".partial";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 128))
            using (var writer = new SevenZipWriter(stream, new SevenZipWriterOptions(CompressionType.LZMA2)))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 128))
                        writer.Write(file.EntryName, source, File.GetLastWriteTime(file.SourcePath));
                    onFile?.Invoke(file.EntryName);
                }
                foreach (var (name, content) in textEntries ?? new Dictionary<string, string>())
                {
                    using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                    writer.Write(name, source, DateTime.Now);
                }
            }
            File.Move(temporary, archivePath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string? ReadText(string archivePath, string entryName, CancellationToken cancellationToken)
    {
        using var archive = Open(archivePath);
        var entry = archive.Entries.FirstOrDefault(item => !item.IsDirectory && string.Equals(item.Key?.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var stream = entry.OpenEntryStream();
        using var text = new StreamReader(stream);
        return text.ReadToEnd();
    }

    /// <summary>Resolves a relative path under a root and refuses anything that would land outside it.</summary>
    public static string ResolveInside(string root, string relative)
    {
        var normalized = DeploymentPackageLayout.NormalizeEntryPath(relative) ?? throw new InvalidOperationException($"Unsafe path '{relative}'.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsafe path '{relative}'.");
        return destination;
    }

    private static IArchive Open(string archivePath)
    {
        if (!IsSupported(archivePath)) throw new InvalidOperationException("Only .zip and .7z packages are supported.");
        try { return ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { LookForHeader = false }); }
        catch (Exception exception) when (exception is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException("The file could not be read as a .zip or .7z archive.", exception);
        }
    }
}
