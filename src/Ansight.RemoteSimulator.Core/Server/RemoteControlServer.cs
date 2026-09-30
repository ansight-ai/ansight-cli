using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Access;
using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;
using Ansight.RemoteSimulator.Core.AppInspection;
using Ansight.RemoteSimulator.Core.Companion;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Recording;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server.WebRtc;
using Ansight.RemoteSimulator.Core.Simulator.Apple;
using Ansight.RemoteSimulator.Core.Streaming;
using Ansight.RemoteSimulator.Core.WebRtc;
using static Ansight.RemoteSimulator.Core.Server.RemoteControlHttpTransport;

namespace Ansight.RemoteSimulator.Core.Server;

public sealed partial class RemoteControlServer : IAsyncDisposable
{
    private const int MaximumRequestBodyBytes = 64 * 1024;
    private const int RecordingResponseChunkBytes = 12 * 1024;
    private const int MaximumRecordingResponseBytes = 20 * 1024 * 1024;
    private const string RecordingReadScope = "recordings.read";
    private const string InspectionReadScope = "inspect.read";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRemoteRuntimeSource runtimeSource;
    private readonly ISimulatorFrameSource frameSource;
    private readonly ISimulatorInputSink inputSink;
    private readonly ISimulatorWebRtcSessionFactory? webRtcSessionFactory;
    private readonly IRemoteRecordingSource? recordingSource;
    private readonly IRemoteAccessAuthorizer? accessAuthorizer;
    private readonly IRemoteDeviceLifecycleSource? deviceLifecycleSource;
    private readonly IRemoteAnnotationSource? annotationSource;
    private readonly IRemoteAgentChatSource? agentChatSource;
    private readonly IRemoteAppInspectionSource? appInspectionSource;
    private readonly IRemoteDeviceLocationSource? deviceLocationSource;
    private readonly bool allowUnauthenticatedLoopback;
    private readonly int requestedPort;
    private readonly string sessionToken;
    private readonly ConcurrentDictionary<string, SessionRegistration> webRtcSessions = new();
    private TcpListener? listener;
    private CancellationTokenSource? serverCancellation;
    private Task? acceptTask;

    public RemoteControlServer(
        SimulatorTracker tracker,
        ISimulatorFrameSource frameSource,
        ISimulatorInputSink inputSink,
        int requestedPort = 8765,
        string? sessionToken = null,
        ISimulatorWebRtcSessionFactory? webRtcSessionFactory = null,
        IRemoteRecordingSource? recordingSource = null,
        IRemoteAccessAuthorizer? accessAuthorizer = null,
        bool allowUnauthenticatedLoopback = true,
        IRemoteDeviceLifecycleSource? deviceLifecycleSource = null,
        IRemoteAnnotationSource? annotationSource = null,
        IRemoteAgentChatSource? agentChatSource = null,
        IRemoteAppInspectionSource? appInspectionSource = null,
        IRemoteDeviceLocationSource? deviceLocationSource = null)
        : this(
            new SimulatorRuntimeSource(tracker ?? throw new ArgumentNullException(nameof(tracker))),
            frameSource,
            inputSink,
            requestedPort,
            sessionToken,
            webRtcSessionFactory,
            recordingSource,
            accessAuthorizer,
            allowUnauthenticatedLoopback,
            deviceLifecycleSource,
            annotationSource,
            agentChatSource,
            appInspectionSource,
            deviceLocationSource)
    {
    }

