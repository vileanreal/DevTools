using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public interface IFormatterService
{
    FormatResult Format(string input, int indentSize);
}

public interface IJsonFormatterService : IFormatterService;
public interface IXmlFormatterService : IFormatterService;

public interface ICodeFormatterService
{
    FormatResult Format(string input, string language, int indentSize, bool useTabs);
    IReadOnlyCollection<string> SupportedLanguages { get; }
}
