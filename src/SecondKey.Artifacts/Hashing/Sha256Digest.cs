using System.Security.Cryptography;

namespace SecondKey.Artifacts.Hashing;

/// <summary>Lower-case hex SHA-256, the digest every artifact reference carries.</summary>
public static class Sha256Digest
{
    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string OfBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
