namespace DevTools.Shared.Models;

public static class DeploymentModes
{
    public const string Partial = "partial";
    public const string Full = "full";
    public static bool IsValid(string? mode) => mode is Partial or Full;
}

public static class DeploymentPackageStatuses
{
    public const string Uploaded = "Uploaded";
    public const string Deploying = "Deploying";
    public const string Deployed = "Deployed";
    public const string Failed = "Failed";
    public const string Restored = "Restored";
    public const string RollingBack = "RollingBack";
    public const string RolledBack = "RolledBack";
    public const string Interrupted = "Interrupted";
}

/// <summary>Kinds of package history events.</summary>
public static class DeploymentEventKinds
{
    public const string Uploaded = "Uploaded";
    public const string DeployStarted = "DeployStarted";
    public const string Deployed = "Deployed";
    public const string DeployFailed = "DeployFailed";
    public const string Restored = "Restored";
    public const string RollbackStarted = "RollbackStarted";
    public const string RolledBack = "RolledBack";
    public const string RollbackFailed = "RollbackFailed";
    public const string Interrupted = "Interrupted";
}

public sealed record DeploymentEventDto(string Kind, DateTimeOffset At, string? Detail);

/// <summary>What an uploaded package contains, per matched module.</summary>
public sealed record DeploymentPackageFile(string Path, long Size);
public sealed record DeploymentPackageModuleContents(string FolderName, string? ModuleName, IReadOnlyList<DeploymentPackageFile> Files, bool Truncated);
public sealed record DeploymentPackageContents(int PackageId, IReadOnlyList<DeploymentPackageModuleContents> Modules);

public sealed record DeploymentModuleDto(int Id, int SystemId, string Name, string TargetPath, string? AppPoolName, string? ServiceName, string? DefaultExclusions);
public sealed record DeploymentSystemDto(int Id, string Name, string? BackupDirectory, string EffectiveBackupDirectory, string? PackageDirectory, string EffectivePackageDirectory, IReadOnlyList<DeploymentModuleDto> Modules);
public sealed record SaveDeploymentSystemRequest(string? Name, string? BackupDirectory, string? PackageDirectory = null);
public sealed record SaveDeploymentModuleRequest(string? Name, string? TargetPath, string? AppPoolName, string? ServiceName, string? DefaultExclusions);

/// <summary>One top-level module folder found in an uploaded package.</summary>
public sealed record DeploymentPackageModule(string FolderName, int? ModuleId, string? ModuleName, int FileCount, long TotalBytes);

public sealed record DeploymentPackageAnalysis(
    string? WrapperFolder,
    IReadOnlyList<DeploymentPackageModule> Modules,
    IReadOnlyList<string> UnmatchedFolders,
    int IgnoredRootFiles,
    long TotalUncompressedBytes);

public sealed record DeploymentPackageDto(
    int Id,
    int SystemId,
    string FileName,
    string? Description,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    string Status,
    DeploymentPackageAnalysis Analysis,
    string? BackupFileName,
    DateTimeOffset? DeployedAt,
    DateTimeOffset? RolledBackAt,
    string? Error,
    bool CanDeploy,
    bool CanRollback,
    string? RollbackBlockedReason,
    string? RollbackWarning,
    IReadOnlyList<DeploymentEventDto> Events);

public sealed record DeployModuleOption(int ModuleId, string? Mode, string? Exclusions);

/// <summary>Preview of what deploying one module will do. Action: new, replace, excluded, delete or keep.</summary>
public sealed record DeploymentFilePreviewRequest(int ModuleId, string? Mode, string? Exclusions);
public sealed record DeploymentFileChange(string Path, string Action, long Size);
public sealed record DeploymentFilePreview(
    int ModuleId,
    string? Mode,
    int NewCount,
    int ReplaceCount,
    int ExcludedCount,
    int DeleteCount,
    int KeepCount,
    bool Truncated,
    IReadOnlyList<DeploymentFileChange> Files);
public sealed record DeployPackageRequest(IReadOnlyList<DeployModuleOption>? Modules, bool AutoRestore = true);

public sealed record DeploymentStepDto(string Key, string Label, string Status, string? Detail);
public sealed record DeploymentLogLine(DateTimeOffset At, string Level, string Message);

public sealed record DeploymentJobStatus(
    string JobId,
    int SystemId,
    int PackageId,
    string Kind,
    string Status,
    IReadOnlyList<DeploymentStepDto> Steps,
    IReadOnlyList<DeploymentLogLine> Log,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? BackupFileName);

/// <summary>Written into every backup and stored with the package so a deployment can be rolled back.</summary>
public sealed record DeploymentManifest(
    int PackageId,
    string PackageFileName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DeploymentManifestModule> Modules);

public sealed record DeploymentManifestModule(
    int ModuleId,
    string ModuleName,
    string TargetPath,
    string Mode,
    string? Exclusions,
    string? AppPoolName,
    string? ServiceName,
    IReadOnlyList<string> BackedUpFiles,
    IReadOnlyList<string> AddedFiles);
