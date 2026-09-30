using System.Runtime.InteropServices;

namespace Ansight.Infrastructure.Security;

internal static class WindowsDpapiNative
{
    internal const int UiForbidden = 0x1;

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptProtectData(
        ref WindowsDataBlob dataIn,
        string description,
        nint optionalEntropy,
        nint reserved,
        nint promptStructure,
        int flags,
        out WindowsDataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptUnprotectData(
        ref WindowsDataBlob dataIn,
        out nint description,
        nint optionalEntropy,
        nint reserved,
        nint promptStructure,
        int flags,
        out WindowsDataBlob dataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    internal static extern nint LocalFree(nint memory);
}
