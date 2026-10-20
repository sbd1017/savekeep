using System.Security.Cryptography;

namespace SaveKeep.Core.Util;

public static class Hex
{
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
