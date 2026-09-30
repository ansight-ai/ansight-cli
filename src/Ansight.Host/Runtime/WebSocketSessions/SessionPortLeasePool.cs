namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ansight.Pairing;

internal sealed class SessionPortLeasePool
{
    private readonly object gate = new();
    private readonly HashSet<int> leasedPorts = [];

    public bool TryLeaseListener(out int port, out TcpListener listener, out string error)
    {
        var rangeStart = ProtocolDefaults.WebSocketSessionPortRangeStart;
        var rangeEnd = ProtocolDefaults.WebSocketSessionPortRangeEnd;
        if (rangeStart < IPEndPoint.MinPort || rangeEnd > IPEndPoint.MaxPort || rangeStart > rangeEnd)
        {
            port = 0;
            listener = null!;
            error = $"Invalid WebSocket port range configured ({rangeStart}-{rangeEnd}).";
            return false;
        }

        var rangeSize = rangeEnd - rangeStart + 1;
        var offset = RandomNumberGenerator.GetInt32(rangeSize);
        lock (gate)
        {
            for (var attempt = 0; attempt < rangeSize; attempt++)
            {
                var candidate = rangeStart + ((offset + attempt) % rangeSize);
                if (leasedPorts.Contains(candidate))
                {
                    continue;
                }

                try
                {
                    listener = StartListener(candidate, backlog: 4);
                }
                catch (SocketException)
                {
                    continue;
                }

                leasedPorts.Add(candidate);
                port = candidate;
                error = string.Empty;
                return true;
            }
        }

        port = 0;
        listener = null!;
        error = $"No available WebSocket ports in configured range {rangeStart}-{rangeEnd}.";
        return false;
    }

    public void Release(int port)
    {
        lock (gate)
        {
            leasedPorts.Remove(port);
        }
    }

    public static TcpListener StartListener(int port, int backlog)
    {
        if (Socket.OSSupportsIPv6)
        {
            var ipv6Listener = new TcpListener(IPAddress.IPv6Any, port);
            try
            {
                ipv6Listener.Server.DualMode = true;
                ipv6Listener.Start(backlog);
                return ipv6Listener;
            }
            catch (Exception ex) when (IsUnsupportedIpv6ListenerException(ex))
            {
                ipv6Listener.Dispose();
            }
        }

        var ipv4Listener = new TcpListener(IPAddress.Any, port);
        ipv4Listener.Start(backlog);
        return ipv4Listener;
    }

    private static bool IsUnsupportedIpv6ListenerException(Exception exception)
        => exception is PlatformNotSupportedException
           || exception is SocketException
           {
               SocketErrorCode: SocketError.AddressFamilyNotSupported
                   or SocketError.AddressNotAvailable
                   or SocketError.OperationNotSupported
                   or SocketError.ProtocolNotSupported
           };
}