    public RemoteControlServer(
        IRemoteRuntimeSource runtimeSource,
        ISimulatorFrameSource frameSource,
        ISimulatorInputSink inputSink,
        int requestedPort = 8765,
        string? sessionToken = null,
        ISimulatorWebRtcSessionFactory? webRtcSessionFactory = null,
        IRemoteRecordingSource? recordingSource = null,
        IRemoteAccessAuthorizer? accessAuthorizer = null,
        bool allowUnauthenticatedLoopback = true,
        IRemoteDeviceLifecycleSource? deviceLifecycleSource = null,
        IRemoteAnnotationSource? annotationSource = null,
        IRemoteAgentChatSource? agentChatSource = null,
        IRemoteAppInspectionSource? appInspectionSource = null,
        IRemoteDeviceLocationSource? deviceLocationSource = null)
    {
        this.runtimeSource = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
        this.frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        this.inputSink = inputSink ?? throw new ArgumentNullException(nameof(inputSink));
        if (requestedPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedPort));
        }

        this.requestedPort = requestedPort;
        this.sessionToken = sessionToken ?? CreateSessionToken();
        this.webRtcSessionFactory = webRtcSessionFactory;
        this.recordingSource = recordingSource;
        this.accessAuthorizer = accessAuthorizer;
        this.allowUnauthenticatedLoopback = allowUnauthenticatedLoopback;
        this.deviceLifecycleSource = deviceLifecycleSource;
        this.annotationSource = annotationSource;
        this.agentChatSource = agentChatSource;
        this.appInspectionSource = appInspectionSource;
        this.deviceLocationSource = deviceLocationSource;
    }

    public bool IsRunning => listener is not null;

    public event EventHandler? CompanionConnectionsChanged;

    public int Port { get; private set; }

    public string LoopbackBaseUrl => Port == 0
        ? string.Empty
        : $"http://127.0.0.1:{Port}/?token={Uri.EscapeDataString(sessionToken)}";

    public bool TryResolveBootedDeviceIdentifier(
        string reportedIdentifier,
        out string deviceIdentifier,
        out string error)
    {
        deviceIdentifier = string.Empty;
        var identifier = reportedIdentifier.Trim();
        var devices = runtimeSource.Current.Devices;
        var exactDevice = devices.FirstOrDefault(device =>
            string.Equals(device.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
        if (exactDevice is not null)
        {
            if (!exactDevice.IsBooted)
            {
                error = $"The selected device '{identifier}' is not booted.";
                return false;
            }

            deviceIdentifier = exactDevice.Identifier;
            error = string.Empty;
            return true;
        }

        // Android SDK profiles report an AVD name, while device transports use
        // the current emulator serial. Never choose arbitrarily between copies
        // of the same AVD or match a physical device's display name.
        var emulators = devices.Where(device =>
                device.IsBooted
                && string.Equals(device.Platform, "android", StringComparison.OrdinalIgnoreCase)
                && device.Identifier.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    string.IsNullOrWhiteSpace(device.BootIdentifier) ? device.Name : device.BootIdentifier,
                    identifier,
                    StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (emulators.Length == 1)
        {
            deviceIdentifier = emulators[0].Identifier;
            error = string.Empty;
            return true;
        }

        error = emulators.Length > 1
            ? $"Multiple booted Android emulators have AVD name '{identifier}'. A device serial is required for Simulator control."
            : $"Could not resolve live simulator device '{identifier}' to a booted device identifier.";
        return false;
    }

    public IReadOnlyList<RemoteCompanionConnection> GetConnectedCompanionConnections()
    {
        return webRtcSessions
            .Select(pair => pair.Value.CompanionDevice is null || !pair.Value.ConnectedAtUtc.HasValue
                ? null
                : new RemoteCompanionConnection(
                    pair.Key,
                    pair.Value.CompanionDevice,
                    pair.Value.Session.DeviceUdid,
                    pair.Value.ConnectedAtUtc.Value))
            .Where(static connection => connection is not null)
            .Cast<RemoteCompanionConnection>()
            .GroupBy(connection => connection.Device.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(connection => connection.ConnectedAtUtc).First())
            .OrderBy(connection => connection.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Device.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<bool> DisconnectCompanionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!webRtcSessions.ContainsKey(sessionId.Trim()))
        {
            return Task.FromResult(false);
        }

        return DisconnectCompanionCoreAsync(sessionId.Trim());
    }

    public async Task<int> DisconnectAllCompanionsAsync()
    {
        var sessionIds = webRtcSessions.Keys.ToArray();
        foreach (var sessionId in sessionIds)
        {
            await CloseWebRtcSessionAsync(sessionId).ConfigureAwait(false);
        }

        return sessionIds.Length;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (listener is not null)
        {
            return Task.CompletedTask;
        }

        var nextListener = new TcpListener(IPAddress.Any, requestedPort);
        nextListener.Start(32);
        listener = nextListener;
        Port = ((IPEndPoint)nextListener.LocalEndpoint).Port;
        serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acceptTask = AcceptConnectionsAsync(nextListener, serverCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task<bool> DisconnectCompanionCoreAsync(string sessionId)
    {
        await CloseWebRtcSessionAsync(sessionId).ConfigureAwait(false);
        return true;
    }

    public async Task StopAsync()
    {
        if (listener is null)
        {
            return;
        }

        var activeListener = listener;
        listener = null;
        activeListener.Stop();
        if (serverCancellation is not null)
        {
            await serverCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an expected completion path.
            }
            catch (SocketException)
            {
                // This exception is an expected fallback for the best-effort operation.
            }
        }

        serverCancellation?.Dispose();
        serverCancellation = null;
        acceptTask = null;
        foreach (var sessionId in webRtcSessions.Keys)
        {
            await CloseWebRtcSessionAsync(sessionId).ConfigureAwait(false);
        }
        Port = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptConnectionsAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _ = HandleClientSafelyAsync(client, cancellationToken);
        }
    }

}
