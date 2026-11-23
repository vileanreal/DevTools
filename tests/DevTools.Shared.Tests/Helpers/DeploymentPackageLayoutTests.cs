using DevTools.Shared.Helpers;
using DevTools.Shared.Models;

namespace DevTools.Shared.Tests.Helpers;

public sealed class DeploymentPackageLayoutTests
{
    private static readonly (int Id, string Name)[] Modules = [(1, "WebApi"), (2, "Worker")];

    private static PackageEntry File(string path, long size = 10) => new(path, size, false);

    [Fact]
    public void Analyze_MatchesTopLevelFoldersToModulesIgnoringCase()
    {
        var analysis = DeploymentPackageLayout.Analyze(
            [File("WebApi/app.dll", 100), File("webapi/web.config", 5), File("Worker/run.exe", 50), new PackageEntry("Worker/", 0, true)],
            Modules);

        Assert.Null(analysis.WrapperFolder);
        Assert.Collection(analysis.Modules,
            module => { Assert.Equal(1, module.ModuleId); Assert.Equal(2, module.FileCount); Assert.Equal(105, module.TotalBytes); },
            module => { Assert.Equal(2, module.ModuleId); Assert.Equal(1, module.FileCount); });
        Assert.Empty(analysis.UnmatchedFolders);
        Assert.Equal(155, analysis.TotalUncompressedBytes);
    }

    [Fact]
    public void Analyze_ReportsUnmatchedFoldersAndRootFiles()
    {
        var analysis = DeploymentPackageLayout.Analyze([File("WebApi/a.dll"), File("Docs/readme.md"), File("release-notes.txt")], Modules);

        Assert.Single(analysis.Modules);
        Assert.Equal(["Docs"], analysis.UnmatchedFolders);
        Assert.Equal(1, analysis.IgnoredRootFiles);
    }

    [Fact]
    public void Analyze_SkipsASingleWrapperFolder()
    {
        var analysis = DeploymentPackageLayout.Analyze([File("release-1.4/WebApi/a.dll"), File("release-1.4/Worker/b.exe")], Modules);

        Assert.Equal("release-1.4", analysis.WrapperFolder);
        Assert.Equal(2, analysis.Modules.Count);
    }

    [Fact]
    public void Analyze_DoesNotTreatAModuleFolderAsWrapper()
    {
        var analysis = DeploymentPackageLayout.Analyze([File("WebApi/Worker/a.dll"), File("WebApi/bin/b.dll")], Modules);

        Assert.Null(analysis.WrapperFolder);
        Assert.Equal("WebApi", Assert.Single(analysis.Modules).ModuleName);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("WebApi/../../evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    public void Analyze_RejectsUnsafeEntryPaths(string path) =>
        Assert.Throws<InvalidOperationException>(() => DeploymentPackageLayout.Analyze([File(path)], Modules));

    [Theory]
    [InlineData("WebApi/bin/app.dll", null, "WebApi", "bin/app.dll")]
    [InlineData(@"WebApi\bin\app.dll", null, "WebApi", "bin/app.dll")]
    [InlineData("release/WebApi/app.dll", "release", "WebApi", "app.dll")]
    public void Locate_SplitsModuleFolderAndRelativePath(string entry, string? wrapper, string folder, string relative) =>
        Assert.Equal((folder, relative), DeploymentPackageLayout.Locate(entry, wrapper));

    [Theory]
    [InlineData("readme.txt", null)]
    [InlineData("WebApi/", null)]
    [InlineData("other/WebApi/app.dll", "release")]
    public void Locate_ReturnsNullOutsideModuleFolders(string entry, string? wrapper) =>
        Assert.Null(DeploymentPackageLayout.Locate(entry, wrapper));

    [Fact]
    public void BackupFileName_UsesMmddyyyyAndAddsCounterForSameDay()
    {
        var date = new DateTime(2026, 11, 20, 14, 30, 0);
        var existing = new HashSet<string> { "11202026.7z", "11202026-2.7z" };

        Assert.Equal("11202026.7z", DeploymentPackageLayout.BackupFileName(date, DeploymentBackupFormats.SevenZip, _ => false));
        Assert.Equal("11202026-3.7z", DeploymentPackageLayout.BackupFileName(date, DeploymentBackupFormats.SevenZip, existing.Contains));
    }

    [Fact]
    public void BackupFileName_UsesZipAndCountsBackupsInEitherFormat()
    {
        var date = new DateTime(2026, 11, 20, 14, 30, 0);
        var existing = new HashSet<string> { "11202026.7z", "11202026-2.zip" };

        Assert.Equal("11202026.zip", DeploymentPackageLayout.BackupFileName(date, DeploymentBackupFormats.Zip, _ => false));
        Assert.Equal("11202026-3.zip", DeploymentPackageLayout.BackupFileName(date, DeploymentBackupFormats.Zip, existing.Contains));
        Assert.Equal("11202026.zip", DeploymentPackageLayout.BackupFileName(date, "unknown", _ => false));
    }

    [Fact]
    public void BackupFileName_FolderHasNoExtensionAndSharesTheDayCounter()
    {
        var date = new DateTime(2026, 11, 20, 14, 30, 0);
        var existing = new HashSet<string> { "11202026.zip", "11202026-2" };

        Assert.Equal("11202026", DeploymentPackageLayout.BackupFileName(date, DeploymentBackupFormats.Folder, _ => false));
        Assert.Equal("11202026-3", DeploymentPackageLayout.BackupFileName(date, DeploymentBackupFormats.Folder, existing.Contains));
        Assert.Equal("11202026-3.7z", DeploymentPackageLayout.BackupFileName(date, DeploymentBackupFormats.SevenZip, existing.Contains));
        Assert.Equal(DeploymentBackupFormats.Folder, DeploymentBackupFormats.FromPath("/backups/11202026-3"));
        Assert.Equal(DeploymentBackupFormats.Zip, DeploymentBackupFormats.FromPath("/backups/11202026.ZIP"));
    }
}
