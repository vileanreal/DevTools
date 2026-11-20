using System.Text;
using System.Text.RegularExpressions;
using DevTools.Application.Services;
using DevTools.Shared.Models;

namespace DevTools.Web.Services;

public interface IDatabaseCompareService
{
    Task<DatabaseCompareResult> CompareAsync(DatabaseCompareRequest request, CancellationToken cancellationToken);
    Task<DatabaseCompareApplyResult> ApplyAsync(DatabaseCompareApplyRequest request, CancellationToken cancellationToken);
}

public sealed class DatabaseCompareService(ISqlDocService sqlDocService, IDatabaseSchemaDiffService schemaDiff) : IDatabaseCompareService
{
    private sealed record SchemaObject(string Key, string Type, string Name, string Definition, object Model);
    private sealed record TableModel(SqlDocTable Table, IReadOnlyList<SqlDocIndex> Indexes);
    private sealed record PlannedObject(DatabaseCompareObject Result, IReadOnlyList<string> Commands);
    private sealed record ComparePlan(DatabaseCompareResult Result, IReadOnlyDictionary<string, PlannedObject> Objects);

    public async Task<DatabaseCompareResult> CompareAsync(DatabaseCompareRequest request, CancellationToken cancellationToken) =>
        (await BuildPlanAsync(request.LeftSourceId, request.RightSourceId, cancellationToken)).Result;

    public async Task<DatabaseCompareApplyResult> ApplyAsync(DatabaseCompareApplyRequest request, CancellationToken cancellationToken)
    {
        var plan = await BuildPlanAsync(request.LeftSourceId, request.RightSourceId, cancellationToken);
        var selectedKeys = request.ObjectKeys!.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var selected = selectedKeys.Select(key => plan.Objects.TryGetValue(key, out var item)
            ? item
            : throw new InvalidOperationException($"Schema object '{key}' is no longer present in the comparison.")).ToList();
        var blocked = selected.FirstOrDefault(item => !item.Result.CanApply);
        if (blocked is not null) throw new InvalidOperationException($"{blocked.Result.Name} cannot be applied automatically. Review its generated SQL and note.");
        var commands = selected.SelectMany(item => item.Commands).ToList();
        var count = await sqlDocService.ExecuteCommandsAsync(request.RightSourceId, commands, cancellationToken);
        return new DatabaseCompareApplyResult(selected.Count, count, $"Applied {selected.Count} schema change{(selected.Count == 1 ? string.Empty : "s")} to {plan.Result.RightSourceName}.");
    }

