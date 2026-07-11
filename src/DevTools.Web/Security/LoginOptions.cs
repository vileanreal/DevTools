namespace DevTools.Web.Security;

public sealed class LoginOptions
{
    public const string SectionName = "Login";

    public bool UseLogin { get; set; } = true;
    public string UsernameMd5 { get; set; } = string.Empty;
    public string PasswordMd5 { get; set; } = string.Empty;

    public bool HasValidHashes() => !UseLogin || (IsMd5(UsernameMd5) && IsMd5(PasswordMd5));

    private static bool IsMd5(string? value) =>
        value?.Length == 32 && value.All(character => Uri.IsHexDigit(character));
}
