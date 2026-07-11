using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace DevTools.Web.Security;

public interface ILoginCredentialVerifier
{
    bool IsValid(string? username, string? password);
}

public sealed class Sha256CredentialVerifier(IOptions<LoginOptions> options) : ILoginCredentialVerifier
{
    public bool IsValid(string? username, string? password)
    {
        var configured = options.Value;
        var usernameHash = SHA256.HashData(Encoding.UTF8.GetBytes(username ?? string.Empty));
        var passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty));
        var configuredUsernameHash = Convert.FromHexString(configured.UsernameSha256);
        var configuredPasswordHash = Convert.FromHexString(configured.PasswordSha256);
        var usernameMatches = CryptographicOperations.FixedTimeEquals(usernameHash, configuredUsernameHash);
        var passwordMatches = CryptographicOperations.FixedTimeEquals(passwordHash, configuredPasswordHash);
        return usernameMatches & passwordMatches;
    }
}
