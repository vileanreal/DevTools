namespace DevTools.Web.Models;

public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    /// <summary>Folder for uploaded packages, extraction staging and default backups. Relative paths resolve from the content root.</summary>
    public string WorkingDirectory { get; set; } = "deployments";

    /// <summary>Largest package upload accepted, in megabytes.</summary>
    public long MaxUploadMegabytes { get; set; } = 4096;

    /// <summary>Largest total uncompressed size accepted for one package, in megabytes (guards against archive bombs).</summary>
    public long MaxExtractedMegabytes { get; set; } = 20480;

    public long MaxUploadBytes => Math.Max(1, MaxUploadMegabytes) * 1024 * 1024;
    public long MaxExtractedBytes => Math.Max(1, MaxExtractedMegabytes) * 1024 * 1024;
}
