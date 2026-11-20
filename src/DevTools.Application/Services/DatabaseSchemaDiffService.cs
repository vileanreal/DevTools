using System.Text.RegularExpressions;

namespace DevTools.Application.Services;

public interface IDatabaseSchemaDiffService
{
    bool Equivalent(string leftDefinition, string rightDefinition);
    string Classify(string? leftDefinition, string? rightDefinition);
}

public sealed class DatabaseSchemaDiffService : IDatabaseSchemaDiffService
{
    public bool Equivalent(string leftDefinition, string rightDefinition) =>
        Normalize(leftDefinition).Equals(Normalize(rightDefinition), StringComparison.OrdinalIgnoreCase);

    public string Classify(string? leftDefinition, string? rightDefinition) =>
        leftDefinition is null ? "Deleted" :
        rightDefinition is null ? "Added" :
        Equivalent(leftDefinition, rightDefinition) ? "Unchanged" : "Changed";

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim().TrimEnd(';'), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
}
