namespace DevTools.Shared.Helpers;

public static class TextHelper
{
    public static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static bool ContainsIgnoreCase(string source, string? value) =>
        string.IsNullOrWhiteSpace(value) || source.Contains(value, StringComparison.OrdinalIgnoreCase);
}
