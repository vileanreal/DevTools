using DevTools.Shared.Helpers;

namespace DevTools.Shared.Tests.Helpers;

public sealed class DeploymentPathMatcherTests
{
    private static bool Excluded(string patterns, string path, bool isDirectory = false) =>
        DeploymentPathMatcher.Parse(patterns).IsExcluded(path, isDirectory);

    [Theory]
    [InlineData("web.config", true)]
    [InlineData("Web.Config", true)]
    [InlineData("sub/web.config", true)]
    [InlineData("web.config.bak", false)]
    [InlineData("app.dll", false)]
    public void NameWithoutSlash_MatchesAtAnyDepth(string path, bool expected) =>
        Assert.Equal(expected, Excluded("web.config", path));

    [Theory]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Production.json", true)]
    [InlineData("config/appsettings.Local.json", true)]
    [InlineData("appsettings.xml", false)]
    public void Wildcards_MatchWithinOneName(string path, bool expected) =>
        Assert.Equal(expected, Excluded("appsettings*.json", path));

    [Theory]
    [InlineData("logs/today.log", true)]
    [InlineData("logs/2026/11/app.log", true)]
    [InlineData("site/logs/today.log", true)]
    [InlineData("logs.txt", false)]
    public void TrailingSlash_ExcludesFolderAndContents(string path, bool expected) =>
        Assert.Equal(expected, Excluded("logs/", path));

    [Fact]
    public void TrailingSlash_MatchesTheFolderItselfOnlyAsADirectory()
    {
        Assert.True(Excluded("logs/", "logs", isDirectory: true));
        Assert.False(Excluded("logs/", "logs", isDirectory: false));
    }

    [Theory]
    [InlineData("App_Data/db.sqlite", true)]
    [InlineData("App_Data/sub/deep/file.txt", true)]
    [InlineData("other/App_Data/file.txt", false)]
    public void PatternWithSlash_IsAnchoredToModuleRoot(string path, bool expected) =>
        Assert.Equal(expected, Excluded("App_Data/**", path));

    [Theory]
    [InlineData("config/a.xml", true)]
    [InlineData("config/sub/a.xml", false)]
    [InlineData("config/a.json", false)]
    public void AnchoredSingleStar_StaysInOneFolder(string path, bool expected) =>
        Assert.Equal(expected, Excluded("config/*.xml", path));

    [Theory]
    [InlineData("a/b/c/settings.json", true)]
    [InlineData("settings.json", true)]
    public void DoubleStarSlash_MatchesZeroOrMoreFolders(string path, bool expected) =>
        Assert.Equal(expected, Excluded("**/settings.json", path));

    [Fact]
    public void BackslashesAreTreatedAsSeparators()
    {
        Assert.True(Excluded(@"logs\", @"logs\a.log"));
        Assert.True(Excluded("logs/", @"logs\a.log"));
    }

    [Fact]
    public void CommentsBlankLinesAndSemicolonsAreHandled()
    {
        var matcher = DeploymentPathMatcher.Parse("# keep config\nweb.config\n\n  ;logs/  \n");

        Assert.Equal(["web.config", "logs/"], matcher.Patterns);
        Assert.True(matcher.IsExcluded("web.config"));
        Assert.True(matcher.IsExcluded("logs/x.log"));
    }

    [Fact]
    public void EmptyMatcher_ExcludesNothing()
    {
        Assert.True(DeploymentPathMatcher.Parse(null).IsEmpty);
        Assert.False(DeploymentPathMatcher.Parse("   ").IsExcluded("web.config"));
    }

    [Fact]
    public void RegexCharactersInNamesAreLiteral() =>
        Assert.False(Excluded("app(1).dll", "app1.dll"));

    [Theory]
    [InlineData("C:/web.config")]
    [InlineData("../secrets")]
    [InlineData("logs/../web.config")]
    public void Validate_RejectsUnsafePatterns(string value) =>
        Assert.NotNull(DeploymentPathMatcher.Validate(value));

    [Fact]
    public void Validate_AcceptsTypicalLists() =>
        Assert.Null(DeploymentPathMatcher.Validate("web.config\nappsettings*.json\nlogs/\nApp_Data/**"));

    [Fact]
    public void Normalize_StoresOnePatternPerLine() =>
        Assert.Equal("web.config\nlogs/", DeploymentPathMatcher.Normalize(" web.config ; logs\\ "));
}
