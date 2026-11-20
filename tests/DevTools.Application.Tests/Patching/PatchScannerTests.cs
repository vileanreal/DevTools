using DevTools.Application.Services.Deployment;
using DevTools.Application.Services.Patching;
using DevTools.Shared.Models;

namespace DevTools.Application.Tests.Patching;

public sealed class PatchScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devtools-patch-tests", Guid.NewGuid().ToString("N"));
    private readonly PatchScanner _scanner = new();

    public PatchScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ListsFilesWithSha256_AndSkipsExclusions()
    {
        Write("bin/app.dll", "hello");
        Write("web.config", "server");
        Write("logs/today.log", "log");

        var manifest = await _scanner.ScanAsync(3, "WebApi", _root, "web.config\nlogs/", null, CancellationToken.None);

        var file = Assert.Single(manifest.Files);
        Assert.Equal(("bin/app.dll", 5L), (file.Path, file.Size));
        Assert.Equal("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", file.Sha256);
        Assert.Equal((3, "WebApi"), (manifest.ModuleId, manifest.ModuleName));
    }

    [Fact]
    public async Task ReusesKnownHash_WhenSizeAndTimeMatch()
    {
        var path = Write("app.dll", "hello");
        var info = new FileInfo(path);
        var known = new Dictionary<string, PatchFileEntry> { ["app.dll"] = new("app.dll", info.Length, info.LastWriteTimeUtc, "KNOWN-HASH") };

        var manifest = await _scanner.ScanAsync(1, "M", _root, null, known, CancellationToken.None);

        Assert.Equal("KNOWN-HASH", manifest.Files.Single().Sha256);
    }

    [Fact]
    public async Task RehashesFile_WhenItsTimeChanged()
    {
        var path = Write("app.dll", "hello");
        var known = new Dictionary<string, PatchFileEntry> { ["app.dll"] = new("app.dll", 5, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), "STALE") };

        var manifest = await _scanner.ScanAsync(1, "M", _root, null, known, CancellationToken.None);

        Assert.NotEqual("STALE", manifest.Files.Single().Sha256);
    }

    [Fact]
    public async Task MissingFolder_Throws() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _scanner.ScanAsync(1, "M", Path.Combine(_root, "missing"), null, null, CancellationToken.None));

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public void CreateArchive_WritesFilesAndTextEntries(string format)
    {
        var source = Write("bin/app.dll", new string('x', 50_000));
        var archivePath = Path.Combine(_root, $"patch.{format}");
        var archives = new PackageArchiveService();

        archives.CreateArchive(archivePath, format, [new ArchiveSource("ADDED/WebApi/bin/app.dll", source)],
            new Dictionary<string, string> { ["DELETED/WebApi/DELETED-FILES.txt"] = "old.dll\r\n" }, null, CancellationToken.None);

        var entries = archives.ListEntries(archivePath, CancellationToken.None).Where(entry => !entry.IsDirectory).ToDictionary(entry => entry.Path.Replace('\\', '/'), entry => entry.Size);
        Assert.Equal(50_000, entries["ADDED/WebApi/bin/app.dll"]);
        Assert.Equal("old.dll\r\n", archives.ReadText(archivePath, "DELETED/WebApi/DELETED-FILES.txt", CancellationToken.None));
    }
}
