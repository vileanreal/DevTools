namespace DevTools.Web.Data.Entities;

public sealed class PatchSystem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? OutputDirectory { get; set; }
    /// <summary>Patch Helper window title; empty means "&lt;system name&gt; Patch Helper".</summary>
    public string? HelperTitle { get; set; }
    public string? HelperSubtitle { get; set; }
    /// <summary>Shown by the Patch Helper when something fails, e.g. who to send the log to.</summary>
    public string? SupportContact { get; set; }
    /// <summary>File name of the helper inside the patch; empty means PatchHelper.exe.</summary>
    public string? HelperFileName { get; set; }
    public List<PatchModule> Modules { get; set; } = [];
    public List<PatchSnapshot> Snapshots { get; set; } = [];
    public List<PatchPackage> Packages { get; set; } = [];
    public List<PatchLatestEvent> LatestEvents { get; set; } = [];
}

public sealed class PatchModule
{
    public int Id { get; set; }
    public int PatchSystemId { get; set; }
    public required string Name { get; set; }
    public required string SourcePath { get; set; }
    public string? Exclusions { get; set; }
    /// <summary>The snapshot holding this module's latest version (the files now on UAT/production).</summary>
    public int? LatestSnapshotId { get; set; }
    /// <summary>Suggested folder of this module on the client's server, pre-filled by the Patch Helper.</summary>
    public string? ClientFolder { get; set; }
    /// <summary>IIS app pool the Patch Helper stops before applying and starts afterwards (on the client's server).</summary>
    public string? AppPoolName { get; set; }
    /// <summary>Windows service the Patch Helper stops before applying and starts afterwards (on the client's server).</summary>
    public string? ServiceName { get; set; }
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
    /// <summary>JSON array of the module names this snapshot covers, so lists don't need to decompress the manifest.</summary>
    public string? Modules { get; set; }
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
    /// <summary>JSON array of the module names in this patch.</summary>
    public string? Modules { get; set; }
    /// <summary>Gzip JSON <c>PatchChangeSet</c>: exactly what was compiled. Older patches have none and are rebuilt from snapshots.</summary>
    public byte[]? ChangesGz { get; set; }
    public PatchSystem? PatchSystem { get; set; }
}

/// <summary>
/// One change of a module's latest version: from which snapshot to which, when, by whom, how and why. Labels are copied
/// so the history stays readable after a snapshot is deleted.
/// </summary>
public sealed class PatchLatestEvent
{
    public int Id { get; set; }
    public int PatchSystemId { get; set; }
    public int? PatchModuleId { get; set; }
    public required string ModuleName { get; set; }
    public int? PreviousSnapshotId { get; set; }
    public string? PreviousLabel { get; set; }
    public DateTime? PreviousCreatedAtUtc { get; set; }
    public int? SnapshotId { get; set; }
    public string? SnapshotLabel { get; set; }
    public DateTime? SnapshotCreatedAtUtc { get; set; }
    public DateTime AtUtc { get; set; }
    public string? CreatedBy { get; set; }
    /// <summary>initial, compile, current, snapshot, patch or undo (see PatchLatestSources).</summary>
    public required string Source { get; set; }
    public int? PatchPackageId { get; set; }
    public string? PatchFileName { get; set; }
    public required string Remarks { get; set; }
    public PatchSystem? PatchSystem { get; set; }
}
