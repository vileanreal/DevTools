namespace DevTools.Web.Data.Entities;

public sealed class SqlDocDiagram
{
    public int Id { get; set; }
    public int SqlDocSourceId { get; set; }
    public required string Name { get; set; }
    public required string DiagramJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public SqlDocSource? SqlDocSource { get; set; }
}
