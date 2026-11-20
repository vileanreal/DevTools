using DevTools.Shared.Helpers;

namespace DevTools.Shared.Tests.Helpers;

public sealed class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:\MyApp_Backup", true)]
    [InlineData("C:/MyApp_Backup", true)]
    [InlineData(@"d:\", true)]
    [InlineData(@"\\fileserver\backups", true)]
    [InlineData(@"\\fileserver\backups\myapp", true)]
    [InlineData(@"\\fileserver", false)]
    [InlineData("C:", false)]
    [InlineData(@"C:MyApp_Backup", false)]
    [InlineData(@"MyApp_Backup", false)]
    [InlineData(@"\MyApp_Backup", false)]
    [InlineData("/Users/me/backup", false)]
    [InlineData("", false)]
    public void IsFullyQualified_RecognisesDriveAndUncPaths(string path, bool expected) =>
        Assert.Equal(expected, WindowsPath.IsFullyQualified(path));

    [Theory]
    [InlineData(@"C:\MyApp_Backup", @"C:\MyApp_Backup")]
    [InlineData(@"c:\MyApp_Backup\", @"C:\MyApp_Backup")]
    [InlineData("C:/MyApp_Backup//daily/", @"C:\MyApp_Backup\daily")]
    [InlineData(@"C:\MyApp_Backup\.\old\..\new", @"C:\MyApp_Backup\new")]
    [InlineData(@"C:\..\..\MyApp", @"C:\MyApp")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"  D:\Apps\Api  ", @"D:\Apps\Api")]
    [InlineData(@"C:\Backups.\", @"C:\Backups")]
    [InlineData(@"\\fileserver\backups\", @"\\fileserver\backups")]
    [InlineData(@"\\fileserver\backups\myapp\..\daily", @"\\fileserver\backups\daily")]
    public void Normalize_ProducesCanonicalWindowsPaths(string input, string expected) =>
        Assert.Equal(expected, WindowsPath.Normalize(input));

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"\\fileserver\backups", true)]
    [InlineData(@"C:\MyApp_Backup", false)]
    [InlineData(@"\\fileserver\backups\myapp", false)]
    public void IsRoot_DetectsDriveRootsAndBareShares(string normalized, bool expected) =>
        Assert.Equal(expected, WindowsPath.IsRoot(normalized));

    [Theory]
    [InlineData(@"C:\inetpub\Api", @"C:\inetpub\Api", true)]
    [InlineData(@"C:\inetpub\api\bin", @"C:\INETPUB\Api", true)]
    [InlineData(@"C:\inetpub\Api2", @"C:\inetpub\Api", false)]
    [InlineData(@"C:\inetpub", @"C:\inetpub\Api", false)]
    [InlineData(@"C:\anything", @"C:\", true)]
    public void IsSameOrInside_ComparesCaseInsensitivelyOnFolderBoundaries(string path, string parent, bool expected) =>
        Assert.Equal(expected, WindowsPath.IsSameOrInside(path, parent));

    [Fact]
    public void Normalize_RejectsRelativePaths() =>
        Assert.Throws<ArgumentException>(() => WindowsPath.Normalize("MyApp_Backup"));
}
