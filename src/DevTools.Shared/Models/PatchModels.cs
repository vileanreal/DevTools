namespace DevTools.Shared.Models;

public static class PatchFormats
{
    public const string Zip = "zip";
    public const string SevenZip = "7z";
    public static bool IsValid(string? format) => format is Zip or SevenZip;
}

/// <summary>How a module's latest version was changed (recorded in the version history).</summary>
public static class PatchLatestSources
{
    public const string Initial = "initial";
    public const string Compile = "compile";
    public const string Current = "current";
    public const string Snapshot = "snapshot";
    public const string Patch = "patch";
    public const string Undo = "undo";
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
    string? LatestLabel = null,
    string? LatestRemarks = null,
    DateTimeOffset? LatestChangedAt = null,
    string? LatestChangedBy = null,
    string? ClientFolder = null,
    string? AppPoolName = null,
    string? ServiceName = null);
public sealed record PatchSystemDto(
    int Id,
    string Name,
    string? OutputDirectory,
    string EffectiveOutputDirectory,
    IReadOnlyList<PatchModuleDto> Modules,
    string? HelperTitle = null,
    string? HelperSubtitle = null,
    string? SupportContact = null,
    string? HelperFileName = null,
    string EffectiveHelperTitle = "",
    string EffectiveHelperFileName = "PatchHelper.exe");
public sealed record SavePatchSystemRequest(
    string? Name,
    string? OutputDirectory,
    string? HelperTitle = null,
    string? HelperSubtitle = null,
    string? SupportContact = null,
    string? HelperFileName = null);
public sealed record SavePatchModuleRequest(
    string? Name,
    string? SourcePath,
    string? Exclusions,
    string? ClientFolder = null,
    string? AppPoolName = null,
    string? ServiceName = null);

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

/// <summary>One changed file. The hashes let the Patch Helper check the server has the expected version before replacing it.</summary>
public sealed record PatchFileChange(string Module, string Path, string Kind, long? Size, long? PreviousSize, string? Sha256 = null, string? PreviousSha256 = null);
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
/// <summary>
/// Compiles the selected modules' changes. Without module ids every module is included. <see cref="LatestRemarks"/> is
/// required when <see cref="SetAsLatest"/> is true: why this patch is now the latest version.
/// </summary>
public sealed record CompilePatchRequest(string? Format, int? BaseSnapshotId, string? Label, IReadOnlyList<int>? ModuleIds = null, bool SetAsLatest = false, string? LatestRemarks = null);
/// <summary>Takes a snapshot of the selected modules (all when empty) and makes it their latest version. Remarks say why.</summary>
public sealed record SetLatestRequest(string? Label, IReadOnlyList<int>? ModuleIds = null, string? Remarks = null);
/// <summary>Makes a snapshot (or the snapshot of a compiled patch) the latest version of the modules it contains.</summary>
public sealed record SetSnapshotLatestRequest(string? Remarks);
/// <summary>Moves a module back to the version it had before its most recent latest-version change.</summary>
public sealed record UndoLatestRequest(string? Remarks);

/// <summary>
/// patch.json inside every compiled patch, read by the Patch Helper (DevTools.PatchHelper). Property names are written in
/// camelCase; keep them in step with the helper's PatchManifest.
/// </summary>
public sealed record PatchHelperManifest(
    int FormatVersion,
    string System,
    string Title,
    string? Subtitle,
    string? SupportContact,
    string PatchName,
    string? Description,
    string CreatedAt,
    string? CreatedBy,
    IReadOnlyList<PatchHelperModule> Modules);
public sealed record PatchHelperModule(string Name, string? ClientFolder, string? AppPool, string? Service, IReadOnlyList<PatchHelperFile> Files);
public sealed record PatchHelperFile(string Path, string Kind, long? Size, string? Sha256, string? PreviousSha256);

/// <summary>One change of a module's latest version, newest first in the version history.</summary>
public sealed record PatchLatestEventDto(
    int Id,
    int? ModuleId,
    string ModuleName,
    int? PreviousSnapshotId,
    string? PreviousLabel,
    DateTimeOffset? PreviousCreatedAt,
    int? SnapshotId,
    string? SnapshotLabel,
    DateTimeOffset? SnapshotCreatedAt,
    DateTimeOffset At,
    string? CreatedBy,
    string Source,
    int? PatchId,
    string? PatchFileName,
    string Remarks,
    bool CanUndo);

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
