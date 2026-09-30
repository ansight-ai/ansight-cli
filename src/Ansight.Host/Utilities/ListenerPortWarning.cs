namespace Ansight.Host.Utilities;

internal static class ListenerPortWarning
{
    // macOS EADDRINUSE ("address already in use"):
    // https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/intro.2.html
    private const int MacOsAddressAlreadyInUseErrorCode = 48;

    // Linux asm-generic EADDRINUSE ("address already in use"):
    // https://github.com/torvalds/linux/blob/master/include/uapi/asm-generic/errno.h
    private const int LinuxAddressAlreadyInUseErrorCode = 98;

    // Windows Winsock WSAEADDRINUSE ("address already in use"):
    // https://learn.microsoft.com/en-us/windows/win32/winsock/windows-sockets-error-codes-2
    private const int WindowsAddressAlreadyInUseErrorCode = 10048;
    private const string PortInUsePhrase = "another app is already using that port";

    public static bool TryCreateMessage(
        Exception exception,
        string listenerName,
        string portDescription,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(portDescription);

        if (!IsPortInUse(exception))
        {
            message = string.Empty;
            return false;
        }

        message =
            $"Ansight couldn't start the {listenerName} on {portDescription} because another app is already using that port. Close the other app or free the port, then restart Ansight.";
        return true;
    }

    public static bool IsPortWarningMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
               && message.Contains(PortInUsePhrase, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortInUse(Exception exception)
    {
        return exception switch
        {
            SocketException socketException when socketException.SocketErrorCode == SocketError.AddressAlreadyInUse => true,
            HttpListenerException httpListenerException
                when IsPortInUseNativeError(httpListenerException.NativeErrorCode)
                     || IsPortInUseNativeError(httpListenerException.ErrorCode) => true,
            _ => exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool IsPortInUseNativeError(int errorCode)
    {
        return errorCode is
            MacOsAddressAlreadyInUseErrorCode or
            LinuxAddressAlreadyInUseErrorCode or
            WindowsAddressAlreadyInUseErrorCode;
    }
}
