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

/// <summary>A module and its own latest version: the snapshot its changes are measured from.</summary>
public sealed record PatchModuleDto(
    int Id,
    int SystemId,
    string Name,
    string SourcePath,
    string? Exclusions,
    int? LatestSnapshotId = null,
    DateTimeOffset? LatestAt = null,
    string? LatestLabel = null);
public sealed record PatchSystemDto(
    int Id,
    string Name,
    string? OutputDirectory,
    string EffectiveOutputDirectory,
    IReadOnlyList<PatchModuleDto> Modules);
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
    int? PatchId,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> LatestFor);

public sealed record PatchFileChange(string Module, string Path, string Kind, long? Size, long? PreviousSize);
/// <summary>Change counts of one module and the snapshot they were measured from (none when the module has no version yet).</summary>
public sealed record PatchModuleChangeSummary(
    string Module,
    int Added,
    int Changed,
    int Deleted,
    int? ModuleId = null,
    int? BaseSnapshotId = null,
    DateTimeOffset? BaseCreatedAt = null,
    string? BaseLabel = null);

/// <summary>One module to compare: its base files (null when it has no version yet) and its files now or in a later snapshot.</summary>
public sealed record PatchModuleComparison(
    int? ModuleId,
    string Name,
    int? BaseSnapshotId,
    DateTimeOffset? BaseCreatedAt,
    string? BaseLabel,
    PatchModuleManifest? BaseFiles,
    PatchModuleManifest Current);

/// <summary>The exact change list stored with a compiled patch.</summary>
public sealed record PatchChangeSet(IReadOnlyList<PatchModuleChangeSummary> Modules, IReadOnlyList<PatchFileChange> Files);

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

/// <summary>Compiles the selected modules' changes. Without module ids every module is included.</summary>
public sealed record CompilePatchRequest(string? Format, int? BaseSnapshotId, string? Label, IReadOnlyList<int>? ModuleIds = null, bool SetAsLatest = false);
/// <summary>Takes a snapshot of the selected modules (all when empty) and makes it their latest version.</summary>
public sealed record SetLatestRequest(string? Label, IReadOnlyList<int>? ModuleIds = null);

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
    bool FileExists,
    IReadOnlyList<string> Modules);
