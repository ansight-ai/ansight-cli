using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ansight.Infrastructure.Security;

internal static class WindowsDpapiKeyProvider
{
    private const int KeySizeInBytes = 32;

    internal static byte[] Resolve(string encryptedStorageFilePath, bool createIfMissing = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is available only on Windows.");
        }

        var keyFilePath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(encryptedStorageFilePath);
        if (File.Exists(keyFilePath))
        {
            return Unprotect(Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim()));
        }

        if (!createIfMissing) throw new FileNotFoundException("The protected secret-store key is unavailable.");

        var parent = Path.GetDirectoryName(keyFilePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var key = RandomNumberGenerator.GetBytes(KeySizeInBytes);
        try
        {
            var protectedKey = Protect(key);
            try
            {
                using var stream = new FileStream(
                    keyFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(Convert.ToBase64String(protectedKey));
                writer.Flush();
                stream.Flush(flushToDisk: true);
                return key;
            }
            catch (IOException) when (File.Exists(keyFilePath))
            {
                CryptographicOperations.ZeroMemory(key);
                return Unprotect(Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim()));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static byte[] Protect(byte[] plaintext)
    {
        var input = CreateBlob(plaintext);
        try
        {
            if (!WindowsDpapiNative.CryptProtectData(
                    ref input,
                    "Ansight secret-store master key",
                    0,
                    0,
                    0,
                    WindowsDpapiNative.UiForbidden,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            ZeroAndFreeInput(input);
        }
    }

    private static byte[] Unprotect(byte[] protectedValue)
    {
        var input = CreateBlob(protectedValue);
        try
        {
            if (!WindowsDpapiNative.CryptUnprotectData(
                    ref input,
                    out var description,
                    0,
                    0,
                    0,
                    WindowsDpapiNative.UiForbidden,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            WindowsDpapiNative.LocalFree(description);
            var key = CopyAndFree(output);
            if (key.Length != KeySizeInBytes)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidDataException("The DPAPI-protected Ansight master key has an invalid length.");
            }

            return key;
        }
        finally
        {
            ZeroAndFreeInput(input);
            CryptographicOperations.ZeroMemory(protectedValue);
        }
    }

    private static WindowsDataBlob CreateBlob(byte[] value)
    {
        var data = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, data, value.Length);
        return new WindowsDataBlob { Size = value.Length, Data = data };
    }

    private static byte[] CopyAndFree(WindowsDataBlob blob)
    {
        try
        {
            var value = new byte[blob.Size];
            Marshal.Copy(blob.Data, value, 0, value.Length);
            return value;
        }
        finally
        {
            ZeroAndLocalFree(blob);
        }
    }

    private static void ZeroAndFreeInput(WindowsDataBlob blob)
    {
        if (blob.Data == 0)
        {
            return;
        }

        for (var index = 0; index < blob.Size; index++)
        {
            Marshal.WriteByte(blob.Data, index, 0);
        }

        Marshal.FreeHGlobal(blob.Data);
    }

    private static void ZeroAndLocalFree(WindowsDataBlob blob)
    {
        if (blob.Data == 0)
        {
            return;
        }

        for (var index = 0; index < blob.Size; index++)
        {
            Marshal.WriteByte(blob.Data, index, 0);
        }

        WindowsDpapiNative.LocalFree(blob.Data);
    }
}
