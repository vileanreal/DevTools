using System.IO.Compression;
using DevTools.Application.Services.Deployment;
using DevTools.Shared.Models;

namespace DevTools.Application.Tests.Deployment;

public sealed class DeploymentVersionCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devtools-version-tests", Guid.NewGuid().ToString("N"));
    private readonly DeploymentVersionChecker _checker = new(new PackageArchiveService());
    // Two real .NET assemblies: this test assembly (version 1.0.0.0) and the runtime's core library (a 9.x version).
    private static readonly string AssemblyA = typeof(DeploymentVersionCheckerTests).Assembly.Location;
    private static readonly string AssemblyB = typeof(object).Assembly.Location;

    public DeploymentVersionCheckerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private string Package(string mainFile)
    {
        var source = Path.Combine(_root, "pkg", "release-2.0", "WebApi", "bin");
        Directory.CreateDirectory(source);
        File.Copy(mainFile, Path.Combine(source, "App.dll"), overwrite: true);
        var zip = Path.Combine(_root, "package.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(Path.Combine(_root, "pkg"), zip);
        return zip;
    }

    private string Server(string? mainFile)
    {
        var target = Path.Combine(_root, "server", "WebApi");
        Directory.CreateDirectory(Path.Combine(target, "bin"));
        if (mainFile is not null) File.Copy(mainFile, Path.Combine(target, "bin", "App.dll"), overwrite: true);
        return target;
    }

    private DeploymentVersionCheck Check(string zip, string target, bool require) =>
        Assert.Single(_checker.Check(zip, "release-2.0", [new VersionCheckModule(1, "WebApi", "bin/App.dll", "WebApi", target)], require, CancellationToken.None));

    [Fact]
    public void SameFileOnTheServer_IsBlocked_WhenANewVersionIsRequired()
    {
        var result = Check(Package(AssemblyA), Server(AssemblyA), require: true);
        Assert.Equal((DeploymentVersionStates.Same, true), (result.State, result.Blocking));
        Assert.Equal(result.PackageVersion, result.ServerVersion);
    }

    [Fact]
    public void NewerVersion_IsAllowed()
    {
        var result = Check(Package(AssemblyB), Server(AssemblyA), require: true);
        Assert.Equal((DeploymentVersionStates.Newer, false), (result.State, result.Blocking));
    }

    [Fact]
    public void OlderVersion_IsBlocked_WhenANewVersionIsRequired()
    {
        var result = Check(Package(AssemblyA), Server(AssemblyB), require: true);
        Assert.Equal((DeploymentVersionStates.Older, true), (result.State, result.Blocking));
    }

    [Fact]
    public void MainFileNotOnTheServerYet_IsAFirstDeploy()
    {
        var result = Check(Package(AssemblyA), Server(null), require: true);
        Assert.Equal((DeploymentVersionStates.FirstDeploy, false), (result.State, result.Blocking));
    }

    [Fact]
    public void MainFileMissingFromThePackage_IsReported()
    {
        var zip = Package(AssemblyA);
        var result = Assert.Single(_checker.Check(zip, "release-2.0", [new VersionCheckModule(1, "WebApi", "bin/Other.dll", "WebApi", Server(AssemblyA))], true, CancellationToken.None));
        Assert.Equal((DeploymentVersionStates.NotInPackage, true), (result.State, result.Blocking));
    }
}
