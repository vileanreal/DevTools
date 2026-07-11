using System.Text;
using System.Text.RegularExpressions;
using DevTools.Shared.Helpers;
using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public sealed class CodeFormatterService(
    IJsonFormatterService jsonFormatter,
    IXmlFormatterService xmlFormatter) : ICodeFormatterService
{
    private static readonly string[] Languages =
    [
        "csharp", "java", "javascript", "typescript", "c", "cpp", "css", "scss",
        "sql", "html", "xml", "json", "python", "ruby", "php", "go", "rust",
        "kotlin", "swift", "shell", "yaml", "plaintext"
    ];

    private static readonly HashSet<string> BraceLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "java", "javascript", "typescript", "c", "cpp", "css", "scss",
        "php", "go", "rust", "kotlin", "swift"
    };

    public IReadOnlyCollection<string> SupportedLanguages => Languages;

    public FormatResult Format(string input, string language, int indentSize, bool useTabs)
    {
        var normalized = language.Trim().ToLowerInvariant();
        if (!Languages.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return new FormatResult(string.Empty, false, "The selected language is not supported.");

        indentSize = Math.Clamp(indentSize, 1, 8);
        var result = normalized switch
        {
            "json" => jsonFormatter.Format(input, indentSize),
            "xml" => xmlFormatter.Format(input, indentSize),
            "html" => new FormatResult(FormatHtml(input, indentSize), true),
            "sql" => new FormatResult(FormatSql(input, indentSize), true),
            _ when BraceLanguages.Contains(normalized) => new FormatResult(FormatBraces(input, indentSize), true),
            _ => new FormatResult(NormalizeIndentation(input, indentSize), true)
        };

        return result.IsValid && useTabs
            ? result with { Output = SpacesToTabs(result.Output, indentSize) }
            : result;
    }

    private static string FormatBraces(string input, int indentSize)
    {
        var source = TextHelper.NormalizeLineEndings(input);
        var lines = new List<string>();
        var token = new StringBuilder();
        var indent = 0;
        var quote = '\0';
        var escaped = false;

        void Flush()
        {
            var value = token.ToString().Trim();
            if (value.Length > 0) lines.Add(new string(' ', indent * indentSize) + value);
            token.Clear();
        }

        foreach (var character in source)
        {
            if (quote != '\0')
            {
                token.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"' or '`') { quote = character; token.Append(character); continue; }
            switch (character)
            {
                case '{':
                    if (token.ToString().Trim().Length > 0) token.Append(' ');
                    token.Append('{'); Flush(); indent++; break;
                case '}':
                    Flush(); indent = Math.Max(0, indent - 1); token.Append('}'); break;
                case ';':
                    token.Append(';'); Flush(); break;
                case '\n':
                    Flush(); break;
                default:
                    if (!char.IsWhiteSpace(character) || (token.Length > 0 && token[^1] != ' ')) token.Append(character);
                    break;
            }
        }
        Flush();
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatSql(string input, int indentSize)
    {
        var tokens = TokenizeSql(input);
        var writer = new SqlWriter(indentSize);
        var clause = SqlClause.None;
        var depth = 0;
        var clauseDepth = 0;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var phrase = ReadSqlPhrase(tokens, ref index);
            var keyword = phrase.ToUpperInvariant();

            if (keyword is "SELECT" or "WITH" or "INSERT INTO" or "UPDATE" or "DELETE FROM")
            {
                writer.StartLine(depth, keyword);
                clause = keyword == "SELECT" ? SqlClause.Select : SqlClause.Other;
                clauseDepth = depth;
                if (keyword == "SELECT") writer.StartLine(depth + 1);
                continue;
            }

            if (keyword is "FROM" or "WHERE" or "GROUP BY" or "ORDER BY" or "HAVING" or "LIMIT" or "OFFSET" or "FETCH" or "RETURNING" or "SET" or "VALUES")
            {
                writer.StartLine(depth, keyword);
                clause = keyword switch
                {
                    "WHERE" => SqlClause.Where,
                    "HAVING" => SqlClause.Having,
                    "SET" => SqlClause.Set,
                    "VALUES" => SqlClause.Values,
                    _ => SqlClause.Other
                };
                clauseDepth = depth;
                continue;
            }

            if (keyword is "JOIN" or "LEFT JOIN" or "RIGHT JOIN" or "INNER JOIN" or "FULL JOIN" or "FULL OUTER JOIN" or "LEFT OUTER JOIN" or "RIGHT OUTER JOIN" or "CROSS JOIN")
            {
                writer.StartLine(depth, keyword);
                clause = SqlClause.Join;
                clauseDepth = depth;
                continue;
            }

            if (keyword == "ON")
            {
                writer.StartLine(depth + 1, keyword);
                clause = SqlClause.On;
                clauseDepth = depth;
                continue;
            }

            if (keyword is "UNION" or "UNION ALL" or "EXCEPT" or "INTERSECT")
            {
                writer.StartLine(Math.Max(0, depth - 1), keyword);
                clause = SqlClause.None;
                continue;
            }

            if (keyword is "AND" or "OR" && clause is SqlClause.Where or SqlClause.Having or SqlClause.On)
            {
                writer.StartLine(clauseDepth + 1, keyword);
                continue;
            }

            if (token == "(")
            {
                writer.Append(token, spaceBefore: !IsFunctionCall(tokens, index));
                depth++;
                continue;
            }
            if (token == ")")
            {
                depth = Math.Max(0, depth - 1);
                writer.Append(token, spaceBefore: false);
                continue;
            }
            if (token == ",")
            {
                writer.Append(token, spaceBefore: false);
                if (depth == clauseDepth && clause is SqlClause.Select or SqlClause.Set or SqlClause.Values)
                    writer.StartLine(clauseDepth + 1);
                continue;
            }
            if (token == ";")
            {
                writer.Append(token, spaceBefore: false);
                writer.StartLine(0);
                clause = SqlClause.None;
                continue;
            }
            if (token.StartsWith("--", StringComparison.Ordinal) || token.StartsWith("/*", StringComparison.Ordinal))
            {
                writer.Append(token);
                writer.StartLine(depth);
                continue;
            }

            writer.Append(IsSqlKeyword(keyword) ? keyword : token, spaceBefore: token != "." && writer.LastToken != ".");
        }

        return writer.ToString();
    }

    private static List<string> TokenizeSql(string input) => Regex.Matches(input,
        """--[^\r\n]*|/\*[\s\S]*?\*/|'(?:''|[^'])*'|"(?:""|[^"])*"|\[[^\]]*\]|`[^`]*`|(?:\d+\.\d+|\d+)|(?:<>|!=|<=|>=|::|:=|[-+*/%=<>])|[(),.;]|[\w@$#]+""",
        RegexOptions.CultureInvariant).Select(match => match.Value).ToList();

    private static string ReadSqlPhrase(IReadOnlyList<string> tokens, ref int index)
    {
        string[] phrases = ["FULL OUTER JOIN", "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "INSERT INTO", "DELETE FROM", "GROUP BY", "ORDER BY", "UNION ALL", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "CROSS JOIN"];
        foreach (var phrase in phrases)
        {
            var length = phrase.Count(character => character == ' ') + 1;
            var candidate = string.Join(' ', tokens.Skip(index).Take(length));
            if (index + length <= tokens.Count && candidate.Equals(phrase, StringComparison.OrdinalIgnoreCase))
            {
                index += length - 1;
                return phrase;
            }
        }
        return tokens[index];
    }

    private static bool IsFunctionCall(IReadOnlyList<string> tokens, int openParenthesisIndex) =>
        openParenthesisIndex > 0 && Regex.IsMatch(tokens[openParenthesisIndex - 1], @"^[\w@$#]+$") &&
        !tokens[openParenthesisIndex - 1].Equals("IN", StringComparison.OrdinalIgnoreCase) &&
        !tokens[openParenthesisIndex - 1].Equals("EXISTS", StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlKeyword(string value) => SqlKeywords.Contains(value);

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AS", "ASC", "CASE", "DESC", "DISTINCT", "ELSE", "END", "EXISTS", "IN", "IS", "LIKE", "NOT", "NULL", "THEN", "WHEN",
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "FETCH", "RETURNING", "SET", "VALUES",
        "JOIN", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "CROSS JOIN", "ON", "AND", "OR", "WITH", "INSERT INTO", "UPDATE", "DELETE FROM"
    };

    private enum SqlClause { None, Select, Where, Having, Join, On, Set, Values, Other }

    private sealed class SqlWriter(int indentSize)
    {
        private readonly List<string> _lines = [];
        private readonly StringBuilder _line = new();
        private int _indent;
        public string? LastToken { get; private set; }

        public void StartLine(int indent, string? firstToken = null)
        {
            Flush();
            _indent = Math.Max(0, indent);
            LastToken = null;
            if (firstToken is not null) Append(firstToken, false);
        }

        public void Append(string token, bool spaceBefore = true)
        {
            if (_line.Length == 0) _line.Append(' ', _indent * indentSize);
            if (spaceBefore && _line.Length > _indent * indentSize && !token.StartsWith(".", StringComparison.Ordinal)) _line.Append(' ');
            _line.Append(token);
            LastToken = token;
        }

        public override string ToString()
        {
            Flush();
            return string.Join(Environment.NewLine, _lines).Trim();
        }

        private void Flush()
        {
            var value = _line.ToString().TrimEnd();
            if (value.Trim().Length > 0) _lines.Add(value);
            _line.Clear();
        }
    }

    private static string FormatHtml(string input, int indentSize)
    {
        var tokens = Regex.Split(input.Trim(), @"(?=<)|(?<=>)").Where(token => !string.IsNullOrWhiteSpace(token));
        var voidTags = new HashSet<string>(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"], StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var indent = 0;
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            var closing = token.StartsWith("</", StringComparison.Ordinal);
            if (closing) indent = Math.Max(0, indent - 1);
            lines.Add(new string(' ', indent * indentSize) + token);
            var match = Regex.Match(token, @"^<\s*([\w-]+)");
            var opensContainer = match.Success && !closing && !token.StartsWith("<!", StringComparison.Ordinal) &&
                !token.EndsWith("/>", StringComparison.Ordinal) && !voidTags.Contains(match.Groups[1].Value) &&
                !Regex.IsMatch(token, $@"</\s*{Regex.Escape(match.Groups[1].Value)}\s*>$", RegexOptions.IgnoreCase);
            if (opensContainer) indent++;
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeIndentation(string input, int indentSize) => string.Join(Environment.NewLine,
        TextHelper.NormalizeLineEndings(input).Split('\n').Select(line =>
        {
            var leading = line.TakeWhile(char.IsWhiteSpace).Sum(character => character == '\t' ? indentSize : 1);
            return new string(' ', leading) + line.Trim();
        }));

    private static string SpacesToTabs(string input, int indentSize) => string.Join(Environment.NewLine,
        TextHelper.NormalizeLineEndings(input).Split('\n').Select(line =>
        {
            var spaces = line.TakeWhile(character => character == ' ').Count();
            return new string('\t', spaces / indentSize) + new string(' ', spaces % indentSize) + line[spaces..];
        }));
}
