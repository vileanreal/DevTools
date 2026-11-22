using System.Text;
using DevTools.PatchHelper;

namespace DevTools.PatchHelper.Tests;

public sealed class PatchEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devtools-helper-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeServices _services = new();
    private readonly ListReporter _reporter = new();
    private string PatchFolder => Path.Combine(_root, "patch");
    private string Server => Path.Combine(_root, "server", "WebApi");

    public PatchEngineTests()
    {
        Directory.CreateDirectory(PatchFolder);
        Directory.CreateDirectory(Server);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    // ---------- Helpers ----------

    private static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private void ServerFile(string path, string text)
    {
        var full = Path.Combine(Server, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string? ServerText(string path) => File.Exists(Path.Combine(Server, path)) ? File.ReadAllText(Path.Combine(Server, path)) : null;

    private PatchManifestFile PatchFile(string kind, string path, string? newText, string? oldText)
    {
        if (newText != null)
        {
            var full = Path.Combine(PatchFolder, "WebApi", kind, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, newText);
        }
        return new PatchManifestFile { Path = path, Kind = kind, Sha256 = newText is null ? null : Hash(newText), PreviousSha256 = oldText is null ? null : Hash(oldText) };
    }

    private PatchEngine Engine(params PatchManifestFile[] files)
    {
        var manifest = new PatchManifest
        {
            FormatVersion = 1, System = "Sample", Title = "Sample Patch Helper", PatchName = "sample-patch",
            Modules = [new PatchManifestModule { Name = "WebApi", AppPool = "WebApiPool", Service = "Worker", Files = files.ToList() }]
        };
        Json.WriteFile(Path.Combine(PatchFolder, PatchManifest.FileName), manifest);
        return new PatchEngine(PatchFolder, PatchManifest.Load(PatchFolder), _services, _reporter);
    }

    private Dictionary<string, string> Targets => new() { ["WebApi"] = Server };

    // ---------- Tests ----------

    [Fact]
    public void Manifest_ReadsTheCamelCaseJsonDevToolsWrites()
    {
        File.WriteAllText(Path.Combine(PatchFolder, "patch.json"), """
            {"formatVersion":1,"system":"Sample","title":"T","subtitle":null,"supportContact":"help","patchName":"p","description":"d",
             "createdAt":"2026-11-22 10:00","createdBy":null,"modules":[{"name":"WebApi","clientFolder":"D:\\Sites\\WebApi","appPool":"Pool","service":null,
             "files":[{"path":"bin/a.dll","kind":"CHANGED","size":3,"sha256":"aa","previousSha256":"bb"}]}]}
            """);

        var manifest = PatchManifest.Load(PatchFolder);

        Assert.Equal(("T", "help", "Pool"), (manifest.Title, manifest.SupportContact, manifest.Modules[0].AppPool));
        Assert.Equal(("bin/a.dll", "CHANGED", "aa", "bb"), (manifest.Modules[0].Files[0].Path, manifest.Modules[0].Files[0].Kind, manifest.Modules[0].Files[0].Sha256, manifest.Modules[0].Files[0].PreviousSha256));
    }

    [Fact]
    public void Manifest_FromANewerFormat_IsRefused()
    {
        File.WriteAllText(Path.Combine(PatchFolder, "patch.json"), """{"formatVersion":2,"modules":[]}""");
        Assert.Throws<InvalidDataException>(() => PatchManifest.Load(PatchFolder));
    }

    [Fact]
    public void CheckPatch_FindsMissingAndDamagedFiles()
    {
        var engine = Engine(PatchFile("ADDED", "a.dll", "new", null), new PatchManifestFile { Path = "missing.dll", Kind = "ADDED", Sha256 = Hash("x") });
        File.WriteAllText(Path.Combine(PatchFolder, "WebApi", "ADDED", "a.dll"), "tampered");

        var error = Assert.Throws<PatchHelperException>(() => engine.CheckPatch(CancellationToken.None));

        Assert.Contains("2 file(s)", error.Message);
        Assert.Contains(_reporter.Lines, line => line.Contains("missing.dll: missing from the patch"));
        Assert.Contains(_reporter.Lines, line => line.Contains("a.dll: damaged"));
    }

    [Fact]
    public void Compare_ClassifiesEveryCaseOnTheServer()
    {
        ServerFile("changed.dll", "v1");
        ServerFile("edited.dll", "edited by hand");
        ServerFile("done.dll", "v2");
        ServerFile("old.dll", "old");
        ServerFile("existing-added.dll", "something else");
        var engine = Engine(
            PatchFile("ADDED", "new.dll", "new", null),
            PatchFile("ADDED", "existing-added.dll", "added", null),
            PatchFile("CHANGED", "changed.dll", "v2", "v1"),
            PatchFile("CHANGED", "edited.dll", "v2", "v1"),
            PatchFile("CHANGED", "done.dll", "v2", "v1"),
            PatchFile("CHANGED", "gone.dll", "v2", "v1"),
            PatchFile("DELETED", "old.dll", null, "old"),
            PatchFile("DELETED", "already-gone.dll", null, "x"));

        var plan = engine.Compare(Targets, CancellationToken.None);
        string Describe(string path) { var file = plan.Files.Single(item => item.File.Path == path); return $"{file.Action}{(file.Warning ? "!" : "")}"; }

        Assert.Equal("Add", Describe("new.dll"));
        Assert.Equal("Replace!", Describe("existing-added.dll"));
        Assert.Equal("Replace", Describe("changed.dll"));
        Assert.Equal("Replace!", Describe("edited.dll"));
        Assert.Equal("Skip", Describe("done.dll"));
        Assert.Equal("Add!", Describe("gone.dll"));
        Assert.Equal("Delete", Describe("old.dll"));
        Assert.Equal("Skip", Describe("already-gone.dll"));
        Assert.Equal(3, plan.Warnings.Count());
    }

    [Fact]
    public void Compare_RequiresAnExistingFolderForEveryModule()
    {
        var engine = Engine(PatchFile("ADDED", "a.dll", "new", null));
        Assert.Throws<PatchHelperException>(() => engine.Compare(new Dictionary<string, string>(), CancellationToken.None));
        Assert.Throws<PatchHelperException>(() => engine.Compare(new Dictionary<string, string> { ["WebApi"] = Path.Combine(_root, "nope") }, CancellationToken.None));
    }

    [Fact]
    public void Apply_BacksUpStopsAppliesStartsAndVerifies()
    {
        ServerFile("bin/app.dll", "v1");
        ServerFile("old.dll", "old");
        ServerFile("keep.txt", "untouched");
        var engine = Engine(PatchFile("CHANGED", "bin/app.dll", "v2", "v1"), PatchFile("ADDED", "wwwroot/new.js", "new", null), PatchFile("DELETED", "old.dll", null, "old"));
        engine.CheckPatch(CancellationToken.None);
        var plan = engine.Compare(Targets, CancellationToken.None);
        var state = new ApplyState();

        engine.Apply(plan, state, CancellationToken.None);

        Assert.Equal(("v2", "new", (string?)null, "untouched"), (ServerText("bin/app.dll"), ServerText("wwwroot/new.js"), ServerText("old.dll"), ServerText("keep.txt")));
        Assert.Equal("applied", ApplyState.TryLoad(PatchFolder)!.Status);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(state.BackupFolder, "WebApi", "bin", "app.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(state.BackupFolder, "WebApi", "old.dll")));
        Assert.Equal(["stop-pool:WebApiPool", "stop-service:Worker", "start-service:Worker", "start-pool:WebApiPool"], _services.Calls);
        Assert.Contains(_reporter.Steps, step => step == "verify:Done");
    }

    [Fact]
    public void Apply_DoesNotStartWhatWasAlreadyStopped()
    {
        _services.Stopped.Add("Worker");
        var engine = Engine(PatchFile("ADDED", "a.dll", "new", null));
        var plan = engine.Compare(Targets, CancellationToken.None);

        engine.Apply(plan, new ApplyState(), CancellationToken.None);

        Assert.Equal(["stop-pool:WebApiPool", "start-pool:WebApiPool"], _services.Calls);
    }

    [Fact]
    public void FailedApply_CanBeRolledBack_ToTheExactPreviousFiles()
    {
        ServerFile("bin/app.dll", "v1");
        ServerFile("old.dll", "old");
        _services.FailStart = true;
        var engine = Engine(PatchFile("CHANGED", "bin/app.dll", "v2", "v1"), PatchFile("ADDED", "wwwroot/new.js", "new", null), PatchFile("DELETED", "old.dll", null, "old"));
        var plan = engine.Compare(Targets, CancellationToken.None);
        var state = new ApplyState();

        Assert.Throws<PatchHelperException>(() => engine.Apply(plan, state, CancellationToken.None));
        var saved = ApplyState.TryLoad(PatchFolder)!;
        Assert.Equal("failed", saved.Status);
        Assert.Contains(_reporter.Steps, step => step == "start:Failed");

        _services.FailStart = false;
        engine.Rollback(saved, CancellationToken.None);

        Assert.Equal(("v1", (string?)null, "old"), (ServerText("bin/app.dll"), ServerText("wwwroot/new.js"), ServerText("old.dll")));
        Assert.Equal("rolled-back", ApplyState.TryLoad(PatchFolder)!.Status);
        Assert.Contains("start-pool:WebApiPool", _services.Calls);
    }

    [Fact]
    public void InterruptedApply_ContinuesWithoutRepeatingFinishedSteps()
    {
        ServerFile("a.dll", "v1");
        var engine = Engine(PatchFile("CHANGED", "a.dll", "v2", "v1"));
        var plan = engine.Compare(Targets, CancellationToken.None);
        // Simulate a run that backed up and stopped, then the window was closed.
        var state = new ApplyState { BackupFolder = Path.Combine(PatchFolder, "backup-earlier"), StartedAt = "earlier" };
        state.CompletedSteps.AddRange([PatchEngine.StepBackup, PatchEngine.StepStop]);
        state.StoppedAppPools.Add("WebApiPool");
        state.Save(PatchFolder);

        engine.Apply(plan, ApplyState.TryLoad(PatchFolder)!, CancellationToken.None);

        Assert.Equal("v2", ServerText("a.dll"));
        Assert.False(Directory.Exists(Path.Combine(PatchFolder, "backup-earlier")));
        Assert.Equal(["start-pool:WebApiPool"], _services.Calls);
        Assert.Equal("applied", ApplyState.TryLoad(PatchFolder)!.Status);
    }

    [Fact]
    public void RollbackBeforeAnyFileChanged_OnlyStartsWhatWasStopped()
    {
        var engine = Engine(PatchFile("ADDED", "a.dll", "new", null));
        var state = new ApplyState { BackupFolder = Path.Combine(PatchFolder, "backup-x") };
        state.StoppedServices.Add("Worker");

        engine.Rollback(state, CancellationToken.None);

        Assert.Equal(["start-service:Worker"], _services.Calls);
        Assert.Equal("rolled-back", state.Status);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("bin/../../outside.dll")]
    [InlineData("C:/Windows/evil.dll")]
    public void ResolveInside_RefusesPathsOutsideTheModule(string path) =>
        Assert.Throws<PatchHelperException>(() => PatchEngine.ResolveInside(Path.Combine(_root, "server"), path));

    // ---------- Fakes ----------

    private sealed class FakeServices : IServiceControl
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Stopped { get; } = [];
        public bool FailStart { get; set; }
        public bool StopAppPool(string name) { if (Stopped.Contains(name)) return false; Stopped.Add(name); Calls.Add("stop-pool:" + name); return true; }
        public void StartAppPool(string name) { if (FailStart) throw new PatchHelperException("app pool won't start"); Stopped.Remove(name); Calls.Add("start-pool:" + name); }
        public bool StopService(string name) { if (Stopped.Contains(name)) return false; Stopped.Add(name); Calls.Add("stop-service:" + name); return true; }
        public void StartService(string name) { if (FailStart) throw new PatchHelperException("service won't start"); Stopped.Remove(name); Calls.Add("start-service:" + name); }
    }

    private sealed class ListReporter : IHelperReporter
    {
        public List<string> Lines { get; } = [];
        public List<string> Steps { get; } = [];
        public void Step(string key, StepStatus status, string? detail = null) => Steps.Add($"{key}:{status}");
        public void Log(string message) => Lines.Add(message);
        public void Progress(int done, int total, string text) { }
    }
}
