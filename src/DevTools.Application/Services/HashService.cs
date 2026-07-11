using System.Security.Cryptography;
using System.Text;
using DevTools.Shared.Models;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;

namespace DevTools.Application.Services;

public sealed class HashService : IHashService
{
    private static readonly string[] Algorithms =
    [
        "SHA256", "SHA384", "SHA512",
        "SHA3-256", "SHA3-384", "SHA3-512",
        "BLAKE2B-256", "BLAKE2B-512", "BLAKE2S-256",
        "HMACSHA256", "HMACSHA384", "HMACSHA512",
        "SHA1", "MD5"
    ];
    public IReadOnlyCollection<string> SupportedAlgorithms => Algorithms;

    public HashResult Compute(string input, string algorithm, string? key = null)
    {
        var normalized = algorithm.Trim().ToUpperInvariant().Replace("_", "-", StringComparison.Ordinal);
        if (!Algorithms.Contains(normalized, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported hashing algorithm.", nameof(algorithm));

        var bytes = Encoding.UTF8.GetBytes(input);
        var keyBytes = Encoding.UTF8.GetBytes(key ?? string.Empty);
        if (normalized.StartsWith("HMAC", StringComparison.Ordinal) && keyBytes.Length == 0)
            throw new ArgumentException("A secret key is required for HMAC algorithms.", nameof(key));

        var hash = normalized switch
        {
            "MD5" => MD5.HashData(bytes),
            "SHA1" => SHA1.HashData(bytes),
            "SHA256" => SHA256.HashData(bytes),
            "SHA384" => SHA384.HashData(bytes),
            "SHA512" => SHA512.HashData(bytes),
            "SHA3-256" => ComputeDigest(new Sha3Digest(256), bytes),
            "SHA3-384" => ComputeDigest(new Sha3Digest(384), bytes),
            "SHA3-512" => ComputeDigest(new Sha3Digest(512), bytes),
            "BLAKE2B-256" => ComputeDigest(new Blake2bDigest(256), bytes),
            "BLAKE2B-512" => ComputeDigest(new Blake2bDigest(512), bytes),
            "BLAKE2S-256" => ComputeDigest(new Blake2sDigest(256), bytes),
            "HMACSHA256" => HMACSHA256.HashData(keyBytes, bytes),
            "HMACSHA384" => HMACSHA384.HashData(keyBytes, bytes),
            "HMACSHA512" => HMACSHA512.HashData(keyBytes, bytes),
            _ => throw new InvalidOperationException()
        };

        return new HashResult(normalized, Convert.ToHexString(hash).ToLowerInvariant(), input.Length);
    }

    private static byte[] ComputeDigest(IDigest digest, byte[] input)
    {
        digest.BlockUpdate(input, 0, input.Length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return output;
    }
}
