using DevTools.Shared.Helpers;

namespace DevTools.Shared.Tests.Helpers;

public sealed class DeploymentVersionRulesTests
{
    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", 0)]
    [InlineData("1.2", "1.2.0.0", 0)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0.0", "10.0.0.0", -1)]
    [InlineData("1", "1.0", 0)]
    public void Compare_IsNumeric(string first, string second, int expected) =>
        Assert.Equal(expected, Math.Sign(DeploymentVersionRules.Compare(first, second)));

    [Fact]
    public void SameVersion_BlocksOnlyWhenTheSystemRequiresANewVersion()
    {
        Assert.Equal(("same", true), Pick(DeploymentVersionRules.Evaluate("bin/App.dll", true, "1.2.0.0", true, "1.2.0.0", requireChange: true)));
        Assert.Equal(("same", false), Pick(DeploymentVersionRules.Evaluate("bin/App.dll", true, "1.2.0.0", true, "1.2.0.0", requireChange: false)));
    }

    [Fact]
    public void NewerVersion_IsAllowed() =>
        Assert.Equal(("newer", false), Pick(DeploymentVersionRules.Evaluate("App.exe", true, "1.3.0.0", true, "1.2.0.0", true)));

    [Fact]
    public void OlderVersion_IsBlockedWhenANewVersionIsRequired_BecauseRollbackCoversGoingBack()
    {
        var blocked = DeploymentVersionRules.Evaluate("App.exe", true, "1.1.0.0", true, "1.2.0.0", requireChange: true);
        Assert.Equal(("older", true), Pick(blocked));
        Assert.Contains("Rollback", blocked.Message);
        var shown = DeploymentVersionRules.Evaluate("App.exe", true, "1.1.0.0", true, "1.2.0.0", requireChange: false);
        Assert.Equal(("older", false), Pick(shown));
        Assert.Contains("older than the server", shown.Message);
    }

    [Fact]
    public void FirstDeployment_IsAllowed() =>
        Assert.Equal(("first-deploy", false), Pick(DeploymentVersionRules.Evaluate("App.exe", true, "1.0.0.0", false, null, true)));

    [Fact]
    public void MissingOrUnreadableMainFile_BlocksWhenRequired()
    {
        Assert.Equal(("not-in-package", true), Pick(DeploymentVersionRules.Evaluate("App.exe", false, null, true, "1.0", true)));
        Assert.Equal(("unreadable", true), Pick(DeploymentVersionRules.Evaluate("App.exe", true, null, true, "1.0", true)));
        Assert.Equal(("not-in-package", false), Pick(DeploymentVersionRules.Evaluate("App.exe", false, null, true, "1.0", false)));
    }

    [Fact]
    public void FileVersionReader_ReadsADotNetAssembly_AndReturnsNullForOtherFiles()
    {
        Assert.False(string.IsNullOrEmpty(FileVersionReader.Read(typeof(DeploymentVersionRules).Assembly.Location)));
        var text = Path.GetTempFileName();
        try { File.WriteAllText(text, "not a dll"); Assert.Null(FileVersionReader.Read(text)); }
        finally { File.Delete(text); }
        Assert.Null(FileVersionReader.Read(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".dll")));
    }

    private static (string, bool) Pick((string State, bool Blocking, string Message) result) => (result.State, result.Blocking);
}
