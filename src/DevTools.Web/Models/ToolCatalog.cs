namespace DevTools.Web.Models;

/// <summary>A toolbox entry shown on the home page.</summary>
public sealed record ToolInfo(
    string Key,
    string Group,
    string Tone,
    string Title,
    string Description,
    string Icon,
    string Action,
    IReadOnlyList<string> Features);

/// <summary>The single list of tools; <see cref="ToolOptions"/> decides which are enabled.</summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ToolInfo> All { get; } =
    [
        new("hash", "Build & transform", "tone-1", "Hash generator", "Generate common hashes locally.", "#", "Hash", ["MD5", "SHA family", "Local only"]),
        new("formatter", "Build & transform", "tone-2", "Code formatter", "Format SQL, C#, JSON, XML, HTML, and more.", "</>", "Formatter", ["SQL", "C#", "JSON", "XML"]),
        new("logs", "Observe & operate", "tone-3", "Logs viewer", "Paste or upload logs for quick analysis.", "☷", "Logs", ["Paste", "Upload", "Filter"]),
        new("system-logs", "Observe & operate", "tone-4", "System logs", "Index log folders, then search, filter and chart every entry across modules.", "▦", "SystemLogs", ["Full-text search", "Charts", "File filters"]),
        new("system-services", "Observe & operate", "tone-5", "System Services", "Watch Windows services and IIS app pools, and start, stop or restart them.", "⚙", "SystemServices", ["Windows services", "IIS app pools", "Restart"]),
        new("deployment", "Release & deployment", "tone-1", "Deployment", "Upload a package, back up, stop app pools and services, patch, and start them again — with one-click rollback.", "⇪", "Deployment", ["Upload .zip/.7z", "Auto backup", "Rollback"]),
        new("patch-management", "Release & deployment", "tone-3", "Releases", "Track each module's files on this server, see what changed since the version on UAT/production, and compile the modules you choose into a patch.", "Δ", "PatchManagement", ["Per-module versions", "ADDED / CHANGED / DELETED", ".zip or .7z"]),
        new("sql-doc", "Database", "tone-6", "SQL Doc", "Inspect a database, export its documentation and draw relationship diagrams.", "DB", "SqlDoc", ["Schema docs", "Diagrams", "5 providers"]),
        new("database-compare", "Database", "tone-2", "Database Compare", "Diff two schemas side by side and generate the SQL to bring a target up to date.", "⇄", "DatabaseCompare", ["Side-by-side diff", "Update scripts", "Apply"])
    ];

    public static IReadOnlyList<ToolInfo> Enabled(ToolOptions options) => All.Where(tool => options.IsEnabled(tool.Key)).ToList();
}
