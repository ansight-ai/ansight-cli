using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace Ansight.RemoteSimulator.Core.Runtime;

internal static class LocalSdkProcessTargetPolicy
{
    public static bool IsEligible(
        string? platform,
        bool? isVirtual,
        string? remoteAddress,
        int processIdentifier)
        => IsMacProcessPlatform(platform, isVirtual)
           && IsLocalAddress(remoteAddress)
           && IsProcessRunning(processIdentifier);

    private static bool IsMacProcessPlatform(string? platform, bool? isVirtual)
        => string.Equals(platform, "macos", StringComparison.OrdinalIgnoreCase)
           || (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
               && isVirtual == false);

    public static bool IsProcessRunning(int processIdentifier)
    {
        if (processIdentifier <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processIdentifier);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    internal static bool IsLocalAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        try
        {
            return NetworkInterface
                .GetAllNetworkInterfaces()
                .SelectMany(static networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Any(candidate => AddressesEqual(candidate.Address, address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static bool AddressesEqual(IPAddress left, IPAddress right)
        => left.Equals(right)
           || (left.AddressFamily != right.AddressFamily
               && left.MapToIPv6().Equals(right.MapToIPv6()));
}
