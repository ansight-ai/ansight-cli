using System.Runtime.InteropServices;

namespace Ansight.Infrastructure.Security;

internal static class MacOsKeychainNative
{
    internal const int Success = 0;
    internal const int ItemNotFound = -25300;

    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const uint Utf8Encoding = 0x08000100;
    private static readonly nint coreFoundationHandle = NativeLibrary.Load(CoreFoundationLibrary);
    private static readonly nint securityHandle = NativeLibrary.Load(SecurityLibrary);

    internal static nint SecClass => ReadReference(securityHandle, "kSecClass");

    internal static nint SecClassGenericPassword => ReadReference(securityHandle, "kSecClassGenericPassword");

    internal static nint SecAttrService => ReadReference(securityHandle, "kSecAttrService");

    internal static nint SecAttrAccount => ReadReference(securityHandle, "kSecAttrAccount");

    internal static nint SecAttrAccessible => ReadReference(securityHandle, "kSecAttrAccessible");

    internal static nint SecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        => ReadReference(securityHandle, "kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly");

    internal static nint SecValueData => ReadReference(securityHandle, "kSecValueData");

    internal static nint SecReturnData => ReadReference(securityHandle, "kSecReturnData");

    internal static nint SecMatchLimit => ReadReference(securityHandle, "kSecMatchLimit");

    internal static nint SecMatchLimitOne => ReadReference(securityHandle, "kSecMatchLimitOne");

    internal static nint BooleanTrue => ReadReference(coreFoundationHandle, "kCFBooleanTrue");

    internal static nint CreateDictionary()
    {
        var keyCallbacks = NativeLibrary.GetExport(coreFoundationHandle, "kCFTypeDictionaryKeyCallBacks");
        var valueCallbacks = NativeLibrary.GetExport(coreFoundationHandle, "kCFTypeDictionaryValueCallBacks");
        return CFDictionaryCreateMutable(0, 0, keyCallbacks, valueCallbacks);
    }

    internal static void SetValue(nint dictionary, nint key, nint value)
    {
        CFDictionarySetValue(dictionary, key, value);
    }

    internal static void SetString(nint dictionary, nint key, string value)
    {
        var nativeValue = CFStringCreateWithCString(0, value, Utf8Encoding);
        if (nativeValue == 0)
        {
            throw new InvalidOperationException("Could not allocate a macOS Keychain string value.");
        }

        try
        {
            CFDictionarySetValue(dictionary, key, nativeValue);
        }
        finally
        {
            CFRelease(nativeValue);
        }
    }

    internal static void SetData(nint dictionary, nint key, byte[] value)
    {
        var nativeValue = CFDataCreate(0, value, value.Length);
        if (nativeValue == 0)
        {
            throw new InvalidOperationException("Could not allocate macOS Keychain data.");
        }

        try
        {
            CFDictionarySetValue(dictionary, key, nativeValue);
        }
        finally
        {
            CFRelease(nativeValue);
        }
    }

    internal static byte[] ReadData(nint data)
    {
        var length = CFDataGetLength(data);
        if (length < 0 || length > int.MaxValue)
        {
            throw new InvalidDataException("The macOS Keychain returned an invalid data length.");
        }

        var bytes = new byte[(int)length];
        if (bytes.Length > 0)
        {
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
        }

        return bytes;
    }

    internal static void Release(nint value)
    {
        if (value != 0)
        {
            CFRelease(value);
        }
    }

    internal static int CopyMatching(nint query, out nint result)
        => SecItemCopyMatching(query, out result);

    internal static int Add(nint attributes)
        => SecItemAdd(attributes, out _);

    internal static int Update(nint query, nint attributesToUpdate)
        => SecItemUpdate(query, attributesToUpdate);

    internal static int Delete(nint query)
        => SecItemDelete(query);

    private static nint ReadReference(nint libraryHandle, string symbol)
        => Marshal.ReadIntPtr(NativeLibrary.GetExport(libraryHandle, symbol));

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFDictionaryCreateMutable(
        nint allocator,
        nint capacity,
        nint keyCallbacks,
        nint valueCallbacks);

    [DllImport(CoreFoundationLibrary)]
    private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFStringCreateWithCString(
        nint allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string value,
        uint encoding);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFDataCreate(nint allocator, [In] byte[] bytes, nint length);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFDataGetLength(nint data);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFDataGetBytePtr(nint data);

    [DllImport(CoreFoundationLibrary)]
    private static extern void CFRelease(nint value);

    [DllImport(SecurityLibrary)]
    private static extern int SecItemCopyMatching(nint query, out nint result);

    [DllImport(SecurityLibrary)]
    private static extern int SecItemAdd(nint attributes, out nint result);

    [DllImport(SecurityLibrary)]
    private static extern int SecItemUpdate(nint query, nint attributesToUpdate);

    [DllImport(SecurityLibrary)]
    private static extern int SecItemDelete(nint query);
}
