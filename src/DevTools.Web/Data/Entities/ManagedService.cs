namespace DevTools.Web.Data.Entities;

public sealed class ServiceSystem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<ManagedService> Services { get; set; } = [];
}

public sealed class ManagedService
{
    public int Id { get; set; }
    public int ServiceSystemId { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string TargetName { get; set; }
    public ServiceSystem? ServiceSystem { get; set; }
}
