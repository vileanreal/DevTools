namespace DevTools.Shared.Models;

public static class PatchFormats
{
    public const string Zip = "zip";
    public const string SevenZip = "7z";
    public static bool IsValid(string? format) => format is Zip or SevenZip;
}

public static class PatchChangeKinds
{
    public const string Added = "ADDED";
    public const string Changed = "CHANGED";
    public const string Deleted = "DELETED";
}

/// <summary>One file in a snapshot: path relative to its module, size, last write time and SHA-256 hash.</summary>
public sealed record PatchFileEntry(string Path, long Size, DateTime LastWriteUtc, string Sha256);

/// <summary>The files of one module at a point in time.</summary>
public sealed record PatchModuleManifest(int ModuleId, string ModuleName, IReadOnlyList<PatchFileEntry> Files);

public sealed record PatchModuleDto(int Id, int SystemId, string Name, string SourcePath, string? Exclusions);
public sealed record PatchSystemDto(
    int Id,
    string Name,
    string? OutputDirectory,
    string EffectiveOutputDirectory,
    IReadOnlyList<PatchModuleDto> Modules,
    PatchSnapshotDto? LatestVersion);
public sealed record SavePatchSystemRequest(string? Name, string? OutputDirectory);
public sealed record SavePatchModuleRequest(string? Name, string? SourcePath, string? Exclusions);

public sealed record PatchSnapshotDto(
    int Id,
    int SystemId,
    DateTimeOffset CreatedAt,
    string Source,
    string? Label,
    string? CreatedBy,
    bool IsLatest,
    DateTimeOffset? MarkedLatestAt,
    int FileCount,
    long TotalBytes,
    int? PatchId);

public sealed record PatchFileChange(string Module, string Path, string Kind, long? Size, long? PreviousSize);
public sealed record PatchModuleChangeSummary(string Module, int Added, int Changed, int Deleted);

/// <summary>Differences between a base snapshot and the current files (or another snapshot).</summary>
public sealed record PatchChangesDto(
    int? BaseSnapshotId,
    DateTimeOffset? BaseCreatedAt,
    string? BaseLabel,
    int? TargetSnapshotId,
    DateTimeOffset ComparedAt,
    int Added,
    int Changed,
    int Deleted,
    IReadOnlyList<PatchModuleChangeSummary> Modules,
    IReadOnlyList<PatchFileChange> Files,
    bool Truncated);

public sealed record CompilePatchRequest(string? Format, int? BaseSnapshotId, string? Label);
public sealed record SetLatestRequest(string? Label);

public sealed record PatchPackageDto(
    int Id,
    int SystemId,
    DateTimeOffset CreatedAt,
    string Format,
    string FileName,
    long SizeBytes,
    string? Label,
    string? CreatedBy,
    int Added,
    int Changed,
    int Deleted,
    int? BaseSnapshotId,
    DateTimeOffset? BaseCreatedAt,
    int SnapshotId,
    bool SnapshotIsLatest,
    bool FileExists);
