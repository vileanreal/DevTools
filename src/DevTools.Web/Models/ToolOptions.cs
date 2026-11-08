namespace DevTools.Web.Models;

public sealed class ToolOptions
{
    public const string SectionName = "Tools";

    public bool Hash { get; set; } = true;
    public bool Formatter { get; set; } = true;
    public bool Logs { get; set; } = true;
    public bool SystemLogs { get; set; } = true;
    public bool SystemServices { get; set; } = true;
    public bool SqlDoc { get; set; } = true;

    public bool IsEnabled(string key) => key switch
    {
        "hash" => Hash,
        "formatter" => Formatter,
        "logs" => Logs,
        "system-logs" => SystemLogs,
        "system-services" => SystemServices,
        "sql-doc" => SqlDoc,
        _ => false
    };
}
