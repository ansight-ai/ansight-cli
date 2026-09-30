namespace Ansight.Host.Utilities;

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public sealed record AddressDiscoveryResult(
    IReadOnlyList<string> Addresses,
    string? WifiInterfaceName)
{
    public string? PreferredAddress => Addresses.FirstOrDefault();
}
