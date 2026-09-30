namespace Ansight.Host.Utilities;

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public static class AddressDiscovery
{
    private const string AdditionalHostAddressesEnvironmentVariable = "ANSIGHT_ADDITIONAL_HOST_ADDRESSES";
    private static Func<string, CancellationToken, Task<string?>>? macCatalystWifiNetworkNameResolver;

    public static AddressDiscoveryResult Capture()
    {
        var preferredOutboundIpv4Address = TryResolvePreferredOutboundAddress(AddressFamily.InterNetwork, "1.1.1.1");
        var preferredOutboundIpv6Address = TryResolvePreferredOutboundAddress(AddressFamily.InterNetworkV6, "2606:4700:4700::1111");
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToArray();

        var addresses = interfaces
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses.Select(address => new
            {
                NetworkInterface = networkInterface,
                Address = address.Address,
                address.PrefixLength
            }))
            .Where(entry => IsEligibleDiscoveredHostAddress(entry.Address, entry.PrefixLength))
            .GroupBy(entry => entry.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Address = group.Key,
                AddressFamily = group.First().Address.AddressFamily,
                IsPreferredOutboundAddress = string.Equals(
                    group.Key,
                    group.First().Address.AddressFamily == AddressFamily.InterNetwork
                        ? preferredOutboundIpv4Address
                        : preferredOutboundIpv6Address,
                    StringComparison.OrdinalIgnoreCase),
                IsWifi = group.Any(entry => IsWifiInterface(entry.NetworkInterface)),
                HasGateway = group.Any(entry => entry.NetworkInterface.GetIPProperties().GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == group.First().Address.AddressFamily &&
                    !IPAddress.IsLoopback(gateway.Address) &&
                    !IPAddress.Any.Equals(gateway.Address) &&
                    !IPAddress.IPv6Any.Equals(gateway.Address)))
            })
            .OrderBy(entry => entry.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenByDescending(entry => entry.IsPreferredOutboundAddress)
            .ThenByDescending(entry => entry.IsWifi)
            .ThenByDescending(entry => entry.HasGateway)
            .ThenBy(entry => entry.Address, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Address)
            .ToArray();

        addresses = ApplyAdditionalHostAddresses(
            addresses,
            ResolveAdditionalHostAddressesOverride());

        var wifiInterface = interfaces.FirstOrDefault(IsWifiInterface);
        return new AddressDiscoveryResult(addresses, wifiInterface?.Name);
    }

    public static Task<string?> TryResolveWifiNetworkNameAsync(string? interfaceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return Task.FromResult<string?>(null);
        }

        if (OperatingSystem.IsMacCatalyst())
        {
            var resolver = Volatile.Read(ref macCatalystWifiNetworkNameResolver);
            return resolver is null
                ? Task.FromResult<string?>(interfaceName.Trim())
                : resolver(interfaceName.Trim(), cancellationToken);
        }

        if (OperatingSystem.IsMacOS())
        {
            return TryResolveMacWifiSsidAsync(interfaceName.Trim(), cancellationToken);
        }

        if (OperatingSystem.IsWindows())
        {
            return TryResolveWindowsWifiSsidAsync(interfaceName.Trim(), cancellationToken);
        }

        return Task.FromResult<string?>(interfaceName.Trim());
    }

    public static void ConfigureMacCatalystWifiNetworkNameResolver(
        Func<string, CancellationToken, Task<string?>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Volatile.Write(ref macCatalystWifiNetworkNameResolver, resolver);
    }

    public static bool HasAdditionalHostAddressesOverrideConfigured()
    {
        return SplitAdditionalHostAddresses(ResolveAdditionalHostAddressesOverride())
            .Any(address => IPAddress.TryParse(address, out var parsedAddress) && IsEligibleHostAddress(parsedAddress));
    }

    public static string? ResolveWifiNetworkName(AddressDiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        if (string.IsNullOrWhiteSpace(discovery.WifiInterfaceName))
        {
            return null;
        }

        try
        {
            return TryResolveWifiNetworkNameAsync(discovery.WifiInterfaceName)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWifiInterface(NetworkInterface networkInterface)
    {
        var name = networkInterface.Name ?? string.Empty;
        var description = networkInterface.Description ?? string.Empty;
        return name.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("wifi", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("airport", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("wifi", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("airport", StringComparison.OrdinalIgnoreCase) ||
               networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
    }

    private static bool IsEligibleHostAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !address.ToString().StartsWith("169.254.", StringComparison.Ordinal),
            AddressFamily.InterNetworkV6 => !address.IsIPv6LinkLocal &&
                                            !address.IsIPv6Multicast &&
                                            !address.IsIPv6Teredo &&
                                            !IPAddress.IPv6None.Equals(address),
            _ => false
        };
    }

    internal static bool IsEligibleDiscoveredHostAddress(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!IsEligibleHostAddress(address))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => prefixLength < 32,
            AddressFamily.InterNetworkV6 => prefixLength < 128,
            _ => false
        };
    }

    private static string? TryResolvePreferredOutboundAddress(AddressFamily family, string remoteAddress)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse(remoteAddress), 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryResolveMacWifiSsidAsync(string interfaceName, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(1.5));

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/sbin/networksetup",
                Arguments = $"-getairportnetwork {interfaceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = (await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim();

            return ParseMacWifiNetworkName(output);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public static string? ParseMacWifiNetworkName(string output)
    {
        const string prefix = "Current Wi-Fi Network: ";
        if (string.IsNullOrWhiteSpace(output)
            || !output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ssid = output[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(ssid) ? null : ssid;
    }

    private static async Task<string?> TryResolveWindowsWifiSsidAsync(string interfaceName, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(1.5));

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = (await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim();
            return ParseWindowsWifiNetworkName(output, interfaceName);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static string? ParseWindowsWifiNetworkName(string output, string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var targetInterfaceName = interfaceName?.Trim();
        string? currentInterfaceName = null;
        string? currentSsid = null;
        string? firstSsid = null;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = rawLine.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = rawLine[..separatorIndex].Trim();
            var value = rawLine[(separatorIndex + 1)..].Trim();

            if (string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase))
            {
                if (IsTargetWindowsWifiInterface(currentInterfaceName, currentSsid, targetInterfaceName))
                {
                    return currentSsid!.Trim();
                }

                currentInterfaceName = value;
                currentSsid = null;
                continue;
            }

            if (string.Equals(key, "SSID", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value))
            {
                currentSsid = value;
                firstSsid ??= value;
            }
        }

        if (IsTargetWindowsWifiInterface(currentInterfaceName, currentSsid, targetInterfaceName))
        {
            return currentSsid!.Trim();
        }

        return string.IsNullOrWhiteSpace(firstSsid) ? null : firstSsid.Trim();
    }

    internal static string[] ApplyAdditionalHostAddresses(
        IReadOnlyList<string> discoveredAddresses,
        string? additionalAddresses)
    {
        ArgumentNullException.ThrowIfNull(discoveredAddresses);

        var addresses = new List<string>();
        AddAddresses(SplitAdditionalHostAddresses(additionalAddresses));
        AddAddresses(discoveredAddresses);
        return addresses.ToArray();

        void AddAddresses(IEnumerable<string> values)
        {
            foreach (var value in values)
            {
                AddAddress(value);
            }
        }

        void AddAddress(string value)
        {
            var trimmed = value.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                !IPAddress.TryParse(trimmed, out var address) ||
                !IsEligibleHostAddress(address))
            {
                return;
            }

            var normalized = address.ToString();
            if (addresses.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            addresses.Add(normalized);
        }
    }

    private static string[] SplitAdditionalHostAddresses(string? additionalAddresses)
    {
        return string.IsNullOrWhiteSpace(additionalAddresses)
            ? []
            : additionalAddresses.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? ResolveAdditionalHostAddressesOverride()
    {
        var values = new List<string>();
        Add(Environment.GetEnvironmentVariable(AdditionalHostAddressesEnvironmentVariable));

        if (OperatingSystem.IsWindows())
        {
            Add(TryGetEnvironmentVariable(EnvironmentVariableTarget.User));
            Add(TryGetEnvironmentVariable(EnvironmentVariableTarget.Machine));
        }

        return values.Count == 0 ? null : string.Join(';', values);

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }
    }

    private static string? TryGetEnvironmentVariable(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable(AdditionalHostAddressesEnvironmentVariable, target);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTargetWindowsWifiInterface(
        string? currentInterfaceName,
        string? currentSsid,
        string? targetInterfaceName)
    {
        return !string.IsNullOrWhiteSpace(currentSsid) &&
               !string.IsNullOrWhiteSpace(currentInterfaceName) &&
               !string.IsNullOrWhiteSpace(targetInterfaceName) &&
               string.Equals(currentInterfaceName.Trim(), targetInterfaceName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKillProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }
    }
}
