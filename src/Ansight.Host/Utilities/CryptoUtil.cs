namespace Ansight.Host.Utilities;

internal static class CryptoUtil
{
    public static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string CreateBase64UrlRandom(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        try
        {
            return ToBase64Url(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] FromBase64Url(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder != 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }

        return Convert.FromBase64String(normalized);
    }

    public static bool FixedTimeEqualsBase64Url(string expected, string actual)
    {
        byte[] expectedBytes;
        byte[] actualBytes;
        try
        {
            expectedBytes = FromBase64Url(expected);
            actualBytes = FromBase64Url(actual);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return expectedBytes.Length == actualBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }
}