    private async Task<ComparePlan> BuildPlanAsync(int leftSourceId, int rightSourceId, CancellationToken cancellationToken)
    {
        var left = await sqlDocService.InspectAsync(leftSourceId, cancellationToken)
            ?? throw new KeyNotFoundException("Left database connection not found.");
        var right = await sqlDocService.InspectAsync(rightSourceId, cancellationToken)
            ?? throw new KeyNotFoundException("Right database connection not found.");
        var targetProvider = ProviderKey(right.Provider);
        var sameProvider = ProviderKey(left.Provider) == targetProvider;
        var leftObjects = BuildObjects(left, targetProvider);
        var rightObjects = BuildObjects(right, targetProvider);
        var keys = leftObjects.Keys.Union(rightObjects.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(KeyOrder).ThenBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
        var planned = new Dictionary<string, PlannedObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            leftObjects.TryGetValue(key, out var leftObject);
            rightObjects.TryGetValue(key, out var rightObject);
            var status = schemaDiff.Classify(leftObject?.Definition, rightObject?.Definition);
            var (commands, note) = PlanCommands(leftObject, rightObject, targetProvider, sameProvider);
            var canApply = status != "Unchanged" && commands.Count > 0 && note is null;
            var type = leftObject?.Type ?? rightObject!.Type;
            var name = leftObject?.Name ?? rightObject!.Name;
            var script = commands.Count == 0
                ? status == "Unchanged" ? string.Empty : $"-- Manual review required for {type} {name}."
                : string.Join("\n\n", commands.Select(Terminate));
            var result = new DatabaseCompareObject(key, type, name, status, leftObject?.Definition ?? string.Empty, rightObject?.Definition ?? string.Empty, script, canApply, note);
            planned[key] = new PlannedObject(result, commands);
        }
        var changed = planned.Values.Where(item => item.Result.Status != "Unchanged").ToList();
        var scriptBuilder = new StringBuilder();
        scriptBuilder.AppendLine($"-- Update {right.SourceName} to match {left.SourceName}");
        scriptBuilder.AppendLine($"-- Target provider: {right.Provider}");
        scriptBuilder.AppendLine($"-- Generated: {DateTimeOffset.UtcNow:O}");
        foreach (var item in changed)
        {
            scriptBuilder.AppendLine();
            scriptBuilder.AppendLine($"-- {item.Result.Status}: {item.Result.Type} {item.Result.Name}");
            if (!string.IsNullOrWhiteSpace(item.Result.Note)) scriptBuilder.AppendLine($"-- NOTE: {item.Result.Note}");
            scriptBuilder.AppendLine(item.Result.UpdateScript);
        }
        var warning = sameProvider ? null : "The databases use different providers. Definitions are normalized for comparison, but automatic updates are disabled; review and run the generated target SQL manually.";
        if (!sameProvider)
        {
            foreach (var key in planned.Keys.ToList())
            {
                var item = planned[key];
                if (item.Result.Status != "Unchanged") planned[key] = item with { Result = item.Result with { CanApply = false, Note = warning } };
            }
        }
        var publicObjects = planned.Values.Select(item => item.Result).ToList();
        var resultPage = new DatabaseCompareResult(left.SourceId, left.SourceName, right.SourceId, right.SourceName, right.Provider, publicObjects, scriptBuilder.ToString().TrimEnd(), warning);
        return new ComparePlan(resultPage, planned);
    }

