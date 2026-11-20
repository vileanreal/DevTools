using DevTools.Shared.Helpers;

namespace DevTools.Shared.Tests.Helpers;

public sealed class LogFileNameFilterTests
{
    // Mirrors the System Logs default: files with a supported extension are read when no include pattern is set.
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".log", ".txt", ".out", ".json", ".jsonl" };
    private static bool DefaultInclude(string fileName) => SupportedExtensions.Contains(Path.GetExtension(fileName));

    private static bool Matches(string? filter, string fileName) => LogFileNameFilter.Parse(filter).IsMatch(fileName, DefaultInclude);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; , ;\n")]
    public void Normalize_ReturnsNull_WhenThereAreNoPatterns(string? value) =>
        Assert.Null(LogFileNameFilter.Normalize(value));

    [Theory]
    [InlineData(" api-*.log ; app.log.* ", "api-*.log;app.log.*")]
    [InlineData("api-*.log,worker.log\napp.log", "api-*.log;worker.log;app.log")]
    [InlineData("api-*.log\r\n!audit-*.log", "api-*.log;!audit-*.log")]
    [InlineData("API-*.log;api-*.LOG;worker.log", "API-*.log;worker.log")]
    public void Normalize_TrimsSplitsAndDeduplicates(string value, string expected) =>
        Assert.Equal(expected, LogFileNameFilter.Normalize(value));

    [Fact]
    public void Parse_SeparatesIncludesFromExclusions()
    {
        var filter = LogFileNameFilter.Parse("*.log; ! audit-*.log ;app.log.*");

        Assert.Equal(["*.log", "app.log.*"], filter.Includes);
        Assert.Equal(["audit-*.log"], filter.Excludes);
        Assert.True(filter.HasIncludes);
    }

    [Fact]
    public void Parse_ReturnsEmpty_ForBlankValue()
    {
        var filter = LogFileNameFilter.Parse("  ");

        Assert.Same(LogFileNameFilter.Empty, filter);
        Assert.False(filter.HasIncludes);
        Assert.Empty(filter.Excludes);
    }

    [Theory]
    [InlineData("api-1.log", true)]
    [InlineData("worker.txt", true)]
    [InlineData("events.jsonl", true)]
    [InlineData("app.log.1", false)]
    [InlineData("notes.md", false)]
    [InlineData("archive.log.gz", false)]
    public void IsMatch_WithoutFilter_UsesDefaultExtensions(string fileName, bool expected) =>
        Assert.Equal(expected, Matches(null, fileName));

    [Theory]
    [InlineData("api-1.log", true)]
    [InlineData("api-payments.log", true)]
    [InlineData("API-2.LOG", true)]
    [InlineData("worker.log", false)]
    [InlineData("api-1.txt", false)]
    [InlineData("my-api-1.log", false)]
    public void IsMatch_WithIncludePattern_SelectsOnlyMatchingNames(string fileName, bool expected) =>
        Assert.Equal(expected, Matches("api-*.log", fileName));

    [Theory]
    [InlineData("app.log", true)]
    [InlineData("app.log.1", true)]
    [InlineData("app.log.2026-11-20", true)]
    [InlineData("app.log.gz", true)]
    [InlineData("other.log", false)]
    public void IsMatch_IncludePattern_IgnoresSupportedExtensionCheck(string fileName, bool expected) =>
        Assert.Equal(expected, Matches("app.log;app.log.*", fileName));

    [Theory]
    [InlineData("api-1.log", true)]
    [InlineData("worker.log", true)]
    [InlineData("audit-1.log", false)]
    [InlineData("AUDIT-2.log", false)]
    [InlineData("notes.txt", false)]
    public void IsMatch_ExclusionWinsOverInclude(string fileName, bool expected) =>
        Assert.Equal(expected, Matches("*.log;!audit-*.log", fileName));

    [Theory]
    [InlineData("api-1.log", true)]
    [InlineData("worker.txt", true)]
    [InlineData("debug.log", false)]
    [InlineData("debug.txt", false)]
    [InlineData("notes.md", false)]
    public void IsMatch_OnlyExclusions_FiltersTheDefaultSelection(string fileName, bool expected) =>
        Assert.Equal(expected, Matches("!debug.*", fileName));

    [Theory]
    [InlineData("api-1.log", true)]
    [InlineData("api-12.log", false)]
    [InlineData("api-.log", false)]
    public void IsMatch_QuestionMarkMatchesExactlyOneCharacter(string fileName, bool expected) =>
        Assert.Equal(expected, Matches("api-?.log", fileName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api-*.log")]
    [InlineData("api-*.log;app.log.*;!audit-*.log")]
    [InlineData("service ?.log, worker.txt")]
    public void Validate_AcceptsValidFilters(string? value) =>
        Assert.Null(LogFileNameFilter.Validate(value));

    [Theory]
    [InlineData("logs/api-*.log")]
    [InlineData(@"logs\api-*.log")]
    [InlineData("C:api.log")]
    [InlineData("a|b.log")]
    [InlineData("a<b.log")]
    [InlineData("\"api.log\"")]
    public void Validate_RejectsFolderAndInvalidCharacters(string value)
    {
        var error = LogFileNameFilter.Validate(value);

        Assert.NotNull(error);
        Assert.Contains("not a valid file name pattern", error);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("api-*.log; ! ")]
    public void Validate_RejectsEmptyExclusion(string value) =>
        Assert.Equal("A '!' exclusion must be followed by a file name pattern.", LogFileNameFilter.Validate(value));

    [Fact]
    public void Validate_RejectsTooLongValue()
    {
        var value = new string('a', LogFileNameFilter.MaxLength + 1);

        Assert.Equal($"File name filter must be {LogFileNameFilter.MaxLength} characters or fewer.", LogFileNameFilter.Validate(value));
    }

    [Fact]
    public void Validate_RejectsTooManyPatterns()
    {
        var value = string.Join(';', Enumerable.Range(1, LogFileNameFilter.MaxPatterns + 1).Select(index => $"f{index}.log"));

        Assert.Equal($"File name filter can contain at most {LogFileNameFilter.MaxPatterns} patterns.", LogFileNameFilter.Validate(value));
    }

    [Fact]
    public void Validate_AllowsExactlyMaxPatterns()
    {
        var value = string.Join(';', Enumerable.Range(1, LogFileNameFilter.MaxPatterns).Select(index => $"f{index}.log"));

        Assert.Null(LogFileNameFilter.Validate(value));
    }
}
