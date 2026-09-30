using System.Security.Cryptography;
using System.Text;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ConvertToUsingDeclaration

namespace Ansight.Infrastructure.Utilities;

/// <summary>
/// A helper class for generating MD5 hashes from strings, streams and file paths.
/// </summary>
public static class MD5Helper
{
    /// <summary>
    /// Creates a new MD5 hash for the <paramref name="string"/>.
    /// </summary>
    public static string FromString(string @string)
    {
        if (string.IsNullOrEmpty(@string))
        {
            return string.Empty;
        }

        using (var md5 = MD5.Create())
        {
            var data = Encoding.UTF8.GetBytes(@string);
            return BitConverter.ToString(md5.ComputeHash(data));
        }
    }

    /// <summary>
    /// Asynchronous creates a new MD5 hash for the <paramref name="string"/>.
    /// </summary>
    public static Task<string> FromStringAsync(string @string)
    {
        if (string.IsNullOrEmpty(@string))
        {
            throw new ArgumentException($"'{nameof(@string)}' cannot be null or empty.", nameof(@string));
        }

        return Task.Run(() => FromString(@string));
    }


    /// <summary>
    /// Creates a MD5 hash for the given <paramref name="stream"/>.
    /// </summary>
    public static string FromStream(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        using (var md5 = MD5.Create())
        {
            return BitConverter.ToString(md5.ComputeHash(stream));
        }
    }


    /// <summary>
    /// Asynchronoulsy creates a new MD5 hash for the given <paramref name="stream"/>.
    /// </summary>
    public static Task<string> FromStreamAsync(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        return Task.Run(() => FromStream(stream));
    }

    /// <summary>
    /// Creates an MD5 has for the given <paramref name="filePath"/>.
    /// </summary>
    /// <returns>The file.</returns>
    /// <param name="filePath">File path.</param>
    public static string FromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException($"'{nameof(filePath)}' cannot be null or whitespace.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Cannot create an MD5 checksum for '{filePath}' as it does not exist.");
        }

        using (var fs = File.OpenRead(filePath))
        {
            return FromStream(fs);
        }
    }

    /// <summary>
    /// Asynchronously creates an MD5 has for the given <paramref name="filePath"/>.
    /// </summary>
    public static Task<string> FromFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException($"'{nameof(filePath)}' cannot be null or whitespace.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Cannot create an MD5 checksum for '{filePath}' as it does not exist.");
        }

        return Task.Run(() => FromFile(filePath));
    }
}