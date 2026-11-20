using DevTools.Shared.Helpers;
using DevTools.Shared.Models;

namespace DevTools.Shared.Tests.Helpers;

public sealed class PatchDiffTests
{
    private static readonly DateTime Time = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
    private static PatchFileEntry File(string path, string hash, long size = 10) => new(path, size, Time, hash);
    private static PatchModuleManifest Module(string name, params PatchFileEntry[] files) => new(1, name, files);

    [Fact]
    public void ClassifiesAddedChangedAndDeleted()
    {
        var baseline = new[] { Module("WebApi", File("bin/api.dll", "A1", 100), File("bin/old.dll", "O1"), File("web.config", "W1")) };
        var current = new[] { Module("WebApi", File("bin/api.dll", "A2", 120), File("bin/new.dll", "N1"), File("web.config", "W1")) };

        var (files, modules) = PatchDiff.Compare(baseline, current);

        Assert.Equal(
            [("bin/new.dll", PatchChangeKinds.Added), ("bin/api.dll", PatchChangeKinds.Changed), ("bin/old.dll", PatchChangeKinds.Deleted)],
            files.Select(file => (file.Path, file.Kind)).ToList());
        var changed = files.Single(file => file.Kind == PatchChangeKinds.Changed);
        Assert.Equal((120L, 100L), (changed.Size!.Value, changed.PreviousSize!.Value));
        Assert.Equal(new PatchModuleChangeSummary("WebApi", 1, 1, 1), Assert.Single(modules));
    }

    [Fact]
    public void SameHash_IsNotAChange_EvenIfSizeOrTimeDiffer()
    {
        var baseline = new[] { Module("WebApi", new PatchFileEntry("a.dll", 10, Time, "SAME")) };
        var current = new[] { Module("WebApi", new PatchFileEntry("a.dll", 10, Time.AddDays(1), "same")) };

        Assert.Empty(PatchDiff.Compare(baseline, current).Files);
    }

    [Fact]
    public void PathsAndModulesMatchCaseInsensitively()
    {
        var baseline = new[] { Module("WebApi", File("Bin/App.dll", "X")) };
        var current = new[] { Module("webapi", File("bin/app.DLL", "X")) };

        Assert.Empty(PatchDiff.Compare(baseline, current).Files);
    }

    [Fact]
    public void NoBaseline_MakesEveryFileAdded()
    {
        var (files, modules) = PatchDiff.Compare([], [Module("Worker", File("w.exe", "1"), File("w.dll", "2"))]);

        Assert.All(files, file => Assert.Equal(PatchChangeKinds.Added, file.Kind));
        Assert.Equal(2, Assert.Single(modules).Added);
    }

    [Fact]
    public void RemovedModule_ReportsItsFilesAsDeleted()
    {
        var (files, _) = PatchDiff.Compare([Module("Old", File("x.dll", "1"))], [Module("WebApi")]);

        Assert.Equal(("Old", "x.dll", PatchChangeKinds.Deleted), (files.Single().Module, files.Single().Path, files.Single().Kind));
    }

    [Fact]
    public void ResultIsOrderedByModuleThenKindThenPath()
    {
        var current = new[] { Module("B", File("z", "1")), Module("A", File("y", "1"), File("x", "1")) };

        var (files, modules) = PatchDiff.Compare([], current);

        Assert.Equal(["A/x", "A/y", "B/z"], files.Select(file => $"{file.Module}/{file.Path}"));
        Assert.Equal(["A", "B"], modules.Select(module => module.Module));
    }

    [Theory]
    [InlineData("ADDED", "WebApi", "bin/app.dll", "WebApi/ADDED/bin/app.dll")]
    [InlineData("CHANGED", "Worker", @"Plugins\x.dll", "Worker/CHANGED/Plugins/x.dll")]
    public void ArchiveEntry_UsesModuleThenKindFolders(string kind, string module, string path, string expected) =>
        Assert.Equal(expected, PatchDiff.ArchiveEntry(kind, module, path));
}