    private static Dictionary<string, SchemaObject> BuildObjects(SqlDocSchema schema, string targetProvider)
    {
        var objects = new Dictionary<string, SchemaObject>(StringComparer.OrdinalIgnoreCase);
        var indexesByTable = schema.Indexes
            .GroupBy(index => QualifiedName(index.Schema, index.TableName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in schema.Tables)
        {
            var name = QualifiedName(table.Schema, table.Name);
            tableNames.Add(name);
            var indexes = indexesByTable.TryGetValue(name, out var tableIndexes) ? tableIndexes : [];
            var model = new TableModel(table, indexes);
            Add(new SchemaObject($"table:{name}", "Table", name, TableDefinition(model, targetProvider), model));
        }
        foreach (var view in schema.Views)
        {
            var name = QualifiedName(view.Schema, view.Name);
            Add(new SchemaObject($"view:{name}", "View", name, CreateView(view, targetProvider), view));
        }
        foreach (var routine in schema.StoredProcedures)
        {
            var name = QualifiedName(routine.Schema, routine.Name);
            Add(new SchemaObject($"procedure:{name}", "Stored procedure", name, DefinitionOrPlaceholder(routine.Definition, "PROCEDURE", name), routine));
        }
        foreach (var routine in schema.Functions)
        {
            var name = QualifiedName(routine.Schema, routine.Name);
            Add(new SchemaObject($"function:{name}", "Function", name, DefinitionOrPlaceholder(routine.Definition, "FUNCTION", name), routine));
        }
        // Indexes belong to their table's object; only an index whose table was not inspected is listed on its own.
        foreach (var index in schema.Indexes.Where(index => !tableNames.Contains(QualifiedName(index.Schema, index.TableName))))
        {
            var name = $"{QualifiedName(index.Schema, index.TableName)}.{index.Name}";
            Add(new SchemaObject($"index:{name}", "Index", name, CreateIndex(index, targetProvider), index));
        }
        return objects;
        void Add(SchemaObject item) => objects[item.Key] = item;
    }

    private (IReadOnlyList<string> Commands, string? Note) PlanCommands(SchemaObject? left, SchemaObject? right, string provider, bool sameProvider)
    {
        if (left is null && right is null) return ([], null);
        if (left is not null && right is not null && schemaDiff.Equivalent(left.Definition, right.Definition)) return ([], null);
        if (!sameProvider) return ([], "Cross-provider changes require manual review.");
        if (right is null && left!.Model is TableModel newTable)
            return ([CreateTable(newTable.Table, provider), .. newTable.Indexes.Select(index => CreateIndex(index, provider))], null);
        if (right is null) return left!.Definition.StartsWith("--", StringComparison.Ordinal) ? ([], "The source definition is unavailable.") : ([left.Definition], null);
        if (left is null) return ([Drop(right, provider)], null);
        if (left.Model is TableModel leftTable && right.Model is TableModel rightTable) return PlanTableChanges(leftTable, rightTable, provider);
        if (left.Type is "Stored procedure" or "Function" && (left.Definition.StartsWith("--", StringComparison.Ordinal) || provider == "postgresql"))
            return ([], "Routine signatures or definitions are incomplete; apply this object manually.");
        return ([Drop(right, provider), left.Definition], null);
    }

    private static (IReadOnlyList<string> Commands, string? Note) PlanTableChanges(TableModel leftModel, TableModel rightModel, string provider)
    {
        var (left, right) = (leftModel.Table, rightModel.Table);
        var commands = new List<string>();
        var notes = new List<string>();
        var tableName = QuoteQualified(left.Schema, left.Name, provider);
        var leftIndexes = leftModel.Indexes.ToDictionary(index => index.Name, StringComparer.OrdinalIgnoreCase);
        var rightIndexes = rightModel.Indexes.ToDictionary(index => index.Name, StringComparer.OrdinalIgnoreCase);
        bool SameIndex(SqlDocIndex a, SqlDocIndex b) =>
            Normalize(CreateIndex(a, provider)).Equals(Normalize(CreateIndex(b, provider)), StringComparison.OrdinalIgnoreCase);
        // Drop removed or redefined indexes first so column drops and type changes are not blocked by them.
        foreach (var index in rightModel.Indexes.Where(index => !leftIndexes.TryGetValue(index.Name, out var desired) || !SameIndex(desired, index)))
            commands.Add(DropIndex(index, provider));
        var leftColumns = left.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var rightColumns = right.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var column in left.Columns.Where(column => !rightColumns.ContainsKey(column.Name)))
            commands.Add($"ALTER TABLE {tableName} ADD {ColumnDefinition(column, provider)}");
        foreach (var column in right.Columns.Where(column => !leftColumns.ContainsKey(column.Name)))
            commands.Add($"ALTER TABLE {tableName} DROP COLUMN {Quote(column.Name, provider)}");
        foreach (var column in left.Columns.Where(column => rightColumns.ContainsKey(column.Name)))
        {
            var current = rightColumns[column.Name];
            if (EquivalentColumn(column, current)) continue;
            switch (provider)
            {
                case "sqlserver": commands.Add($"ALTER TABLE {tableName} ALTER COLUMN {ColumnDefinition(column, provider, includeDefault: false)}"); break;
                case "mysql": commands.Add($"ALTER TABLE {tableName} MODIFY COLUMN {ColumnDefinition(column, provider)}"); break;
                case "oracle": commands.Add($"ALTER TABLE {tableName} MODIFY ({ColumnDefinition(column, provider)})"); break;
                case "postgresql":
                    commands.Add($"ALTER TABLE {tableName} ALTER COLUMN {Quote(column.Name, provider)} TYPE {ColumnType(column)}");
                    commands.Add($"ALTER TABLE {tableName} ALTER COLUMN {Quote(column.Name, provider)} {(column.IsNullable ? "DROP" : "SET")} NOT NULL");
                    commands.Add($"ALTER TABLE {tableName} ALTER COLUMN {Quote(column.Name, provider)} {(column.DefaultValue is null ? "DROP DEFAULT" : $"SET DEFAULT {column.DefaultValue}")}");
                    break;
                default: notes.Add($"SQLite cannot safely alter column {column.Name} in place."); break;
            }
        }
        var leftPk = left.Columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToArray();
        var rightPk = right.Columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToArray();
        if (!leftPk.SequenceEqual(rightPk, StringComparer.OrdinalIgnoreCase)) notes.Add("Primary-key changes require manual review.");
        if (!ForeignKeysEquivalent(left.ForeignKeys, right.ForeignKeys)) notes.Add("Foreign-key changes require manual review.");
        foreach (var index in leftModel.Indexes.Where(index => !rightIndexes.TryGetValue(index.Name, out var current) || !SameIndex(index, current)))
            commands.Add(CreateIndex(index, provider));
        return (notes.Count == 0 ? commands : [], notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private static string CreateTable(SqlDocTable table, string provider)
    {
        var lines = table.Columns.Select(column => "  " + ColumnDefinition(column, provider)).ToList();
        var primaryKeys = table.Columns.Where(column => column.IsPrimaryKey).Select(column => Quote(column.Name, provider)).ToList();
        if (primaryKeys.Count > 0) lines.Add($"  CONSTRAINT {Quote($"PK_{table.Name}", provider)} PRIMARY KEY ({string.Join(", ", primaryKeys)})");
        lines.AddRange(table.ForeignKeys.Select(fk => $"  CONSTRAINT {Quote(fk.Name, provider)} FOREIGN KEY ({Quote(fk.ColumnName, provider)}) REFERENCES {QuoteExistingQualified(fk.ReferencedTable, provider)} ({Quote(fk.ReferencedColumn, provider)})"));
        return $"CREATE TABLE {QuoteQualified(table.Schema, table.Name, provider)} (\n{string.Join(",\n", lines)}\n)";
    }

    private static string TableDefinition(TableModel model, string provider) =>
        string.Join(";\n\n", [CreateTable(model.Table, provider), .. model.Indexes.Select(index => CreateIndex(index, provider))]);

    private static string CreateView(SqlDocView view, string provider)
    {
        var definition = view.Definition?.Trim();
        if (string.IsNullOrWhiteSpace(definition)) return $"-- Definition unavailable for view {QualifiedName(view.Schema, view.Name)}";
        return Regex.IsMatch(definition, @"^\s*CREATE\s+", RegexOptions.IgnoreCase)
            ? definition
            : $"CREATE VIEW {QuoteQualified(view.Schema, view.Name, provider)} AS\n{definition}";
    }

    private static string CreateIndex(SqlDocIndex index, string provider) =>
        $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {Quote(index.Name, provider)} ON {QuoteQualified(index.Schema, index.TableName, provider)} ({string.Join(", ", index.Columns.Select(column => Quote(column, provider)))})";

    private static string DefinitionOrPlaceholder(string? definition, string type, string name) =>
        string.IsNullOrWhiteSpace(definition) ? $"-- Definition unavailable for {type.ToLowerInvariant()} {name}" : definition.Trim();

    private static string Drop(SchemaObject item, string provider)
    {
        if (item.Model is SqlDocIndex index) return DropIndex(index, provider);
        var keyword = item.Type switch { "Table" => "TABLE", "View" => "VIEW", "Stored procedure" => "PROCEDURE", "Function" => "FUNCTION", _ => "OBJECT" };
        var parts = item.Name.Split('.', 2);
        return parts.Length == 2 ? $"DROP {keyword} {QuoteQualified(parts[0], parts[1], provider)}" : $"DROP {keyword} {Quote(parts[0], provider)}";
    }

    private static string DropIndex(SqlDocIndex index, string provider) =>
        provider is "mysql" or "sqlserver"
            ? $"DROP INDEX {Quote(index.Name, provider)} ON {QuoteQualified(index.Schema, index.TableName, provider)}"
            : $"DROP INDEX {QuoteQualified(index.Schema, index.Name, provider)}";

    private static string ColumnDefinition(SqlDocColumn column, string provider, bool includeDefault = true)
    {
        var value = $"{Quote(column.Name, provider)} {ColumnType(column)} {(column.IsNullable ? "NULL" : "NOT NULL")}";
        if (includeDefault && !string.IsNullOrWhiteSpace(column.DefaultValue)) value += $" DEFAULT {column.DefaultValue}";
        return value;
    }

    private static string ColumnType(SqlDocColumn column)
    {
        if (!column.MaxLength.HasValue || column.MaxLength <= 0 || column.DataType.Contains('(')) return column.DataType;
        var type = column.DataType.Trim();
        if (type.Contains("text", StringComparison.OrdinalIgnoreCase) || type.Contains("blob", StringComparison.OrdinalIgnoreCase) || type.Contains("clob", StringComparison.OrdinalIgnoreCase)) return type;
        return $"{type}({column.MaxLength.Value})";
    }

    private static bool EquivalentColumn(SqlDocColumn left, SqlDocColumn right) =>
        left.DataType.Equals(right.DataType, StringComparison.OrdinalIgnoreCase) && left.MaxLength == right.MaxLength && left.IsNullable == right.IsNullable &&
        Normalize(left.DefaultValue ?? string.Empty).Equals(Normalize(right.DefaultValue ?? string.Empty), StringComparison.OrdinalIgnoreCase);

    private static bool ForeignKeysEquivalent(IReadOnlyList<SqlDocForeignKey> left, IReadOnlyList<SqlDocForeignKey> right) =>
        left.Select(ForeignKeyKey).OrderBy(value => value).SequenceEqual(right.Select(ForeignKeyKey).OrderBy(value => value), StringComparer.OrdinalIgnoreCase);

    private static string ForeignKeyKey(SqlDocForeignKey item) => $"{item.Name}|{item.ColumnName}|{item.ReferencedTable}|{item.ReferencedColumn}";
    private static string Normalize(string value) => Regex.Replace(value.Trim().TrimEnd(';'), @"\s+", " ");
    private static string Terminate(string command) => command.TrimEnd().EndsWith(';') ? command.TrimEnd() : command.TrimEnd() + ";";
    private static int KeyOrder(string key) => key.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? 0 : key.StartsWith("view:", StringComparison.OrdinalIgnoreCase) ? 1 : key.StartsWith("procedure:", StringComparison.OrdinalIgnoreCase) ? 2 : key.StartsWith("function:", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    private static string QualifiedName(string schema, string name) => string.IsNullOrWhiteSpace(schema) ? name : $"{schema}.{name}";
    private static string ProviderKey(string provider) => provider.Replace(" ", string.Empty).ToLowerInvariant() switch { "sqlserver" => "sqlserver", "sqlite" => "sqlite", "mysql" => "mysql", "oracle" => "oracle", "postgresql" => "postgresql", var value => value };
    private static string Quote(string name, string provider) => provider switch { "sqlserver" => $"[{name.Replace("]", "]]" )}]", "mysql" => $"`{name.Replace("`", "``")}`", _ => $"\"{name.Replace("\"", "\"\"")}\"" };
    private static string QuoteQualified(string schema, string name, string provider) => string.IsNullOrWhiteSpace(schema) || provider == "sqlite" && schema.Equals("main", StringComparison.OrdinalIgnoreCase) ? Quote(name, provider) : $"{Quote(schema, provider)}.{Quote(name, provider)}";
    private static string QuoteExistingQualified(string value, string provider)
    {
        var parts = value.Split('.', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? QuoteQualified(parts[0], parts[1], provider) : Quote(parts[0], provider);
    }
}
