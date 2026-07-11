using System.Xml;
using System.Xml.Linq;
using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public sealed class XmlFormatterService : IXmlFormatterService
{
    public FormatResult Format(string input, int indentSize)
    {
        try
        {
            var document = XDocument.Parse(input, LoadOptions.PreserveWhitespace);
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = new string(' ', Math.Clamp(indentSize, 2, 8)),
                OmitXmlDeclaration = document.Declaration is null,
                NewLineChars = Environment.NewLine
            };
            using var writer = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(writer, settings)) document.Save(xmlWriter);
            return new FormatResult(writer.ToString(), true);
        }
        catch (XmlException ex)
        {
            return new FormatResult(string.Empty, false, $"Invalid XML at line {ex.LineNumber}, position {ex.LinePosition}.");
        }
    }
}
