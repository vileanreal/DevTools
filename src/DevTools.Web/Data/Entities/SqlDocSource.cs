namespace DevTools.Web.Data.Entities;

public sealed class SqlDocSource
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Provider { get; set; }
    public required string ConnectionString { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<SqlDocDiagram> Diagrams { get; set; } = [];
}
