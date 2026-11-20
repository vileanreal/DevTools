namespace DevTools.Web.Data.Entities;

public sealed class PatchSystem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? OutputDirectory { get; set; }
    public List<PatchModule> Modules { get; set; } = [];
    public List<PatchSnapshot> Snapshots { get; set; } = [];
    public List<PatchPackage> Packages { get; set; } = [];
}

public sealed class PatchModule
{
    public int Id { get; set; }
    public int PatchSystemId { get; set; }
    public required string Name { get; set; }
    public required string SourcePath { get; set; }
    public string? Exclusions { get; set; }
    public PatchSystem? PatchSystem { get; set; }
}

/// <summary>The state of every module's files at a point in time. The file list is stored as gzip-compressed JSON.</summary>
public sealed class PatchSnapshot
{
    public int Id { get; set; }
    public int PatchSystemId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>"latest" (taken by Set current as latest version) or "patch" (captured when a patch was compiled).</summary>
    public required string Source { get; set; }
    public string? Label { get; set; }
    public string? CreatedBy { get; set; }
    public bool IsLatest { get; set; }
    public DateTime? MarkedLatestAtUtc { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public required byte[] ManifestGz { get; set; }
    public PatchSystem? PatchSystem { get; set; }
}

public sealed class PatchPackage
{
    public int Id { get; set; }
    public int PatchSystemId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public required string Format { get; set; }
    public required string FileName { get; set; }
    public required string StoredPath { get; set; }
    public long SizeBytes { get; set; }
    public string? Label { get; set; }
    public string? CreatedBy { get; set; }
    public int Added { get; set; }
    public int Changed { get; set; }
    public int Deleted { get; set; }
    /// <summary>The snapshot the changes were measured from (usually the latest version at the time).</summary>
    public int? BaseSnapshotId { get; set; }
    /// <summary>The snapshot of exactly what this patch contains.</summary>
    public int SnapshotId { get; set; }
    public PatchSystem? PatchSystem { get; set; }
}
