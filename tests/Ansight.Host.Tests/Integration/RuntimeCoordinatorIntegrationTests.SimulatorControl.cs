using System.Net.Http.Json;
using Ansight.Host.Tests.TestSupport;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Streaming;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Theory(Timeout = 30000)]
    [InlineData("Ansight_AnxReader_API36", false)]
    [InlineData("emulator-5554", false)]
    [InlineData("Ansight_AnxReader_API36", true)]
    [InlineData("emulator-5554", true)]
    public async Task SimulatorProxyResolvesAvdNameForVideoFramesAndInput(
        string reportedIdentifier, bool duplicateAvd)
    {
        using var environment = new TestEnvironment();
        var pairing = environment.SeedPairingConfig("org.nitri.opentopo", "OpenTopoMapViewer");
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();
        var connected = await SendConnectRequestAsync(pairing);
        Assert.True(connected.Accepted);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(
            $"ws://127.0.0.1:{connected.WebSocketPort}{connected.WebSocketPath}?token={connected.WebSocketToken}"),
            CancellationToken.None);
        string? sessionId = null;
        await TestWait.UntilAsync(() =>
        {
            sessionId = runtime.AppTools.GetConnectedSessionIds().SingleOrDefault();
            return sessionId is not null;
        });
        var profile = JsonSerializer.SerializeToNode(new DeviceAppProfile
        {
            Device = new DeviceProfile { OsName = "android", IsVirtual = true, IsEmulator = true },
            App = new DeviceApplicationProfile { AppId = pairing.AppId, AppName = pairing.AppName }
        }, protocolJson)!.AsObject();
        profile["device"]!["nativeDeviceId"] = reportedIdentifier;
        await SendJsonAsync(socket, profile);
        await TestWait.UntilAsync(() => runtime.Sessions.GetSummaries()
            .Any(session => session.SessionId == sessionId && session.DeviceProfile?.Device?.IsEmulator == true));

        var devices = new List<RemoteRuntimeDevice>
        {
            new("emulator-5554", "Ansight AnxReader API36", "device", true, "Android 16", "android",
                BootIdentifier: "Ansight_AnxReader_API36")
        };
        if (duplicateAvd)
            devices.Add(devices[0] with { Identifier = "emulator-5556" });
        var source = new SimulatorProxyRuntimeSource(devices);
        var frames = new SimulatorProxyFrameSource();
        await using var input = new SimulatorProxyInputSink();
        var video = new SimulatorProxyVideoFactory();
        await using var remote = new RemoteControlServer(source, frames, input,
            requestedPort: 0, webRtcSessionFactory: video);
        await remote.StartAsync();
        runtime.LocalSimulatorControl = remote;
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        using var client = new HttpClient { BaseAddress = Assert.IsType<Uri>(explorer.ExplorerUrl) };
        var prefix = $"api/sessions/{sessionId}/simulator";
        using var offer = await client.PostAsJsonAsync($"{prefix}/offer", new { type = "offer", sdp = "test-offer" });
        using var frame = await client.GetAsync($"{prefix}/frame");
        using var button = await client.PostAsJsonAsync($"{prefix}/button", new { button = "back" });

        if (duplicateAvd && reportedIdentifier != "emulator-5554")
        {
            foreach (var response in new[] { offer, frame, button })
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains("Multiple booted Android emulators", await response.Content.ReadAsStringAsync());
            }
            Assert.Null(video.LastDeviceIdentifier);
            Assert.Null(frames.LastDeviceIdentifier);
            Assert.Null(input.LastDeviceIdentifier);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, offer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        Assert.Equal(HttpStatusCode.OK, button.StatusCode);
        Assert.Equal("emulator-5554", video.LastDeviceIdentifier);
        Assert.Equal("emulator-5554", frames.LastDeviceIdentifier);
        Assert.Equal("emulator-5554", input.LastDeviceIdentifier);
        Assert.Equal(SimulatorProxyFrameSource.FrameBytes, await frame.Content.ReadAsByteArrayAsync());
        var summaries = await client.GetFromJsonAsync<SessionExplorerSummary[]>("api/sessions");
        Assert.Equal("emulator-5554", Assert.Single(summaries!, session => session.SessionId == sessionId)
            .RuntimeDeviceIdentifier);
    }

    [Theory]
    [InlineData("android", "emulator-5554", "Topo_AVD", true, " topo_avd ", "emulator-5554")]
    [InlineData("android", "emulator-5554", "Topo_AVD", false, "Topo_AVD", null)]
    [InlineData("android", "emulator-5554", "Topo_AVD", false, "emulator-5554", null)]
    [InlineData("android", "emulator-5554", "Topo_AVD", true, "Unknown_AVD", null)]
    [InlineData("android", "phone-serial", "Topo_AVD", true, "Topo_AVD", null)]
    [InlineData("ios", "simulator-uuid", "Topo_AVD", true, "Topo_AVD", null)]
    [InlineData("ios", "simulator-uuid", "Topo_AVD", true, "SIMULATOR-UUID", "simulator-uuid")]
    public async Task SimulatorResolutionPreservesExactIdsAndRejectsUnavailableOrNonEmulatorAliases(
        string platform, string identifier, string name, bool booted, string reported, string? expected)
    {
        var source = new SimulatorProxyRuntimeSource(
            [new RemoteRuntimeDevice(identifier, name, "test", booted, "test", platform)]);
        await using var input = new SimulatorProxyInputSink();
        await using var server = new RemoteControlServer(source, new SimulatorProxyFrameSource(), input);

        var resolved = server.TryResolveBootedDeviceIdentifier(reported, out var device, out var error);

        Assert.Equal(expected is not null, resolved);
        Assert.Equal(expected ?? string.Empty, device);
        Assert.Equal(expected is not null, string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task SimulatorResolutionUsesRawAvdIdentifierRatherThanFormattedDisplayName()
    {
        var source = new SimulatorProxyRuntimeSource(
            [new RemoteRuntimeDevice("emulator-5554", "Topo AVD", "Booted", true, "Android 16", "android",
                BootIdentifier: "Topo_AVD")]);
        await using var input = new SimulatorProxyInputSink();
        await using var server = new RemoteControlServer(source, new SimulatorProxyFrameSource(), input);

        Assert.True(server.TryResolveBootedDeviceIdentifier("Topo_AVD", out var serial, out var error), error);
        Assert.Equal("emulator-5554", serial);
        Assert.False(server.TryResolveBootedDeviceIdentifier("Topo AVD", out _, out _));
    }

    private sealed class SimulatorProxyRuntimeSource(IReadOnlyList<RemoteRuntimeDevice> devices) : IRemoteRuntimeSource
    {
        public RemoteRuntimeSnapshot Current { get; } = new(DateTimeOffset.UtcNow, devices, null);
    }

    private sealed class SimulatorProxyFrameSource : ISimulatorFrameSource
    {
        public static byte[] FrameBytes { get; } = [1, 2, 3, 4];
        public string? LastDeviceIdentifier { get; private set; }
        public Task<RemoteFrame> CaptureAsync(string deviceUdid, CancellationToken cancellationToken = default)
        {
            LastDeviceIdentifier = deviceUdid;
            return Task.FromResult(RemoteFrame.Png(FrameBytes));
        }
    }

    private sealed class SimulatorProxyInputSink : ISimulatorInputSink
    {
        public string BackendName => "test";
        public string Status => "ready";
        public string? LastDeviceIdentifier { get; private set; }
        public Task<InputDeliveryResult> SendButtonAsync(RemoteButtonEvent buttonEvent, CancellationToken cancellationToken = default)
        {
            LastDeviceIdentifier = buttonEvent.DeviceUdid;
            return Task.FromResult(InputDeliveryResult.Success(BackendName));
        }
        public Task<InputDeliveryResult> SendAsync(RemotePointerEvent pointerEvent, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<InputDeliveryResult> SendKeyAsync(RemoteKeyEvent keyEvent, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<InputDeliveryResult> SendTextAsync(RemoteTextEvent textEvent, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SimulatorProxyVideoFactory : ISimulatorWebRtcSessionFactory
    {
        public string BackendName => "test";
        public string Status => "ready";
        public string? LastDeviceIdentifier { get; private set; }
        public bool SupportsDevice(string deviceUdid) => deviceUdid == "emulator-5554";
        public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
        {
            LastDeviceIdentifier = deviceUdid;
            return new SimulatorProxyVideoSession(deviceUdid);
        }
    }

    private sealed class SimulatorProxyVideoSession(string deviceUdid) : ISimulatorWebRtcSession
    {
        public string DeviceUdid { get; } = deviceUdid;
        public event EventHandler<WebRtcInputMessageEventArgs>? InputReceived { add { } remove { } }
        public Task<WebRtcSessionDescription> CreateAnswerAsync(WebRtcSessionDescription offer, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription("answer", "test-answer"));
        public bool TrySendMessage(string message) => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
