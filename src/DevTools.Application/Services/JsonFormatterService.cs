using System.Text.Json;
using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public sealed class JsonFormatterService : IJsonFormatterService
{
    public FormatResult Format(string input, int indentSize)
    {
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { AllowTrailingCommas = true });
            var options = new JsonSerializerOptions { WriteIndented = true };
            var output = JsonSerializer.Serialize(document.RootElement, options);
            return new FormatResult(ApplyIndent(output, Math.Clamp(indentSize, 2, 8)), true);
        }
        catch (JsonException ex)
        {
            return new FormatResult(string.Empty, false, $"Invalid JSON near line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}.");
        }
    }

    private static string ApplyIndent(string value, int size) => size == 2
        ? value
        : string.Join(Environment.NewLine, value.Split('\n').Select(line => line.TrimStart().Length == line.Length
            ? line.TrimEnd('\r')
            : new string(' ', (line.Length - line.TrimStart().Length) / 2 * size) + line.TrimStart().TrimEnd('\r')));
}
