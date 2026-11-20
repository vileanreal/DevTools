namespace DevTools.Web.Data.Entities;

public sealed class DeploymentSystem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? BackupDirectory { get; set; }
    public string? PackageDirectory { get; set; }
    public List<DeploymentModule> Modules { get; set; } = [];
    public List<DeploymentPackage> Packages { get; set; } = [];
}

public sealed class DeploymentModule
{
    public int Id { get; set; }
    public int DeploymentSystemId { get; set; }
    public required string Name { get; set; }
    public required string TargetPath { get; set; }
    public string? AppPoolName { get; set; }
    public string? ServiceName { get; set; }
    public string? DefaultExclusions { get; set; }
    public DeploymentSystem? DeploymentSystem { get; set; }
}

public sealed class DeploymentPackage
{
    public int Id { get; set; }
    public int DeploymentSystemId { get; set; }
    public required string FileName { get; set; }
    public string? Description { get; set; }
    public required string StoredPath { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public required string Status { get; set; }
    public required string AnalysisJson { get; set; }
    public string? BackupPath { get; set; }
    public string? ManifestJson { get; set; }
    public DateTime? DeployedAtUtc { get; set; }
    public DateTime? RolledBackAtUtc { get; set; }
    public string? Error { get; set; }
    public DeploymentSystem? DeploymentSystem { get; set; }
    public List<DeploymentEvent> Events { get; set; } = [];
}

/// <summary>History of a package: uploaded, each deployment and its outcome, each rollback.</summary>
public sealed class DeploymentEvent
{
    public int Id { get; set; }
    public int DeploymentPackageId { get; set; }
    public required string Kind { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Detail { get; set; }
    public DeploymentPackage? DeploymentPackage { get; set; }
}
