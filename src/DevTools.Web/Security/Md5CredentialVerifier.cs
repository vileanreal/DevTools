using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace DevTools.Web.Security;

public interface ILoginCredentialVerifier
{
    bool IsValid(string? username, string? password);
}

public sealed class Md5CredentialVerifier(IOptions<LoginOptions> options) : ILoginCredentialVerifier
{
    public bool IsValid(string? username, string? password)
    {
        var configured = options.Value;
        var usernameHash = MD5.HashData(Encoding.UTF8.GetBytes(username ?? string.Empty));
        var passwordHash = MD5.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty));
        var configuredUsernameHash = Convert.FromHexString(configured.UsernameMd5);
        var configuredPasswordHash = Convert.FromHexString(configured.PasswordMd5);
        var usernameMatches = CryptographicOperations.FixedTimeEquals(usernameHash, configuredUsernameHash);
        var passwordMatches = CryptographicOperations.FixedTimeEquals(passwordHash, configuredPasswordHash);
        return usernameMatches & passwordMatches;
    }
}
