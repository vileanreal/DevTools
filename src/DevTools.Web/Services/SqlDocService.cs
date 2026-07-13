using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DevTools.Shared.Models;
using DevTools.Web.Data;
using DevTools.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Services;

public interface ISqlDocService
{
    Task<IReadOnlyList<SqlDocSourceDto>> ListSourcesAsync(CancellationToken cancellationToken);
    Task<SqlDocSourceDto> CreateSourceAsync(CreateSqlDocSourceRequest request, CancellationToken cancellationToken);
    Task<SqlDocSourceDto?> UpdateSourceAsync(int id, UpdateSqlDocSourceRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteSourceAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListDatabasesAsync(SqlDocConnectionRequest request, CancellationToken cancellationToken);
    Task<SqlDocSchema?> InspectAsync(int id, CancellationToken cancellationToken);
    Task<(byte[] Content, string FileName)?> GenerateDocxAsync(int id, SqlDocGenerateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SqlDocDiagramDto>> ListDiagramsAsync(int sourceId, CancellationToken cancellationToken);
    Task<SqlDocDiagramDto?> GetDiagramAsync(int sourceId, int diagramId, CancellationToken cancellationToken);
    Task<SqlDocDiagramDto?> SaveDiagramAsync(int sourceId, SaveSqlDocDiagramRequest request, CancellationToken cancellationToken);
}

public sealed class SqlDocService(DevToolsDbContext database, IWebHostEnvironment environment) : ISqlDocService
{
    private static readonly HashSet<string> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqlserver", "sqlite", "mysql", "oracle", "postgresql"
    };

    private static readonly HashSet<string> SqlSyntaxKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALTER", "AND", "AS", "ASC", "BEGIN", "BETWEEN", "BY", "CASE", "CAST", "CHECK", "COLUMN",
        "CONSTRAINT", "CREATE", "CROSS", "DECLARE", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE",
        "END", "EXEC", "EXISTS", "FOREIGN", "FROM", "FULL", "FUNCTION", "GROUP", "HAVING", "IF", "IN",
        "INDEX", "INNER", "INSERT", "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "NOT", "NULL", "ON",
        "OR", "ORDER", "OUTER", "PRIMARY", "PROCEDURE", "REFERENCES", "RETURNS", "RIGHT", "SELECT", "SET",
        "TABLE", "THEN", "TOP", "TRIGGER", "UNION", "UNIQUE", "UPDATE", "VALUES", "VIEW", "WHEN", "WHERE",
        "WHILE", "WITH"
    };

