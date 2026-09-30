using System.Net;
using System.Net.Sockets;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal static class CliTestRuntimeOptionsResolver
{
    private const int MaximumPortAllocationAttempts = 128;

    public static async Task<CliRuntimeOptions> ResolveAsync(
        CliRuntimeOptions options,
        WorkspaceTestTargetRequest? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (CliCommandContext.Current is not null || RequiredPortsAreAvailable(options))
        {
            return options;
        }

        if (!await TargetsPhysicalDeviceAsync(options, target, cancellationToken).ConfigureAwait(false))
        {
            return options;
        }

        return WithAvailablePorts(options);
    }

    public static async Task<CliRuntimeOptions> ResolveAsync(
        CliRuntimeOptions options,
        IReadOnlyList<WorkspaceTestTargetRequest> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(targets);

        if (CliCommandContext.Current is not null || RequiredPortsAreAvailable(options))
        {
            return options;
        }

        foreach (var target in targets)
        {
            if (await TargetsPhysicalDeviceAsync(options, target, cancellationToken).ConfigureAwait(false))
            {
                return WithAvailablePorts(options);
            }
        }

        return options;
    }

    internal static CliRuntimeOptions WithAvailablePorts(CliRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var excludedPorts = Enumerable.Range(
                ProtocolDefaults.WebSocketSessionPortRangeStart,
                ProtocolDefaults.WebSocketSessionPortRangeEnd
                - ProtocolDefaults.WebSocketSessionPortRangeStart
                + 1)
            .ToHashSet();
        AddConfiguredPort(excludedPorts, options.DiscoveryPort);
        AddConfiguredPort(excludedPorts, options.WebSocketPort);

        var discoveryPort = options.DiscoveryPort ?? FindAvailableUdpPort(excludedPorts);
        excludedPorts.Add(discoveryPort);
        var webSocketPort = options.WebSocketPort ?? FindAvailableTcpPort(excludedPorts);

        return options with
        {
            DiscoveryPort = discoveryPort,
            WebSocketPort = webSocketPort
        };
    }

    private static bool RequiredPortsAreAvailable(CliRuntimeOptions options)
    {
        var discoveryPort = options.DiscoveryPort ?? ProtocolDefaults.DiscoveryPort;
        var webSocketPort = options.WebSocketPort ?? ProtocolDefaults.WebSocketPort;
        return CanBindUdpPort(discoveryPort)
               && CanBindTcpPort(webSocketPort);
    }

    private static async Task<bool> TargetsPhysicalDeviceAsync(
        CliRuntimeOptions options,
        WorkspaceTestTargetRequest? target,
        CancellationToken cancellationToken)
    {
        if (target is null)
        {
            return false;
        }

        if (DeviceKinds.IsPhysical(target.DeviceKind))
        {
            return true;
        }

        if (DeviceKinds.IsVirtual(target.DeviceKind))
        {
            return false;
        }

        if (string.Equals(
                Path.GetExtension(target.ApplicationPath),
                ".ipa",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(target.DeviceIdentifier))
        {
            return false;
        }

        var devices = new DeviceService(CliRuntime.CreateHostOptions(options));
        var inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var matches = inventory.Devices
            .Where(device => string.Equals(
                device.Identifier,
                target.DeviceIdentifier.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .Where(device => string.IsNullOrWhiteSpace(target.Platform)
                             || string.Equals(
                                 device.Platform,
                                 target.Platform.Trim(),
                                 StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 && DeviceKinds.IsPhysical(matches[0].Kind);
    }

    private static int FindAvailableUdpPort(ISet<int> excludedPorts)
    {
        for (var attempt = 0; attempt < MaximumPortAllocationAttempts; attempt++)
        {
            using var socket = CreateUdpSocket();
            socket.Bind(new IPEndPoint(
                socket.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any,
                0));
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            if (!excludedPorts.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("Unable to allocate an available UDP port for the temporary test host.");
    }

    private static int FindAvailableTcpPort(ISet<int> excludedPorts)
    {
        for (var attempt = 0; attempt < MaximumPortAllocationAttempts; attempt++)
        {
            using var socket = CreateTcpSocket();
            socket.Bind(new IPEndPoint(
                socket.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any,
                0));
            socket.Listen(1);
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            if (!excludedPorts.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("Unable to allocate an available TCP port for the temporary test host.");
    }

    private static bool CanBindUdpPort(int port)
    {
        try
        {
            using var socket = CreateUdpSocket();
            socket.Bind(new IPEndPoint(
                socket.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any,
                port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool CanBindTcpPort(int port)
    {
        try
        {
            using var socket = CreateTcpSocket();
            socket.Bind(new IPEndPoint(
                socket.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any,
                port));
            socket.Listen(1);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static Socket CreateUdpSocket()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
        {
            DualMode = true
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        return socket;
    }

    private static Socket CreateTcpSocket()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        return new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true
        };
    }

    private static void AddConfiguredPort(ISet<int> excludedPorts, int? port)
    {
        if (port.HasValue)
        {
            excludedPorts.Add(port.Value);
        }
    }
}
