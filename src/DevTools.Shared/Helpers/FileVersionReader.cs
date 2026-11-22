using System.Diagnostics;
using System.Reflection;

namespace DevTools.Shared.Helpers;

/// <summary>
/// Reads the version of an .exe or .dll: the Windows file version, then the product version, then (for .NET assemblies
/// without a version resource) the assembly version. Returns null when the file has no readable version.
/// </summary>
public static class FileVersionReader
{
    public static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = Clean(info.FileVersion) ?? Clean(info.ProductVersion);
            if (version is not null) return version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException) { }
        try { return AssemblyName.GetAssemblyName(path).Version?.ToString(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or FileLoadException or ArgumentException) { return null; }
    }

    // Product versions can carry a build suffix ("1.2.3+abc123"); "0.0.0.0" means no version was set.
    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var plus = text.IndexOf('+');
        if (plus > 0) text = text[..plus];
        return text is "0.0.0.0" or "0.0.0" ? null : text;
    }
}

/// <summary>Compares a package's main-file version with the server's and decides whether a deployment may go ahead.</summary>
public static class DeploymentVersionRules
{
    /// <summary>Compares dotted versions numerically (1.10 &gt; 1.9; 1.2 == 1.2.0.0); falls back to ordinal text comparison.</summary>
    public static int Compare(string first, string second)
    {
        if (Version.TryParse(Pad(first), out var a) && Version.TryParse(Pad(second), out var b)) return a.CompareTo(b);
        return string.Compare(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static (string State, bool Blocking, string Message) Evaluate(string versionFile, bool inPackage, string? packageVersion, bool onServer, string? serverVersion, bool requireChange)
    {
        if (!inPackage)
            return ("not-in-package", requireChange,
                requireChange ? $"The package doesn't contain {versionFile}. This system requires a new version, so include it with a new version." : $"The package doesn't contain {versionFile}.");
        if (packageVersion is null)
            return ("unreadable", requireChange,
                requireChange ? $"{versionFile} in the package has no readable version, so it can't be checked." : $"{versionFile} in the package has no readable version.");
        if (!onServer || serverVersion is null)
            return ("first-deploy", false, onServer ? $"Version {packageVersion}; the server's {versionFile} has no readable version." : $"Version {packageVersion} (not on the server yet).");
        var order = Compare(packageVersion, serverVersion);
        if (order == 0)
            return ("same", requireChange,
                requireChange ? $"{versionFile} is still version {packageVersion}, the same as on the server. Update the version before deploying." : $"Same version as on the server ({packageVersion}).");
        if (order > 0) return ("newer", false, $"{serverVersion} → {packageVersion}");
        // Going back to an earlier version is what Rollback is for, so a required new version also refuses older ones.
        return ("older", requireChange,
            requireChange ? $"{versionFile} is version {packageVersion}, older than the server's {serverVersion}. Deploy a newer version, or use Rollback to go back to an earlier one."
                          : $"{serverVersion} → {packageVersion} (older than the server)");
    }

    // Missing parts count as 0, so 1.2 equals 1.2.0.0 (System.Version alone treats them as different).
    private static string Pad(string value)
    {
        var parts = value.Trim().Split('.').ToList();
        while (parts.Count < 4) parts.Add("0");
        return string.Join('.', parts);
    }
}
