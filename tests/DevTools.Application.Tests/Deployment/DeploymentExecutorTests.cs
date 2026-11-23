using System.Text.Json;
using DevTools.Application.Services;
using DevTools.Application.Services.Deployment;
using DevTools.Shared.Models;

namespace DevTools.Application.Tests.Deployment;

public sealed class DeploymentExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devtools-deploy-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _events = [];
    private readonly FakeRuntime _runtime;
    private readonly PackageArchiveService _archives = new();
    private readonly DeploymentExecutor _executor;

    public DeploymentExecutorTests()
    {
        Directory.CreateDirectory(_root);
        _runtime = new FakeRuntime(_events);
        _executor = new DeploymentExecutor(_archives, _runtime);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Partial_ReplacesAndAddsFiles_KeepsExcluded_BacksUpOnlyReplacedFiles()
    {
        var target = Folder("site", ("app.dll", "old app"), ("web.config", "server config"), ("logs/today.log", "log"));
        var staging = Folder("pkg", ("app.dll", "new app"), ("lib/new.dll", "new lib"), ("web.config", "package config"));
        var plan = Plan(Module(target, staging, DeploymentModes.Partial, "web.config"));

        var manifest = await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);

        Assert.Equal("new app", Read(target, "app.dll"));
        Assert.Equal("new lib", Read(target, "lib/new.dll"));
        Assert.Equal("server config", Read(target, "web.config"));
        Assert.Equal("log", Read(target, "logs/today.log"));
        var module = Assert.Single(manifest.Modules);
        Assert.Equal(["app.dll"], module.BackedUpFiles);
        Assert.Equal(["lib/new.dll"], module.AddedFiles);
        Assert.Equal(["WebApi/app.dll"], BackupFiles(plan.BackupPath));
        Assert.Contains("\"packageId\": 7", _archives.ReadText(plan.BackupPath, DeploymentSteps.ManifestEntry, CancellationToken.None));
    }

    [Theory]
    [InlineData("11202026.7z")]
    [InlineData("11202026.zip")]
    [InlineData("11202026")]
    public async Task Partial_Rollback_RestoresReplacedFiles_AndRemovesAddedFiles(string backupName)
    {
        var target = Folder("site", ("app.dll", "old app"), ("web.config", "server config"));
        var staging = Folder("pkg", ("app.dll", "new app"), ("lib/new.dll", "new lib"));
        var plan = Plan(Module(target, staging, DeploymentModes.Partial, "web.config")) with { BackupPath = Path.Combine(_root, "backups", backupName) };
        var manifest = await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);
        File.WriteAllText(Path.Combine(target, "web.config"), "edited after deploy");

        await _executor.RollbackAsync(plan.BackupPath, Roundtrip(manifest), new Reporter(_events), CancellationToken.None);

        Assert.Equal("old app", Read(target, "app.dll"));
        Assert.False(File.Exists(Path.Combine(target, "lib", "new.dll")));
        Assert.False(Directory.Exists(Path.Combine(target, "lib")));
        Assert.Equal("edited after deploy", Read(target, "web.config"));
    }

    [Fact]
    public async Task Full_DeletesEverythingExceptExcluded_BacksUpWholeFolder_AndKeepsEmptyFolders()
    {
        var target = Folder("site", ("old.dll", "old"), ("bin/old2.dll", "old2"), ("web.config", "server config"), ("logs/today.log", "log"));
        Directory.CreateDirectory(Path.Combine(target, "uploads"));
        var staging = Folder("pkg", ("new.dll", "new"), ("logs/package.log", "should be skipped"));
        var plan = Plan(Module(target, staging, DeploymentModes.Full, "web.config\nlogs/"));

        var manifest = await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(target, "old.dll")));
        Assert.False(Directory.Exists(Path.Combine(target, "bin")));
        Assert.Equal("new", Read(target, "new.dll"));
        Assert.Equal("server config", Read(target, "web.config"));
        Assert.Equal("log", Read(target, "logs/today.log"));
        Assert.False(File.Exists(Path.Combine(target, "logs", "package.log")));
        Assert.True(Directory.Exists(Path.Combine(target, "uploads")), "A folder that was already empty is left alone.");
        Assert.Equal(["WebApi/bin/old2.dll", "WebApi/logs/today.log", "WebApi/old.dll", "WebApi/web.config"], BackupFiles(plan.BackupPath));
        Assert.Equal(4, Assert.Single(manifest.Modules).BackedUpFiles.Count);
    }

    [Theory]
    [InlineData("11202026.7z")]
    [InlineData("11202026.zip")]
    [InlineData("11202026")]
    public async Task Full_Rollback_RestoresPreviousFiles_WithoutOverwritingExcludedFiles(string backupName)
    {
        var target = Folder("site", ("old.dll", "old"), ("bin/old2.dll", "old2"), ("web.config", "server config"));
        var staging = Folder("pkg", ("new.dll", "new"));
        var plan = Plan(Module(target, staging, DeploymentModes.Full, "web.config")) with { BackupPath = Path.Combine(_root, "backups", backupName) };
        var manifest = await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);
        File.WriteAllText(Path.Combine(target, "web.config"), "edited after deploy");

        await _executor.RollbackAsync(plan.BackupPath, Roundtrip(manifest), new Reporter(_events), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(target, "new.dll")));
        Assert.Equal("old", Read(target, "old.dll"));
        Assert.Equal("old2", Read(target, "bin/old2.dll"));
        Assert.Equal("edited after deploy", Read(target, "web.config"));
    }

    [Fact]
    public async Task StopsEveryModuleBeforePatching_AndStartsAfterAllArePatched()
    {
        var api = Module(Folder("api", ("a.dll", "1")), Folder("pkg-api", ("a.dll", "2")), DeploymentModes.Partial, null, "WebApi", appPool: "ApiPool");
        var worker = Module(Folder("worker", ("w.exe", "1")), Folder("pkg-worker", ("w.exe", "2")), DeploymentModes.Partial, null, "Worker", service: "WorkerSvc");
        var plan = Plan(api, worker);

        await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);

        var patchStart = _events.IndexOf("step:patch:running");
        var patchEnd = _events.IndexOf("step:patch:done");
        Assert.True(_events.IndexOf("stop:app-pool:ApiPool") < patchStart);
        Assert.True(_events.IndexOf("stop:windows-service:WorkerSvc") < patchStart);
        Assert.True(_events.IndexOf("start:app-pool:ApiPool") > patchEnd);
        Assert.True(_events.IndexOf("start:windows-service:WorkerSvc") > patchEnd);
    }

    [Fact]
    public async Task ServicesThatWereAlreadyStopped_AreNotStarted()
    {
        _runtime.Status["windows-service:Paused"] = "Stopped";
        var plan = Plan(Module(Folder("svc", ("a", "1")), Folder("pkg", ("a", "2")), DeploymentModes.Partial, null, service: "Paused"));

        await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);

        Assert.DoesNotContain("stop:windows-service:Paused", _events);
        Assert.DoesNotContain("start:windows-service:Paused", _events);
    }

    [Fact]
    public async Task SharedAppPool_IsStoppedAndStartedOnce()
    {
        var plan = Plan(
            Module(Folder("a", ("x", "1")), Folder("pa", ("x", "2")), DeploymentModes.Partial, null, "A", appPool: "Shared"),
            Module(Folder("b", ("y", "1")), Folder("pb", ("y", "2")), DeploymentModes.Partial, null, "B", appPool: "shared"));

        await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);

        Assert.Single(_events, item => item.StartsWith("stop:app-pool:", StringComparison.OrdinalIgnoreCase));
        Assert.Single(_events, item => item.StartsWith("start:app-pool:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailureAfterPatch_WithAutoRestore_RestoresFilesAndStartsServices()
    {
        _runtime.FailStartsRemaining = 1;
        var target = Folder("site", ("app.dll", "old"));
        var plan = Plan(Module(target, Folder("pkg", ("app.dll", "new"), ("added.dll", "x")), DeploymentModes.Partial, null, appPool: "Pool"), autoRestore: true);

        var failure = await Assert.ThrowsAsync<DeploymentFailedException>(() => _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None));

        Assert.True(failure.Restored);
        Assert.Equal("old", Read(target, "app.dll"));
        Assert.False(File.Exists(Path.Combine(target, "added.dll")));
        Assert.Equal(2, _events.Count(item => item == "start:app-pool:Pool"));
        Assert.Contains("step:restore:done", _events);
    }

    [Fact]
    public async Task FailureAfterPatch_WithoutAutoRestore_LeavesFilesForManualRollback()
    {
        _runtime.FailStartsRemaining = 1;
        var target = Folder("site", ("app.dll", "old"));
        var plan = Plan(Module(target, Folder("pkg", ("app.dll", "new")), DeploymentModes.Partial, null, appPool: "Pool"), autoRestore: false);

        var failure = await Assert.ThrowsAsync<DeploymentFailedException>(() => _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None));

        Assert.False(failure.Restored);
        Assert.Equal("new", Read(target, "app.dll"));
        Assert.True(File.Exists(plan.BackupPath));
    }

    [Fact]
    public async Task UncontrollableService_FailsBeforeAnyFileChanges()
    {
        _runtime.Status["app-pool:Missing"] = "Unavailable";
        var target = Folder("site", ("app.dll", "old"));
        var plan = Plan(Module(target, Folder("pkg", ("app.dll", "new")), DeploymentModes.Full, null, appPool: "Missing"));

        var failure = await Assert.ThrowsAsync<DeploymentFailedException>(() => _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None));

        Assert.True(failure.Restored);
        Assert.Equal("old", Read(target, "app.dll"));
        Assert.DoesNotContain("step:patch:running", _events);
    }

    [Theory]
    [InlineData("11202026.7z")]
    [InlineData("11202026.zip")]
    [InlineData("11202026")]
    public async Task Backup_CanBeReadBack_InEveryFormat(string backupName)
    {
        var target = Folder("site", ("big.bin", new string('x', 200_000)));
        var plan = Plan(Module(target, Folder("pkg", ("big.bin", "y")), DeploymentModes.Partial, null)) with { BackupPath = Path.Combine(_root, "backups", backupName) };

        await _executor.DeployAsync(plan, new Reporter(_events), CancellationToken.None);
        var restoreRoot = Path.Combine(_root, "restored");
        _archives.Extract(plan.BackupPath, restoreRoot, entry => entry.StartsWith("WebApi/") ? entry["WebApi/".Length..] : null, null, CancellationToken.None);

        Assert.Equal(200_000, new FileInfo(Path.Combine(restoreRoot, "big.bin")).Length);
        Assert.NotNull(_archives.ReadText(plan.BackupPath, DeploymentSteps.ManifestEntry, CancellationToken.None));
        if (!backupName.Contains('.'))
        {
            Assert.True(Directory.Exists(plan.BackupPath));
            Assert.False(Directory.Exists(plan.BackupPath + ".partial"));
            return;
        }
        using var stream = File.OpenRead(plan.BackupPath);
        var signature = new byte[2];
        stream.ReadExactly(signature);
        Assert.Equal(backupName.EndsWith(".zip") ? "PK" : "7z", System.Text.Encoding.ASCII.GetString(signature));
    }

    [Fact]
    public async Task ReportsHistorySummaries_ForBackupServicesAndEachModule()
    {
        var web = Module(Folder("site", ("old.dll", "old"), ("app.dll", "v1"), ("web.config", "prod")),
            Folder("pkg", ("app.dll", "v2"), ("new.dll", "new"), ("web.config", "dev")), DeploymentModes.Full, "web.config", "WebApi", appPool: "ApiPool");
        var worker = Module(Folder("svc", ("w.exe", "v1")), Folder("pkg-svc", ("w.exe", "v2"), ("plugin.dll", "p")), DeploymentModes.Partial, null, "Worker");

        await _executor.DeployAsync(Plan(web, worker), new Reporter(_events), CancellationToken.None);

        var summaries = _events.Where(item => item.StartsWith("summary:")).Select(item => item["summary:".Length..]).ToList();
        Assert.Contains(summaries, line => line.StartsWith("Backup: 11202026.7z — 4 file(s), "));
        Assert.Contains("Stopped app pool ApiPool", summaries);
        Assert.Contains("WebApi — whole package: 2 copied (1 new, 1 replaced), 2 deleted, 1 skipped by exclusions", summaries);
        Assert.Contains("Worker — partial update: 2 copied (1 new, 1 replaced)", summaries);
        Assert.Contains("Started app pool ApiPool", summaries);
    }

    [Fact]
    public void Extract_ReadsZipPackages()
    {
        var zipPath = Path.Combine(_root, "release.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("release-2.0/WebApi/bin/app.dll").Open())) writer.Write("v2");
            using (var writer = new StreamWriter(zip.CreateEntry("release-2.0/Docs/readme.md").Open())) writer.Write("docs");
            zip.CreateEntry("release-2.0/WebApi/empty/");
        }
        var staging = Path.Combine(_root, "staging");

        var written = _archives.Extract(zipPath, staging,
            entry => entry.StartsWith("release-2.0/WebApi/") ? "WebApi/" + entry["release-2.0/WebApi/".Length..] : null, null, CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal("v2", Read(staging, "WebApi/bin/app.dll"));
        Assert.False(Directory.Exists(Path.Combine(staging, "Docs")));
    }

    [Fact]
    public void ResolveInside_RefusesPathsOutsideTheRoot()
    {
        Assert.Throws<InvalidOperationException>(() => PackageArchiveService.ResolveInside(_root, "../outside.txt"));
        Assert.Throws<InvalidOperationException>(() => PackageArchiveService.ResolveInside(_root, "a/../../outside.txt"));
        Assert.StartsWith(_root, PackageArchiveService.ResolveInside(_root, "a/b.txt"));
    }

    private string Folder(string name, params (string Path, string Content)[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return folder;
    }

    private static DeploymentModulePlan Module(string target, string staging, string mode, string? exclusions, string name = "WebApi", string? appPool = null, string? service = null) =>
        new(name.GetHashCode(), name, target, mode, exclusions, appPool, service, staging);

    private DeploymentPlan Plan(params DeploymentModulePlan[] modules) => Plan(modules, true);
    private DeploymentPlan Plan(DeploymentModulePlan module, bool autoRestore) => Plan([module], autoRestore);
    private DeploymentPlan Plan(DeploymentModulePlan[] modules, bool autoRestore) =>
        new(7, "release.zip", Path.Combine(_root, "backups", "11202026.7z"), modules, autoRestore);

    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

    private List<string> BackupFiles(string backupPath) =>
        _archives.ListEntries(backupPath, CancellationToken.None)
            .Where(entry => !entry.IsDirectory && !entry.Path.StartsWith("_deployment/"))
            .Select(entry => entry.Path.Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static DeploymentManifest Roundtrip(DeploymentManifest manifest) =>
        JsonSerializer.Deserialize<DeploymentManifest>(JsonSerializer.Serialize(manifest, DeploymentSteps.Json), DeploymentSteps.Json)!;

    private sealed class Reporter(List<string> events) : IDeploymentReporter
    {
        public void Step(string key, string status, string? detail = null) { if (!events.Contains($"step:{key}:{status}") || status != "running") events.Add($"step:{key}:{status}"); }
        public void Log(string level, string message) { }
        public void FailRunningStep(string? detail) => events.Add("step:failed");
        public void Summary(string line) => events.Add("summary:" + line);
    }

    private sealed class FakeRuntime(List<string> events) : ISystemServiceRuntime
    {
        public Dictionary<string, string> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int FailStartsRemaining { get; set; }

        public Task<ServiceRuntimeStatus> GetStatusAsync(string kind, string targetName, CancellationToken cancellationToken) =>
            Task.FromResult(new ServiceRuntimeStatus(Status.GetValueOrDefault($"{kind}:{targetName}", "Running"), null, DateTimeOffset.UtcNow));

        public Task<ServiceRuntimeStatus> ExecuteAsync(string kind, string targetName, string action, CancellationToken cancellationToken)
        {
            events.Add($"{action}:{kind}:{targetName}");
            if (action == "start" && FailStartsRemaining-- > 0) throw new InvalidOperationException($"{targetName} did not start.");
            Status[$"{kind}:{targetName}"] = action == "stop" ? "Stopped" : "Running";
            return Task.FromResult(new ServiceRuntimeStatus(Status[$"{kind}:{targetName}"], null, DateTimeOffset.UtcNow));
        }
    }
}