    public async Task<IReadOnlyList<SqlDocSourceDto>> ListSourcesAsync(CancellationToken cancellationToken) =>
        await database.SqlDocSources.AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => ToDto(item))
            .ToListAsync(cancellationToken);

    public async Task<SqlDocSourceDto> CreateSourceAsync(CreateSqlDocSourceRequest request, CancellationToken cancellationToken)
    {
        var source = new SqlDocSource
        {
            Name = ValidateName(request.Name),
            Provider = ValidateProvider(request.Provider),
            ConnectionString = BuildConnectionString(request.Provider, request.ConnectionString, request.Server, request.Port, request.Database, request.Username, request.Password, request.IntegratedSecurity, request.TrustServerCertificate, request.SqlitePath),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        database.SqlDocSources.Add(source);
        await database.SaveChangesAsync(cancellationToken);
        return ToDto(source);
    }

    public async Task<SqlDocSourceDto?> UpdateSourceAsync(int id, UpdateSqlDocSourceRequest request, CancellationToken cancellationToken)
    {
        var source = await database.SqlDocSources.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null) return null;
        source.Name = ValidateName(request.Name);
        source.Provider = ValidateProvider(request.Provider);
        source.ConnectionString = BuildConnectionString(request.Provider, request.ConnectionString, request.Server, request.Port, request.Database, request.Username, request.Password, request.IntegratedSecurity, request.TrustServerCertificate, request.SqlitePath);
        source.UpdatedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return ToDto(source);
    }

    public async Task<bool> DeleteSourceAsync(int id, CancellationToken cancellationToken)
    {
        var deleted = await database.SqlDocSources.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(SqlDocConnectionRequest request, CancellationToken cancellationToken)
    {
        var provider = ValidateProvider(request.Provider);
        if (provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var path = string.IsNullOrWhiteSpace(request.SqlitePath) ? request.ConnectionString : request.SqlitePath;
            return string.IsNullOrWhiteSpace(path) ? [] : [Path.GetFileName(path)];
        }
        var connectionString = BuildConnectionString(provider, request.ConnectionString, request.Server, request.Port, DatabaseForDiscovery(provider, request.Database), request.Username, request.Password, request.IntegratedSecurity, request.TrustServerCertificate, request.SqlitePath);
        await using var connection = CreateProviderConnection(provider, connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ValuesAsync(connection, DatabaseListSql(provider), cancellationToken);
    }

    public async Task<SqlDocSchema?> InspectAsync(int id, CancellationToken cancellationToken)
    {
        var source = await database.SqlDocSources.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return source is null ? null : await ReadSchemaAsync(source, cancellationToken);
    }

    public async Task<(byte[] Content, string FileName)?> GenerateDocxAsync(int id, SqlDocGenerateRequest request, CancellationToken cancellationToken)
    {
        var source = await database.SqlDocSources.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null) return null;
        var schema = await ReadSchemaAsync(source, cancellationToken);
        schema = FilterSchema(schema, request);
        var content = BuildDocx(schema, request);
        var safeName = SafeFileName(schema.SourceName);
        return (content, safeName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? safeName : $"{safeName}.docx");
    }

    public async Task<IReadOnlyList<SqlDocDiagramDto>> ListDiagramsAsync(int sourceId, CancellationToken cancellationToken) =>
        await database.SqlDocDiagrams.AsNoTracking()
            .Where(item => item.SqlDocSourceId == sourceId)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Select(item => ToDto(item))
            .ToListAsync(cancellationToken);

    public async Task<SqlDocDiagramDto?> GetDiagramAsync(int sourceId, int diagramId, CancellationToken cancellationToken) =>
        await database.SqlDocDiagrams.AsNoTracking()
            .Where(item => item.SqlDocSourceId == sourceId && item.Id == diagramId)
            .Select(item => ToDto(item))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<SqlDocDiagramDto?> SaveDiagramAsync(int sourceId, SaveSqlDocDiagramRequest request, CancellationToken cancellationToken)
    {
        if (!await database.SqlDocSources.AnyAsync(item => item.Id == sourceId, cancellationToken)) return null;

        var name = ValidateDiagramName(request.Name);
        var diagramJson = ValidateDiagramJson(request.DiagramJson);
        var now = DateTime.UtcNow;
        var diagram = await database.SqlDocDiagrams.SingleOrDefaultAsync(item => item.SqlDocSourceId == sourceId && item.Name == name, cancellationToken);
        if (diagram is null)
        {
            diagram = new SqlDocDiagram
            {
                SqlDocSourceId = sourceId,
                Name = name,
                DiagramJson = diagramJson,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            database.SqlDocDiagrams.Add(diagram);
        }
        else
        {
            diagram.DiagramJson = diagramJson;
            diagram.UpdatedAtUtc = now;
        }

        await database.SaveChangesAsync(cancellationToken);
        return ToDto(diagram);
    }

    private async Task<SqlDocSchema> ReadSchemaAsync(SqlDocSource source, CancellationToken cancellationToken) =>
        source.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase)
            ? await ReadSqliteSchemaAsync(source, cancellationToken)
            : await ReadExternalSchemaAsync(source, cancellationToken);

    private static SqlDocSchema FilterSchema(SqlDocSchema schema, SqlDocGenerateRequest request)
    {
        var selected = (request.ObjectNames ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return schema;
        bool Has(string type, string name) => selected.Contains($"{type}:{name}");
        var tables = schema.Tables.Where(item => Has("table", QualifiedName(item.Schema, item.Name))).ToList();
        var tableNames = tables.Select(item => QualifiedName(item.Schema, item.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var views = schema.Views.Where(item => Has("view", QualifiedName(item.Schema, item.Name))).ToList();
        var procedures = schema.StoredProcedures.Where(item => Has("procedure", QualifiedName(item.Schema, item.Name))).ToList();
        var functions = schema.Functions.Where(item => Has("function", QualifiedName(item.Schema, item.Name))).ToList();
        var indexes = schema.Indexes.Where(item => Has("index", item.Name) || tableNames.Contains(QualifiedName(item.Schema, item.TableName))).ToList();
        return schema with { Tables = tables, Views = views, StoredProcedures = procedures, Functions = functions, Indexes = indexes };
    }

    private async Task<SqlDocSchema> ReadSqliteSchemaAsync(SqlDocSource source, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(NormalizeSqliteConnectionString(source.ConnectionString));
        await connection.OpenAsync(cancellationToken);
        var databaseName = await ScalarAsync(connection, "SELECT file FROM pragma_database_list WHERE name = 'main'", cancellationToken) ?? source.Name;
        var tableNames = await ValuesAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", cancellationToken);
        var tables = new List<SqlDocTable>();
        var indexes = new List<SqlDocIndex>();

        foreach (var tableName in tableNames)
        {
            var columns = new List<SqlDocColumn>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_xinfo({QuoteSqliteLiteral(tableName)})";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (GetInt(reader, "hidden") != 0) continue;
                    columns.Add(new SqlDocColumn(
                        GetString(reader, "name"),
                        GetString(reader, "type", "TEXT"),
                        GetInt(reader, "notnull") == 0,
                        GetInt(reader, "pk") > 0,
                        GetNullableString(reader, "dflt_value")));
                }
            }

            var foreignKeys = new List<SqlDocForeignKey>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list({QuoteSqliteLiteral(tableName)})";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    foreignKeys.Add(new SqlDocForeignKey(
                        $"FK_{tableName}_{GetInt(reader, "id")}",
                        GetString(reader, "from"),
                        GetString(reader, "table"),
                        GetString(reader, "to")));
                }
            }

            var indexHeaders = new List<(string Name, bool IsUnique)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_list({QuoteSqliteLiteral(tableName)})";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var indexName = GetString(reader, "name");
                    if (indexName.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase)) continue;
                    indexHeaders.Add((indexName, GetInt(reader, "unique") == 1));
                }
            }
            foreach (var index in indexHeaders)
                indexes.Add(new SqlDocIndex("main", tableName, index.Name, index.IsUnique, await ReadSqliteIndexColumnsAsync(connection, index.Name, cancellationToken)));

            tables.Add(new SqlDocTable("main", tableName, columns.OrderByDescending(column => column.IsPrimaryKey).ThenBy(column => column.Name).ToList(), foreignKeys));
        }

        var views = new List<SqlDocView>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'view' ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                views.Add(new SqlDocView("main", reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return new SqlDocSchema(source.Id, source.Name, "SQLite", databaseName, tables, views, [], [], indexes.OrderBy(item => item.TableName).ThenBy(item => item.Name).ToList());
    }

    private static async Task<IReadOnlyList<string>> ReadSqliteIndexColumnsAsync(SqliteConnection connection, string indexName, CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info({QuoteSqliteLiteral(indexName)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(GetString(reader, "name"));
        return columns;
    }

    private async Task<SqlDocSchema> ReadExternalSchemaAsync(SqlDocSource source, CancellationToken cancellationToken)
    {
        await using var connection = CreateProviderConnection(source.Provider, source.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var providerLabel = ProviderLabel(source.Provider);
        var databaseName = await TryScalarAsync(connection, DatabaseNameSql(source.Provider), cancellationToken) ?? source.Name;
        var tableRows = await ReadRowsAsync(connection, InformationSchemaTablesSql(source.Provider), cancellationToken);
        var columnRows = await ReadRowsAsync(connection, InformationSchemaColumnsSql(source.Provider), cancellationToken);
        var primaryKeys = await ReadRowsAsync(connection, InformationSchemaPrimaryKeysSql(source.Provider), cancellationToken);
        var foreignKeyRows = await TryReadRowsAsync(connection, InformationSchemaForeignKeysSql(source.Provider), cancellationToken);
        var viewRows = await ReadRowsAsync(connection, InformationSchemaViewsSql(source.Provider), cancellationToken);
        var routineRows = await TryReadRowsAsync(connection, InformationSchemaRoutinesSql(source.Provider), cancellationToken);
        var indexRows = await TryReadRowsAsync(connection, InformationSchemaIndexesSql(source.Provider), cancellationToken);
        var pkSet = primaryKeys.Select(row => $"{row["schema_name"]}.{row["object_name"]}.{row["column_name"]}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = tableRows.Select(row =>
        {
            var schema = row["schema_name"];
            var name = row["name"];
            var columns = columnRows
                .Where(column => column["schema_name"].Equals(schema, StringComparison.OrdinalIgnoreCase) && column["object_name"].Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(column => new SqlDocColumn(column["name"], column["type_name"], column["nullable"].Equals("YES", StringComparison.OrdinalIgnoreCase), pkSet.Contains($"{schema}.{name}.{column["name"]}"), EmptyToNull(column["default_value"])))
                .ToList();
            var foreignKeys = foreignKeyRows
                .Where(fk => fk["schema_name"].Equals(schema, StringComparison.OrdinalIgnoreCase) && fk["object_name"].Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(fk => new SqlDocForeignKey(fk["name"], fk["column_name"], $"{fk["ref_schema"]}.{fk["ref_table"]}", fk["ref_column"]))
                .ToList();
            return new SqlDocTable(schema, name, columns, foreignKeys);
        }).ToList();

        var views = viewRows.Select(row => new SqlDocView(row["schema_name"], row["name"], EmptyToNull(row["definition"]))).ToList();
        var procedures = routineRows.Where(row => row["routine_type"].Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase)).Select(row => new SqlDocRoutine(row["schema_name"], row["name"], EmptyToNull(row["definition"]))).ToList();
        var functions = routineRows.Where(row => row["routine_type"].Contains("FUNCTION", StringComparison.OrdinalIgnoreCase)).Select(row => new SqlDocRoutine(row["schema_name"], row["name"], EmptyToNull(row["definition"]))).ToList();
        var indexes = indexRows.GroupBy(row => $"{row["schema_name"]}\u001f{row["object_name"]}\u001f{row["name"]}\u001f{row["unique_value"]}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new SqlDocIndex(first["schema_name"], first["object_name"], first["name"], first["unique_value"].Equals("true", StringComparison.OrdinalIgnoreCase) || first["unique_value"] == "1", group.Select(row => row["column_name"]).Where(column => column.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            })
            .OrderBy(item => item.Schema).ThenBy(item => item.TableName).ThenBy(item => item.Name).ToList();

        return new SqlDocSchema(source.Id, source.Name, providerLabel, databaseName, tables, views, procedures, functions, indexes);
    }

    private async Task<SqlDocSchema> ReadExternalSchemaSectionAsync(SqlDocSource source, string section, CancellationToken cancellationToken)
    {
        await using var connection = CreateProviderConnection(source.Provider, source.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var databaseName = await TryScalarAsync(connection, DatabaseNameSql(source.Provider), cancellationToken) ?? source.Name;
        var tables = new List<SqlDocTable>();
        var views = new List<SqlDocView>();
        var procedures = new List<SqlDocRoutine>();
        var functions = new List<SqlDocRoutine>();
        var indexes = new List<SqlDocIndex>();

        if (section == "tables")
        {
            var tableRows = await ReadRowsAsync(connection, InformationSchemaTablesSql(source.Provider), cancellationToken);
            var columnRows = await ReadRowsAsync(connection, InformationSchemaColumnsSql(source.Provider), cancellationToken);
            var primaryKeys = await ReadRowsAsync(connection, InformationSchemaPrimaryKeysSql(source.Provider), cancellationToken);
            var foreignKeyRows = await TryReadRowsAsync(connection, InformationSchemaForeignKeysSql(source.Provider), cancellationToken);
            var pkSet = primaryKeys.Select(row => $"{row["schema_name"]}.{row["object_name"]}.{row["column_name"]}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            tables = tableRows.Select(row =>
            {
                var schema = row["schema_name"];
                var name = row["name"];
                var columns = columnRows.Where(column => column["schema_name"].Equals(schema, StringComparison.OrdinalIgnoreCase) && column["object_name"].Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(column => new SqlDocColumn(column["name"], column["type_name"], column["nullable"].Equals("YES", StringComparison.OrdinalIgnoreCase), pkSet.Contains($"{schema}.{name}.{column["name"]}"), EmptyToNull(column["default_value"]))).ToList();
                var foreignKeys = foreignKeyRows.Where(fk => fk["schema_name"].Equals(schema, StringComparison.OrdinalIgnoreCase) && fk["object_name"].Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(fk => new SqlDocForeignKey(fk["name"], fk["column_name"], $"{fk["ref_schema"]}.{fk["ref_table"]}", fk["ref_column"])).ToList();
                return new SqlDocTable(schema, name, columns, foreignKeys);
            }).ToList();
        }
        else if (section == "views")
        {
            var rows = await ReadRowsAsync(connection, InformationSchemaViewsSql(source.Provider), cancellationToken);
            views = rows.Select(row => new SqlDocView(row["schema_name"], row["name"], EmptyToNull(row["definition"]))).ToList();
        }
        else if (section is "stored-procedures" or "functions")
        {
            var rows = await TryReadRowsAsync(connection, InformationSchemaRoutinesSql(source.Provider), cancellationToken);
            if (section == "stored-procedures") procedures = rows.Where(row => row["routine_type"].Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase)).Select(row => new SqlDocRoutine(row["schema_name"], row["name"], EmptyToNull(row["definition"]))).ToList();
            else functions = rows.Where(row => row["routine_type"].Contains("FUNCTION", StringComparison.OrdinalIgnoreCase)).Select(row => new SqlDocRoutine(row["schema_name"], row["name"], EmptyToNull(row["definition"]))).ToList();
        }
        else if (section == "indexes")
        {
            var rows = await TryReadRowsAsync(connection, InformationSchemaIndexesSql(source.Provider), cancellationToken);
            indexes = rows.GroupBy(row => $"{row["schema_name"]}\u001f{row["object_name"]}\u001f{row["name"]}\u001f{row["unique_value"]}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    return new SqlDocIndex(first["schema_name"], first["object_name"], first["name"], first["unique_value"].Equals("true", StringComparison.OrdinalIgnoreCase) || first["unique_value"] == "1", group.Select(row => row["column_name"]).Where(column => column.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                }).OrderBy(item => item.Schema).ThenBy(item => item.TableName).ThenBy(item => item.Name).ToList();
        }

        return new SqlDocSchema(source.Id, source.Name, ProviderLabel(source.Provider), databaseName, tables, views, procedures, functions, indexes);
    }

    private static (string Tables, string Views, string Procedures, string Functions, string Indexes) OverviewCountSql(string provider) => provider.ToLowerInvariant() switch
    {
        "sqlite" => (
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'",
            "SELECT COUNT(*) FROM sqlite_master WHERE type='view'",
            "SELECT 0", "SELECT 0",
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_autoindex_%'"),
        "sqlserver" => ("SELECT COUNT(*) FROM sys.tables", "SELECT COUNT(*) FROM sys.views", "SELECT COUNT(*) FROM sys.procedures", "SELECT COUNT(*) FROM sys.objects WHERE type IN ('FN','IF','TF','FS','FT')", "SELECT COUNT(*) FROM sys.indexes WHERE name IS NOT NULL"),
        "mysql" => ("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_type='BASE TABLE'", "SELECT COUNT(*) FROM information_schema.views WHERE table_schema=DATABASE()", "SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema=DATABASE() AND routine_type='PROCEDURE'", "SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema=DATABASE() AND routine_type='FUNCTION'", "SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics WHERE table_schema=DATABASE()"),
        "postgresql" => ("SELECT COUNT(*) FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('information_schema','pg_catalog')", "SELECT COUNT(*) FROM information_schema.views WHERE table_schema NOT IN ('information_schema','pg_catalog')", "SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema NOT IN ('information_schema','pg_catalog') AND routine_type='PROCEDURE'", "SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema NOT IN ('information_schema','pg_catalog') AND routine_type='FUNCTION'", "SELECT COUNT(*) FROM pg_indexes WHERE schemaname NOT IN ('pg_catalog','information_schema')"),
        "oracle" => ("SELECT COUNT(*) FROM all_tables WHERE owner NOT IN ('SYS','SYSTEM')", "SELECT COUNT(*) FROM all_views WHERE owner NOT IN ('SYS','SYSTEM')", "SELECT COUNT(*) FROM all_objects WHERE object_type='PROCEDURE' AND owner NOT IN ('SYS','SYSTEM')", "SELECT COUNT(*) FROM all_objects WHERE object_type='FUNCTION' AND owner NOT IN ('SYS','SYSTEM')", "SELECT COUNT(*) FROM all_indexes WHERE owner NOT IN ('SYS','SYSTEM')"),
        _ => ("SELECT 0", "SELECT 0", "SELECT 0", "SELECT 0", "SELECT 0")
    };

    private static async Task<int> CountAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static DbConnection CreateProviderConnection(string provider, string connectionString)
    {
        var typeName = provider.ToLowerInvariant() switch
        {
            "sqlserver" => "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient",
            "mysql" => "MySqlConnector.MySqlConnection, MySqlConnector",
            "postgresql" => "Npgsql.NpgsqlConnection, Npgsql",
            "oracle" => "Oracle.ManagedDataAccess.Client.OracleConnection, Oracle.ManagedDataAccess",
            _ => throw new InvalidOperationException("Unsupported database provider.")
        };
        var type = Type.GetType(typeName, throwOnError: false);
        if (type is null)
            throw new InvalidOperationException($"The {ProviderLabel(provider)} driver is not installed. Add the matching NuGet provider package, then restore the project.");
        return (DbConnection)(Activator.CreateInstance(type, connectionString) ?? throw new InvalidOperationException($"Unable to create a {ProviderLabel(provider)} connection."));
    }

    private static string DatabaseNameSql(string provider) => provider.ToLowerInvariant() switch
    {
        "sqlserver" => "SELECT DB_NAME()",
        "mysql" => "SELECT DATABASE()",
        "postgresql" => "SELECT current_database()",
        "oracle" => "SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM dual",
        _ => "SELECT ''"
    };

    private static string DatabaseForDiscovery(string provider, string? selectedDatabase) => provider.ToLowerInvariant() switch
    {
        "sqlserver" => "master",
        "postgresql" => string.IsNullOrWhiteSpace(selectedDatabase) ? "postgres" : selectedDatabase,
        _ => selectedDatabase ?? string.Empty
    };

    private static string DatabaseListSql(string provider) => provider.ToLowerInvariant() switch
    {
        "sqlserver" => "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name",
        "mysql" => "SHOW DATABASES",
        "postgresql" => "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname",
        "oracle" => "SELECT username FROM all_users ORDER BY username",
        _ => "SELECT ''"
    };

    private static string InformationSchemaTablesSql(string provider) => provider.Equals("oracle", StringComparison.OrdinalIgnoreCase)
        ? "SELECT owner AS schema_name, table_name AS name FROM all_tables WHERE owner NOT IN ('SYS','SYSTEM') ORDER BY owner, table_name"
        : "SELECT table_schema AS schema_name, table_name AS name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('information_schema','pg_catalog') ORDER BY table_schema, table_name";

    private static string InformationSchemaColumnsSql(string provider) => provider.Equals("oracle", StringComparison.OrdinalIgnoreCase)
        ? "SELECT owner AS schema_name, table_name AS object_name, column_name AS name, data_type AS type_name, nullable AS nullable, data_default AS default_value FROM all_tab_columns WHERE owner NOT IN ('SYS','SYSTEM') ORDER BY owner, table_name, column_id"
        : "SELECT table_schema AS schema_name, table_name AS object_name, column_name AS name, data_type AS type_name, is_nullable AS nullable, column_default AS default_value FROM information_schema.columns WHERE table_schema NOT IN ('information_schema','pg_catalog') ORDER BY table_schema, table_name, ordinal_position";

    private static string InformationSchemaPrimaryKeysSql(string provider) => provider.Equals("oracle", StringComparison.OrdinalIgnoreCase)
        ? "SELECT cons.owner AS schema_name, cols.table_name AS object_name, cols.column_name AS column_name FROM all_constraints cons JOIN all_cons_columns cols ON cons.owner = cols.owner AND cons.constraint_name = cols.constraint_name WHERE cons.constraint_type = 'P'"
        : "SELECT ku.table_schema AS schema_name, ku.table_name AS object_name, ku.column_name AS column_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage ku ON tc.constraint_name = ku.constraint_name AND tc.table_schema = ku.table_schema AND tc.table_name = ku.table_name WHERE tc.constraint_type = 'PRIMARY KEY'";

    private static string InformationSchemaForeignKeysSql(string provider) => provider.Equals("oracle", StringComparison.OrdinalIgnoreCase)
        ? "SELECT cons.owner AS schema_name, child.table_name AS object_name, cons.constraint_name AS name, child.column_name AS column_name, parent.owner AS ref_schema, parent.table_name AS ref_table, parent.column_name AS ref_column FROM all_constraints cons JOIN all_cons_columns child ON cons.owner = child.owner AND cons.constraint_name = child.constraint_name JOIN all_cons_columns parent ON cons.r_owner = parent.owner AND cons.r_constraint_name = parent.constraint_name AND child.position = parent.position WHERE cons.constraint_type = 'R'"
        : "SELECT tc.table_schema AS schema_name, tc.table_name AS object_name, tc.constraint_name AS name, kcu.column_name AS column_name, ccu.table_schema AS ref_schema, ccu.table_name AS ref_table, ccu.column_name AS ref_column FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema WHERE tc.constraint_type = 'FOREIGN KEY'";

    private static string InformationSchemaViewsSql(string provider) => provider.Equals("oracle", StringComparison.OrdinalIgnoreCase)
        ? "SELECT owner AS schema_name, view_name AS name, text AS definition FROM all_views WHERE owner NOT IN ('SYS','SYSTEM') ORDER BY owner, view_name"
        : "SELECT table_schema AS schema_name, table_name AS name, view_definition AS definition FROM information_schema.views WHERE table_schema NOT IN ('information_schema','pg_catalog') ORDER BY table_schema, table_name";

    private static string InformationSchemaRoutinesSql(string provider) => provider.Equals("oracle", StringComparison.OrdinalIgnoreCase)
        ? "SELECT owner AS schema_name, object_name AS name, object_type AS routine_type, NULL AS definition FROM all_objects WHERE object_type IN ('PROCEDURE','FUNCTION') AND owner NOT IN ('SYS','SYSTEM') ORDER BY owner, object_name"
        : "SELECT routine_schema AS schema_name, routine_name AS name, routine_type AS routine_type, routine_definition AS definition FROM information_schema.routines WHERE routine_schema NOT IN ('information_schema','pg_catalog') ORDER BY routine_schema, routine_name";

    private static string InformationSchemaIndexesSql(string provider) => provider.ToLowerInvariant() switch
    {
        "sqlserver" => "SELECT SCHEMA_NAME(t.schema_id) AS schema_name, t.name AS object_name, i.name AS name, i.is_unique AS unique_value, c.name AS column_name FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id WHERE i.name IS NOT NULL ORDER BY SCHEMA_NAME(t.schema_id), t.name, i.name, ic.key_ordinal",
        "postgresql" => "SELECT schemaname AS schema_name, tablename AS object_name, indexname AS name, false AS unique_value, '' AS column_name FROM pg_indexes WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY schemaname, tablename, indexname",
        "mysql" => "SELECT table_schema AS schema_name, table_name AS object_name, index_name AS name, CASE WHEN non_unique = 0 THEN 1 ELSE 0 END AS unique_value, column_name AS column_name FROM information_schema.statistics WHERE table_schema NOT IN ('information_schema','mysql','performance_schema','sys') ORDER BY table_schema, table_name, index_name, seq_in_index",
        "oracle" => "SELECT ind.owner AS schema_name, ind.table_name AS object_name, ind.index_name AS name, CASE WHEN ind.uniqueness = 'UNIQUE' THEN 1 ELSE 0 END AS unique_value, col.column_name AS column_name FROM all_indexes ind LEFT JOIN all_ind_columns col ON ind.owner = col.index_owner AND ind.index_name = col.index_name WHERE ind.owner NOT IN ('SYS','SYSTEM') ORDER BY ind.owner, ind.table_name, ind.index_name, col.column_position",
        _ => "SELECT '' AS schema_name, '' AS object_name, '' AS name, 0 AS unique_value, '' AS column_name"
    };

    private async Task<List<Dictionary<string, string>>> ReadRowsAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, string>>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                row[reader.GetName(index)] = reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
            rows.Add(row);
        }
        return rows;
    }

    private async Task<List<Dictionary<string, string>>> TryReadRowsAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        try { return await ReadRowsAsync(connection, sql, cancellationToken); }
        catch (DbException) { return []; }
    }

    private async Task<string?> TryScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        try { return await ScalarAsync(connection, sql, cancellationToken); }
        catch (DbException) { return null; }
    }

    private static async Task<string?> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> ValuesAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(0)) values.Add(reader.GetString(0));
        return values;
    }

    private string NormalizeSqliteConnectionString(string connectionString)
    {
        if (connectionString.Contains('=', StringComparison.Ordinal)) return connectionString;
        var path = Path.IsPathRooted(connectionString) ? connectionString : Path.Combine(environment.ContentRootPath, connectionString);
        return new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    private static string BuildConnectionString(string? providerValue, string? connectionString, string? server, string? port, string? database, string? username, string? password, bool integratedSecurity, bool trustServerCertificate, string? sqlitePath)
    {
        var provider = ValidateProvider(providerValue);
        if (!string.IsNullOrWhiteSpace(connectionString) && string.IsNullOrWhiteSpace(server) && string.IsNullOrWhiteSpace(sqlitePath))
            return ValidateConnectionString(connectionString);
        if (provider == "sqlite")
        {
            var dataSource = string.IsNullOrWhiteSpace(sqlitePath) ? connectionString : sqlitePath;
            if (string.IsNullOrWhiteSpace(dataSource)) throw new ArgumentException("Choose a SQLite database file.");
            return new SqliteConnectionStringBuilder { DataSource = dataSource.Trim() }.ToString();
        }
        if (string.IsNullOrWhiteSpace(server)) throw new ArgumentException("Server name is required.");

        var endpoint = string.IsNullOrWhiteSpace(port) ? server.Trim() : provider == "sqlserver" ? $"{server.Trim()},{port.Trim()}" : server.Trim();
        var builder = new DbConnectionStringBuilder();
        switch (provider)
        {
            case "sqlserver":
                builder["Server"] = endpoint;
                if (!string.IsNullOrWhiteSpace(database)) builder["Database"] = database.Trim();
                if (integratedSecurity) builder["Integrated Security"] = "True";
                else
                {
                    builder["User ID"] = RequireUsername(username);
                    builder["Password"] = password ?? string.Empty;
                }
                builder["Trust Server Certificate"] = trustServerCertificate ? "True" : "False";
                builder["Encrypt"] = "True";
                break;
            case "mysql":
                builder["Server"] = endpoint;
                if (!string.IsNullOrWhiteSpace(port)) builder["Port"] = port.Trim();
                if (!string.IsNullOrWhiteSpace(database)) builder["Database"] = database.Trim();
                builder["User ID"] = RequireUsername(username);
                builder["Password"] = password ?? string.Empty;
                break;
            case "postgresql":
                builder["Host"] = endpoint;
                if (!string.IsNullOrWhiteSpace(port)) builder["Port"] = port.Trim();
                if (!string.IsNullOrWhiteSpace(database)) builder["Database"] = database.Trim();
                builder["Username"] = RequireUsername(username);
                builder["Password"] = password ?? string.Empty;
                break;
            case "oracle":
                builder["Data Source"] = string.IsNullOrWhiteSpace(database) ? endpoint : $"{endpoint}/{database.Trim()}";
                builder["User Id"] = RequireUsername(username);
                builder["Password"] = password ?? string.Empty;
                break;
        }
        return ValidateConnectionString(builder.ConnectionString);
    }

    private static string RequireUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username is required unless integrated security is enabled.");
        return username.Trim();
    }

    private static byte[] BuildDocx(SqlDocSchema schema, SqlDocGenerateRequest options)
    {
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "word/document.xml", BuildDocumentXml(schema, options));
            WriteEntry(archive, "word/styles.xml", BuildStylesXml());
            WriteEntry(archive, "word/_rels/document.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
        }
        return package.ToArray();
    }

    private static string BuildDocumentXml(SqlDocSchema schema, SqlDocGenerateRequest options)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""");
        Heading(xml, $"{schema.SourceName} Database Documentation", 1);
        Paragraph(xml, $"Provider: {schema.Provider}");
        Paragraph(xml, $"Database: {schema.DatabaseName}");
        Paragraph(xml, $"Generated: {DateTimeOffset.Now:g}");
        AppendTableOfContents(xml, schema, options);
        Heading(xml, "Summary", 2);
        Table(xml, ["Object type", "Count"], [
            ["Tables", schema.Tables.Count.ToString(CultureInfo.InvariantCulture)],
            ["Views", schema.Views.Count.ToString(CultureInfo.InvariantCulture)],
            ["Stored procedures", schema.StoredProcedures.Count.ToString(CultureInfo.InvariantCulture)],
            ["Functions", schema.Functions.Count.ToString(CultureInfo.InvariantCulture)],
            ["Indexes", schema.Indexes.Count.ToString(CultureInfo.InvariantCulture)]
        ]);
        if (options.Tables && schema.Tables.Count > 0) AppendTables(xml, schema, options);
        if (options.Views && schema.Views.Count > 0) AppendViews(xml, schema.Views);
        if (options.StoredProcedures && schema.StoredProcedures.Count > 0) AppendRoutines(xml, "Stored Procedures", schema.StoredProcedures);
        if (options.Functions && schema.Functions.Count > 0) AppendRoutines(xml, "Functions", schema.Functions);
        if (options.Indexes && schema.Indexes.Count > 0) AppendIndexes(xml, schema.Indexes);
        xml.Append("""<w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="900" w:right="900" w:bottom="900" w:left="900" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr></w:body></w:document>""");
        return xml.ToString();
    }

    private static string BuildStylesXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos"/><w:noProof/><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/><w:rPr><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="10"/><w:qFormat/><w:pPr><w:spacing w:before="0" w:after="220"/><w:keepNext/></w:pPr><w:rPr><w:b/><w:color w:val="0B5E57"/><w:sz w:val="42"/><w:szCs w:val="42"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="260" w:after="150"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:color w:val="172033"/><w:sz w:val="34"/><w:szCs w:val="34"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="360" w:after="120"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:color w:val="344054"/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="80"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:color w:val="475467"/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:customStyle="1" w:styleId="DevToolsTableHeader"><w:name w:val="DevTools Table Header"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:b/><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:customStyle="1" w:styleId="DevToolsTableBody"><w:name w:val="DevTools Table Body"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:customStyle="1" w:styleId="DevToolsCode"><w:name w:val="DevTools SQL Code"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:before="0" w:after="0" w:line="220" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr></w:style>
        </w:styles>
        """;

    private static void AppendTables(StringBuilder xml, SqlDocSchema schema, SqlDocGenerateRequest options)
    {
        if (schema.Tables.Count == 0) return;

        Heading(xml, "Tables", 2, pageBreakBefore: true);
        foreach (var table in schema.Tables)
        {
            Heading(xml, QualifiedName(table.Schema, table.Name), 3);
            Table(xml, ["Column", "Type", "Nullable", "PK", "Default"], table.Columns.Select(column => new[]
            {
                column.Name,
                column.DataType,
                column.IsNullable ? "Yes" : "No",
                column.IsPrimaryKey ? "Yes" : "No",
                column.DefaultValue ?? string.Empty
            }), ["left", "center", "center", "center", "center"]);
            if (!options.Relationships || table.ForeignKeys.Count == 0) continue;
            Paragraph(xml, "Foreign keys", before: 180, after: 90, styleId: "Heading3");
            Table(xml, ["Name", "Column", "References"], table.ForeignKeys.Select(fk => new[] { fk.Name, fk.ColumnName, $"{fk.ReferencedTable}.{fk.ReferencedColumn}" }));
            Paragraph(xml, string.Empty, after: 180);
        }
    }

    private static void AppendTableOfContents(StringBuilder xml, SqlDocSchema schema, SqlDocGenerateRequest options)
    {
        Heading(xml, "Table of Contents", 2);
        var rows = new List<IReadOnlyList<string>> { new[] { "Summary", "Overview counts" } };
        if (options.Tables && schema.Tables.Count > 0) rows.AddRange(schema.Tables.Select(table => new[] { "Table", QualifiedName(table.Schema, table.Name) }));
        if (options.Views && schema.Views.Count > 0) rows.AddRange(schema.Views.Select(view => new[] { "View", QualifiedName(view.Schema, view.Name) }));
        if (options.StoredProcedures && schema.StoredProcedures.Count > 0) rows.AddRange(schema.StoredProcedures.Select(routine => new[] { "Stored procedure", QualifiedName(routine.Schema, routine.Name) }));
        if (options.Functions && schema.Functions.Count > 0) rows.AddRange(schema.Functions.Select(routine => new[] { "Function", QualifiedName(routine.Schema, routine.Name) }));
        if (options.Indexes && schema.Indexes.Count > 0) rows.Add(new[] { "Indexes", schema.Indexes.Count.ToString(CultureInfo.InvariantCulture) });
        Table(xml, ["Section", "Object"], rows);
    }

    private static void AppendViews(StringBuilder xml, IReadOnlyList<SqlDocView> views)
    {
        if (views.Count == 0) return;

        Heading(xml, "Views", 2, pageBreakBefore: true);
        foreach (var view in views)
        {
            Heading(xml, QualifiedName(view.Schema, view.Name), 3);
            CodeBlock(xml, FormatSqlDefinition(view.Definition));
            Paragraph(xml, string.Empty, after: 220);
        }
    }

    private static void AppendRoutines(StringBuilder xml, string heading, IReadOnlyList<SqlDocRoutine> routines)
    {
        if (routines.Count == 0) return;

        Heading(xml, heading, 2, pageBreakBefore: true);
        foreach (var routine in routines)
        {
            Heading(xml, QualifiedName(routine.Schema, routine.Name), 3);
            CodeBlock(xml, FormatSqlDefinition(routine.Definition));
            Paragraph(xml, string.Empty, after: 220);
        }
    }

    private static void AppendIndexes(StringBuilder xml, IReadOnlyList<SqlDocIndex> indexes)
    {
        if (indexes.Count == 0) return;

        Heading(xml, "Indexes", 2, pageBreakBefore: true);
        Table(xml, ["Table", "Index", "Unique", "Columns"], indexes.Select(index => new[]
        {
            QualifiedName(index.Schema, index.TableName),
            index.Name,
            index.IsUnique ? "Yes" : "No",
            string.Join(", ", index.Columns)
        }));
    }

    private static void Heading(StringBuilder xml, string text, int level, bool pageBreakBefore = false)
    {
        var styleId = level == 1 ? "Title" : level == 2 ? "Heading1" : "Heading2";
        var pageBreak = pageBreakBefore ? "<w:pageBreakBefore/>" : string.Empty;
        xml.Append($"""<w:p><w:pPr><w:pStyle w:val="{styleId}"/>{pageBreak}</w:pPr><w:r><w:rPr><w:noProof/></w:rPr><w:t>{Escape(text)}</w:t></w:r></w:p>""");
    }

    private static void Paragraph(StringBuilder xml, string text, int before = 0, int after = 0, string? styleId = null)
    {
        var style = string.IsNullOrWhiteSpace(styleId) ? string.Empty : $"<w:pStyle w:val=\"{styleId}\"/>";
        xml.Append($"""<w:p><w:pPr>{style}<w:spacing w:before="{before}" w:after="{after}"/></w:pPr><w:r><w:rPr><w:noProof/></w:rPr><w:t xml:space="preserve">{Escape(text)}</w:t></w:r></w:p>""");
    }

    private static void Table(StringBuilder xml, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, IReadOnlyList<string>? columnAlignments = null)
    {
        xml.Append("""<w:tbl><w:tblPr><w:tblStyle w:val="TableGrid"/><w:tblW w:w="0" w:type="auto"/><w:tblCellMar><w:top w:w="90" w:type="dxa"/><w:left w:w="110" w:type="dxa"/><w:bottom w:w="90" w:type="dxa"/><w:right w:w="110" w:type="dxa"/></w:tblCellMar><w:tblBorders><w:top w:val="single" w:sz="4" w:space="0" w:color="D0D5DD"/><w:left w:val="single" w:sz="4" w:space="0" w:color="D0D5DD"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="D0D5DD"/><w:right w:val="single" w:sz="4" w:space="0" w:color="D0D5DD"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="D0D5DD"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="D0D5DD"/></w:tblBorders></w:tblPr>""");
        Row(xml, headers, header: true, columnAlignments);
        foreach (var row in rows) Row(xml, row, header: false, columnAlignments);
        xml.Append("</w:tbl>");
    }

    private static void Row(StringBuilder xml, IReadOnlyList<string> values, bool header, IReadOnlyList<string>? columnAlignments)
    {
        xml.Append("<w:tr>");
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var alignment = header ? "center" : ColumnAlignment(columnAlignments, index);
            xml.Append("<w:tc><w:tcPr><w:tcW w:w=\"2400\" w:type=\"dxa\"/>");
            if (header) xml.Append("<w:shd w:fill=\"EEF2FF\"/>");
            var styleId = header ? "DevToolsTableHeader" : "DevToolsTableBody";
            xml.Append($"""<w:vAlign w:val="center"/></w:tcPr><w:p><w:pPr><w:pStyle w:val="{styleId}"/><w:jc w:val="{alignment}"/></w:pPr><w:r><w:rPr><w:noProof/></w:rPr>""");
            xml.Append($"""<w:t xml:space="preserve">{Escape(value)}</w:t></w:r></w:p></w:tc>""");
        }
        xml.Append("</w:tr>");
    }

    private static void CodeBlock(StringBuilder xml, string code)
    {
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        xml.Append("""<w:tbl><w:tblPr><w:tblW w:w="5000" w:type="pct"/><w:tblLayout w:type="fixed"/><w:tblCellMar><w:top w:w="140" w:type="dxa"/><w:left w:w="160" w:type="dxa"/><w:bottom w:w="140" w:type="dxa"/><w:right w:w="160" w:type="dxa"/></w:tblCellMar><w:tblBorders><w:top w:val="single" w:sz="6" w:space="0" w:color="D0D5DD"/><w:left w:val="single" w:sz="6" w:space="0" w:color="D0D5DD"/><w:bottom w:val="single" w:sz="6" w:space="0" w:color="D0D5DD"/><w:right w:val="single" w:sz="6" w:space="0" w:color="D0D5DD"/></w:tblBorders></w:tblPr><w:tr><w:tc><w:tcPr><w:tcW w:w="5000" w:type="pct"/><w:shd w:fill="F2F4F7"/><w:vAlign w:val="center"/></w:tcPr>""");
        foreach (var line in lines)
        {
            AppendCodeLine(xml, line);
        }

        xml.Append("</w:tc></w:tr></w:tbl>");
    }

    private static void AppendCodeLine(StringBuilder xml, string line)
    {
        xml.Append("""<w:p><w:pPr><w:pStyle w:val="DevToolsCode"/></w:pPr>""");
        if (line.Length == 0)
        {
            AppendCodeRun(xml, " ");
        }
        else
        {
            foreach (Match match in Regex.Matches(line, @"(--.*$)|(/\*.*?\*/)|(N?'(?:''|[^'])*')|(\[[^\]]+\])|(@[A-Za-z_][A-Za-z0-9_@$#]*)|(\b\d+(?:\.\d+)?\b)|(\b[A-Za-z_][A-Za-z0-9_@$#]*\b)|(\s+)|(.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
            {
                AppendSqlToken(xml, match.Value);
            }
        }

        xml.Append("</w:p>");
    }

    private static void AppendSqlToken(StringBuilder xml, string token)
    {
        if (token.StartsWith("--", StringComparison.Ordinal) || token.StartsWith("/*", StringComparison.Ordinal))
        {
            AppendCodeRun(xml, token, "008000");
        }
        else if (token.StartsWith("'", StringComparison.Ordinal) || token.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
        {
            AppendCodeRun(xml, token, "A31515");
        }
        else if (token.StartsWith('@'))
        {
            AppendCodeRun(xml, token, "267F99");
        }
        else if (char.IsDigit(token[0]))
        {
            AppendCodeRun(xml, token, "098658");
        }
        else if (SqlSyntaxKeywords.Contains(token))
        {
            AppendCodeRun(xml, token, "0000FF", bold: true);
        }
        else
        {
            AppendCodeRun(xml, token);
        }
    }

    private static void AppendCodeRun(StringBuilder xml, string text, string color = "172033", bool bold = false)
    {
        var boldXml = bold ? "<w:b/>" : string.Empty;
        xml.Append($"""<w:r><w:rPr><w:noProof/>{boldXml}<w:color w:val="{color}"/></w:rPr><w:t xml:space="preserve">{Escape(text)}</w:t></w:r>""");
    }

    private static string FormatSqlDefinition(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return "No definition returned by provider.";

        var sql = definition.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (sql.Contains('\n')) return sql;

        sql = Regex.Replace(sql, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

        var replacements = new (string Pattern, string Replacement)[]
        {
            (@"\b(CREATE|ALTER)\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b", "$1 $2"),
            (@"\s+\bAS\s+\bBEGIN\b", "\nAS\nBEGIN"),
            (@"\s+\bAS\s+", "\nAS\n"),
            (@"\s+\bBEGIN\b", "\nBEGIN"),
            (@"\s+\bEND\b", "\nEND"),
            (@"\s+\bSET\s+", "\n    SET "),
            (@"\s+\bSELECT\s+", "\n    SELECT\n        "),
            (@"\s+\bFROM\s+", "\n    FROM "),
            (@"\s+\b(INNER|LEFT|RIGHT|FULL|CROSS)\s+JOIN\s+", "\n    $1 JOIN "),
            (@"\s+\bJOIN\s+", "\n    JOIN "),
            (@"\s+\bWHERE\s+", "\n    WHERE "),
            (@"\s+\bAND\s+", "\n        AND "),
            (@"\s+\bOR\s+", "\n        OR "),
            (@"\s+\bGROUP\s+BY\s+", "\n    GROUP BY "),
            (@"\s+\bORDER\s+BY\s+", "\n    ORDER BY "),
            (@"\s+\bHAVING\s+", "\n    HAVING "),
            (@"\s+\bUNION\s+ALL\s+", "\nUNION ALL\n"),
            (@"\s+\bUNION\s+", "\nUNION\n")
        };

        foreach (var (pattern, replacement) in replacements)
        {
            sql = Regex.Replace(sql, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }

        sql = Regex.Replace(sql, @",\s+", ",\n        ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        return sql.Trim();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }

    private static string ValidateName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length is < 2 or > 120) throw new ArgumentException("Source name must be between 2 and 120 characters.");
        return name;
    }

    private static string ValidateProvider(string? value)
    {
        var provider = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (!Providers.Contains(provider)) throw new ArgumentException("Choose SQL Server, SQLite, MySQL, Oracle, or PostgreSQL.");
        return provider;
    }

    private static string ValidateConnectionString(string? value)
    {
        var connectionString = (value ?? string.Empty).Trim();
        if (connectionString.Length is < 2 or > 4000) throw new ArgumentException("Connection string must be between 2 and 4000 characters.");
        return connectionString;
    }

    private static string ValidateDiagramName(string? value)
    {
        var name = (value ?? "Default diagram").Trim();
        if (name.Length is < 2 or > 160) throw new ArgumentException("Diagram name must be between 2 and 160 characters.");
        return name;
    }

    private static string ValidateDiagramJson(string? value)
    {
        var json = (value ?? string.Empty).Trim();
        if (json.Length < 2) throw new ArgumentException("Diagram is empty.");
        if (json.Length > 2_000_000) throw new ArgumentException("Diagram is too large to save.");
        try { using var _ = System.Text.Json.JsonDocument.Parse(json); }
        catch (System.Text.Json.JsonException) { throw new ArgumentException("Diagram data is not valid JSON."); }
        return json;
    }

    private static SqlDocSourceDto ToDto(SqlDocSource source) => new(
        source.Id,
        source.Name,
        source.Provider,
        source.ConnectionString,
        new DateTimeOffset(DateTime.SpecifyKind(source.CreatedAtUtc, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(source.UpdatedAtUtc, DateTimeKind.Utc)));

    private static SqlDocDiagramDto ToDto(SqlDocDiagram diagram) => new(
        diagram.Id,
        diagram.SqlDocSourceId,
        diagram.Name,
        diagram.DiagramJson,
        new DateTimeOffset(DateTime.SpecifyKind(diagram.CreatedAtUtc, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(diagram.UpdatedAtUtc, DateTimeKind.Utc)));

    private static string ProviderLabel(string provider) => provider.ToLowerInvariant() switch
    {
        "sqlserver" => "SQL Server",
        "sqlite" => "SQLite",
        "mysql" => "MySQL",
        "oracle" => "Oracle",
        "postgresql" => "PostgreSQL",
        _ => provider
    };

    private static string QuoteSqliteLiteral(string value) => $"'{value.Replace("'", "''")}'";
    private static string QualifiedName(string schema, string name) => string.IsNullOrWhiteSpace(schema) ? name : $"{schema}.{name}";
    private static string ColumnAlignment(IReadOnlyList<string>? alignments, int index) => alignments is not null && index < alignments.Count ? alignments[index] : "left";
    private static string SafeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var fileName = new string(value.Select(character => invalidCharacters.Contains(character) ? '-' : character).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(fileName) ? "database" : fileName;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static string GetString(DbDataReader reader, string name, string fallback = "") => reader.IsDBNull(reader.GetOrdinal(name)) ? fallback : reader.GetString(reader.GetOrdinal(name));
    private static string? GetNullableString(DbDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToString(reader.GetValue(reader.GetOrdinal(name)), CultureInfo.InvariantCulture);
    private static int GetInt(DbDataReader reader, string name) => Convert.ToInt32(reader.GetValue(reader.GetOrdinal(name)), CultureInfo.InvariantCulture);
}
