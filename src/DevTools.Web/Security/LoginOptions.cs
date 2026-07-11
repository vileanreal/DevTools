namespace DevTools.Web.Security;

public sealed class LoginOptions
{
    public const string SectionName = "Login";

    public bool UseLogin { get; set; } = true;
    public string UsernameSha256 { get; set; } = string.Empty;
    public string PasswordSha256 { get; set; } = string.Empty;

    public bool HasValidHashes() => !UseLogin || (IsSha256(UsernameSha256) && IsSha256(PasswordSha256));

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);
}
