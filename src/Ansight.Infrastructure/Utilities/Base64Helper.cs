using System;
using System.Text;

namespace Ansight.Infrastructure.Utilities;

/// <summary>
/// A helper class for encoding and decoding base64 strings.
/// </summary>
public static class Base64Helper
{
    /// <summary>
    /// Decode the <paramref name="value"/> from a Base64 string.
    /// </summary>
    /// <returns>The decode.</returns>
    /// <param name="value">The value.</param>
    public static string DecodeBase64(this string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Convert.FromBase64String(value);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Encode the <paramref name="value"/> into a base64 string.
    /// </summary>
    /// <returns>The encode.</returns>
    /// <param name="value">Value.</param>
    public static string EncodeBase64(this string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decode the <paramref name="value"/> from a Base64 string.
    /// </summary>
    public static byte[] DecodeBase64AsBinary(this string value)
    {
        if (value is null)
        {
            return Array.Empty<byte>();
        }

        return Convert.FromBase64String(value);
    }

    /// <summary>
    /// Encode the <paramref name="value"/> into a base64 string.
    /// </summary>
    public static string EncodeBinaryAsBase64(this byte[] value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return Convert.ToBase64String(value);
    }

}