namespace Ansight.Host.Discovery;

using Ansight.Host;

[Export(typeof(IUdpPairingServer))]
internal sealed class UdpPairingServer : IUdpPairingServer
{
    private readonly IIdentityStore hostIdentityStore;
    private readonly IPairingConfigCache pairingCache;
    private readonly IKnownAppStore knownAppStore;
    private readonly WebSocketSessionManager webSocketSessionManager;
    private readonly IRuntimeState runtimeState;
    private readonly SemaphoreSlim responseSendGate = new(1, 1);

    [ImportingConstructor]
    public UdpPairingServer(
        IIdentityStore hostIdentityStore,
        IPairingConfigCache pairingCache,
        IKnownAppStore knownAppStore,
        WebSocketSessionManager webSocketSessionManager,
        IRuntimeState runtimeState)
    {
        this.hostIdentityStore = hostIdentityStore;
        this.pairingCache = pairingCache;
        this.knownAppStore = knownAppStore;
        this.webSocketSessionManager = webSocketSessionManager;
        this.runtimeState = runtimeState;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        UdpClient udpClient;
        try
        {
            udpClient = CreateBoundListener();
            runtimeState.SetServerStatus(
                true,
                $"UDP enrollment listener started on port {ProtocolDefaults.DiscoveryPort}.");
        }
        catch (SocketException ex)
        {
            var statusMessage = ListenerPortWarning.TryCreateMessage(
                ex,
                "enrollment listener",
                $"UDP port {ProtocolDefaults.DiscoveryPort}",
                out var warning)
                ? warning
                : $"Unable to start UDP enrollment listener: {ex.Message}";
            runtimeState.SetServerStatus(false, statusMessage);
            return;
        }

        using (udpClient)
        {
            var requestTasks = new List<Task>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    UdpReceiveResult receiveResult;
                    try
                    {
                        receiveResult = await udpClient.ReceiveAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    requestTasks.RemoveAll(task => task.IsCompleted);
                    requestTasks.Add(HandleAndRespondAsync(udpClient, receiveResult));
                }
            }
            catch (SocketException ex)
            {
                runtimeState.SetServerStatus(false, $"UDP enrollment listener error: {ex.Message}");
            }
            finally
            {
                await Task.WhenAll(requestTasks);
                runtimeState.SetServerStatus(false, "UDP enrollment listener stopped.");
            }
        }
    }

    private async Task HandleAndRespondAsync(
        UdpClient udpClient,
        UdpReceiveResult receiveResult)
    {
        try
        {
            var response = await TryHandleRequestAsync(
                receiveResult.RemoteEndPoint,
                receiveResult.Buffer);
            if (response is null)
            {
                return;
            }

            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonUtil.Compact);
            await responseSendGate.WaitAsync();
            try
            {
                await udpClient.SendAsync(
                    responseBytes,
                    responseBytes.Length,
                    receiveResult.RemoteEndPoint);
            }
            finally
            {
                responseSendGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            runtimeState.LogHost(
                $"UDP enrollment request from {receiveResult.RemoteEndPoint.Address} failed: {ex.Message}");
        }
    }

    private async Task<ConnectResponse?> TryHandleRequestAsync(
        IPEndPoint remoteEndPoint,
        byte[] payload)
    {
        EnrollmentConnectRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EnrollmentConnectRequest>(payload, JsonUtil.Compact);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request is null
            || !string.Equals(request.Type, EnrollmentConnectRequest.MessageType, StringComparison.Ordinal)
            || request.Ver != 2
            || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return null;
        }

        var hostIdentity = hostIdentityStore.Current;
        var connectionAttempt = new DeviceConnectionAttempt(
            request.InviteId,
            request.AppId,
            request.DeviceId,
            request.DeviceName,
            request.AccessToken);
        DeviceConnectionAuthorization authorization;
        if (string.Equals(
                request.EnrollmentMode,
                EnrollmentConnectRequest.LocalMode,
                StringComparison.Ordinal))
        {
            if (!IsLoopback(remoteEndPoint.Address))
            {
                authorization = DeviceConnectionAuthorization.Reject(
                    "LocalEnrollmentUnavailable",
                    "Automatic developer enrollment is available only to apps running on this computer.");
            }
            else
            {
                authorization = pairingCache.AuthorizeLocalConnection(
                    connectionAttempt,
                    hostIdentity);
            }
        }
        else if (string.Equals(
                     request.EnrollmentMode,
                     EnrollmentConnectRequest.InviteMode,
                     StringComparison.Ordinal))
        {
            authorization = pairingCache.AuthorizeConnection(
                connectionAttempt,
                hostIdentity);
        }
        else
        {
            authorization = DeviceConnectionAuthorization.Reject(
                "EnrollmentModeUnsupported",
                "The app requested an unsupported enrollment mode.");
        }

        if (!authorization.Accepted || authorization.Grant is null)
        {
            PublishPairingEvent(
                RuntimePairingEventKind.PairingRejected,
                request,
                remoteEndPoint,
                sessionId: string.Empty,
                authorization.ReasonCode,
                authorization.ReasonMessage);
            return CreateRejectedResponse(
                request,
                hostIdentity,
                authorization.ReasonCode,
                authorization.ReasonMessage);
        }

        var sessionId = runtimeState.ReserveSessionId(request.AppId, request.ProcessSessionId);
        PublishPairingEvent(
            RuntimePairingEventKind.DiscoveryReceived,
            request,
            remoteEndPoint,
            sessionId,
            reasonCode: null,
            "Enrollment connection request received.");

        var session = await webSocketSessionManager.IssueSessionAsync(
            sessionId,
            PairingSessionAuthorization.FromGrant(authorization.Grant),
            CancellationToken.None);
        if (session is null)
        {
            const string reasonCode = "WebSocketUnavailable";
            const string reasonMessage =
                "Ansight registered the device but could not open the live session. Try again.";
            PublishPairingEvent(
                RuntimePairingEventKind.PairingRejected,
                request,
                remoteEndPoint,
                sessionId,
                reasonCode,
                reasonMessage);
            return CreateRejectedResponse(request, hostIdentity, reasonCode, reasonMessage);
        }

        runtimeState.CreateSession(
            request.AppId,
            request.DeviceName,
            remoteEndPoint.Address,
            request.InviteId,
            request.ProcessSessionId,
            sessionId);
        knownAppStore.EnsureKnown(
            request.AppId,
            request.AppId,
            iconGlyph: string.Empty,
            seenAtUtc: DateTimeOffset.UtcNow);
        runtimeState.SetSessionStatus(
            sessionId,
            "Connect Accepted",
            $"Clear-text WebSocket endpoint issued on port {session.Port}.");
        PublishPairingEvent(
            RuntimePairingEventKind.PairingAccepted,
            request,
            remoteEndPoint,
            sessionId,
            "Ok",
            "Device registered and WebSocket endpoint issued.",
            authorization.IsNewRegistration);
        runtimeState.LogHost(
            $"ENROLLMENT_CONNECT from {remoteEndPoint.Address} appId={request.AppId} deviceId={request.DeviceId} accepted=true");

        return new ConnectResponse
        {
            RequestId = request.RequestId,
            Accepted = true,
            Reason = "Ok",
            ReasonMessage = "Device registered.",
            HostId = hostIdentity.HostId,
            HostName = hostIdentity.HostName,
            HostWifiName = AddressDiscovery.ResolveWifiNetworkName(AddressDiscovery.Capture()),
            Message = $"Connection accepted. WebSocket issued on port {session.Port}.",
            WebSocketPort = session.Port,
            WebSocketPath = session.Path,
            WebSocketToken = session.Token
        };
    }

    private static ConnectResponse CreateRejectedResponse(
        EnrollmentConnectRequest request,
        RuntimeIdentity hostIdentity,
        string reasonCode,
        string reasonMessage)
    {
        return new ConnectResponse
        {
            RequestId = request.RequestId,
            Accepted = false,
            Reason = reasonCode,
            ReasonMessage = reasonMessage,
            HostId = hostIdentity.HostId,
            HostName = hostIdentity.HostName,
            Message = reasonMessage
        };
    }

    private void PublishPairingEvent(
        RuntimePairingEventKind kind,
        EnrollmentConnectRequest request,
        IPEndPoint remoteEndPoint,
        string sessionId,
        string? reasonCode,
        string? message,
        bool isFirstConnection = false)
    {
        runtimeState.PublishRuntimeEvent(
            new RuntimePairingEvent(
                DateTimeOffset.UtcNow,
                kind,
                sessionId,
                request.AppId,
                request.DeviceName,
                remoteEndPoint.Address.ToString(),
                request.InviteId,
                reasonCode,
                message,
                isFirstConnection));
    }

    private static UdpClient CreateBoundListener()
    {
        if (Socket.OSSupportsIPv6)
        {
            var ipv6Client = new UdpClient(AddressFamily.InterNetworkV6);
            try
            {
                ipv6Client.Client.DualMode = true;
                ipv6Client.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress,
                    false);
                ipv6Client.Client.Bind(
                    new IPEndPoint(IPAddress.IPv6Any, ProtocolDefaults.DiscoveryPort));
                return ipv6Client;
            }
            catch (Exception ex) when (IsUnsupportedIpv6ListenerException(ex))
            {
                ipv6Client.Dispose();
            }
        }

        var ipv4Client = new UdpClient(AddressFamily.InterNetwork);
        ipv4Client.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            false);
        ipv4Client.Client.Bind(
            new IPEndPoint(IPAddress.Any, ProtocolDefaults.DiscoveryPort));
        return ipv4Client;
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

    private static bool IsLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.IsIPv4MappedToIPv6
               && IPAddress.IsLoopback(address.MapToIPv4());
    }
}
