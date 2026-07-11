using System.Security.Cryptography;
using System.Text;
using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public sealed class HashService : IHashService
{
    private static readonly string[] Algorithms = ["MD5", "SHA1", "SHA256", "SHA384", "SHA512"];
    public IReadOnlyCollection<string> SupportedAlgorithms => Algorithms;

    public HashResult Compute(string input, string algorithm)
    {
        var normalized = algorithm.Trim().ToUpperInvariant();
        if (!Algorithms.Contains(normalized, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported hashing algorithm.", nameof(algorithm));

        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = normalized switch
        {
            "MD5" => MD5.HashData(bytes),
            "SHA1" => SHA1.HashData(bytes),
            "SHA256" => SHA256.HashData(bytes),
            "SHA384" => SHA384.HashData(bytes),
            "SHA512" => SHA512.HashData(bytes),
            _ => throw new InvalidOperationException()
        };

        return new HashResult(normalized, Convert.ToHexString(hash).ToLowerInvariant(), input.Length);
    }
}
