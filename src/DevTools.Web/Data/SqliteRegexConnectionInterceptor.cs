using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DevTools.Web.Data;

public sealed class SqliteRegexConnectionInterceptor : DbConnectionInterceptor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ConcurrentDictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Register(connection);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Register(connection);
        return Task.CompletedTask;
    }

    private void Register(DbConnection connection)
    {
        if (connection is SqliteConnection sqliteConnection)
            sqliteConnection.CreateFunction<string?, string?, bool>("regexp", Matches, isDeterministic: false);
    }

    private bool Matches(string? pattern, string? value)
    {
        if (string.IsNullOrEmpty(pattern) || value is null) return false;
        try
        {
            if (_patterns.Count >= 100 && !_patterns.ContainsKey(pattern)) _patterns.Clear();
            var regex = _patterns.GetOrAdd(pattern, key => new Regex(
                key,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout));
            return regex.IsMatch(value);
        }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
