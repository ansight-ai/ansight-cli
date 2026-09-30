using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.AppInspection;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Recording;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Streaming;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class RemoteControlServerRecordingTests
{
    private const string SessionToken = "recording-test-token";

    [Fact]
    public async Task RecordingEndpoints_ListDescribeAndReturnFrameContent()
    {
        var capturedAt = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        var summary = new RemoteRecordingSummary(
            "session-1",
            "Checkout review",
            "Sample App",
            "iPhone 17 Pro",
            "ios",
            "recorded",
            capturedAt,
            capturedAt.AddSeconds(8),
            8_000,
            1,
            false,
            1,
            1,
            1,
            1);
        var evidence = new RemoteRecordingEvidenceSlice(
            capturedAt,
            capturedAt.AddSeconds(8),
            [new RemoteRecordingLogEntry(capturedAt.AddSeconds(1), "app", "info", "app", "checkout", "Paid", "event-1")],
            [new RemoteRecordingAnnotation(
                "annotation-1",
                capturedAt.AddSeconds(2),
                null,
                "Checkout button",
                "user",
                null,
                null,
                [])],
            [new RemoteRecordingMetricChannel(1, "CPU", "#44AAFF", "%", "gauge", "host", "Trends", "cpu")],
            [new RemoteRecordingMetricSample(1, 42, capturedAt.AddSeconds(3), 0)],
            false,
            false)
        {
            Touches =
            [
                new RemoteRecordingTouchInput(
                    "touch-1",
                    "down",
                    capturedAt.AddSeconds(4),
                    1,
                    0,
                    1,
                    50,
                    100,
                    0.5,
                    0.5,
                    100,
                    200,
                    "window",
                    "points",
                    1),
            ],
        };
        var recordingSource = new FakeRemoteRecordingSource(
            summary,
            new RemoteRecordingFrameSummary("frame-1", capturedAt, 100, 200, "image/png", 4),
            new RemoteRecordingFrame("image/png", [1, 2, 3, 4]),
            evidence);
        await using var server = CreateServer(recordingSource);
        await server.StartAsync();
        using var client = new HttpClient();

        var summaries = await client.GetFromJsonAsync<RemoteRecordingSummary[]>(
            Endpoint(server, "/api/recordings"));
        var details = await client.GetFromJsonAsync<RemoteRecordingDetails>(
            Endpoint(server, "/api/recording", "id=session-1"));
        var frame = await client.GetByteArrayAsync(
            Endpoint(server, "/api/recording/frame", "id=session-1&frame=frame-1"));
        var icon = await client.GetByteArrayAsync(
            Endpoint(server, "/api/recording/icon", "id=session-1"));
        var evidenceResult = await client.GetFromJsonAsync<RemoteRecordingEvidenceSlice>(
            Endpoint(
                server,
                "/api/recording/evidence",
                $"id=session-1&start={Uri.EscapeDataString(capturedAt.ToString("O"))}&end={Uri.EscapeDataString(capturedAt.AddSeconds(8).ToString("O"))}"));

        Assert.Equal(summary, Assert.Single(summaries!));
        Assert.Equal(summary, details!.Recording);
        Assert.Equal("frame-1", Assert.Single(details.Frames).Id);
        Assert.Equal("app", Assert.Single(details.LogStreams).Id);
        Assert.Equal("CPU", Assert.Single(details.MetricChannels).Name);
        Assert.Equal([1, 2, 3, 4], frame);
        Assert.Equal([1, 2, 3, 4], icon);
        Assert.Equal("Paid", Assert.Single(evidenceResult!.Logs).Message);
        Assert.Equal("Checkout button", Assert.Single(evidenceResult.Annotations).Label);
        Assert.Equal(42, Assert.Single(evidenceResult.TelemetrySamples).Value);
        Assert.Equal("touch-1", Assert.Single(evidenceResult.Touches).Id);
    }

    [Fact]
    public async Task RecordingEndpoints_RejectHostsWithoutRecordingAccess()
    {
        await using var server = CreateServer(recordingSource: null);
        await server.StartAsync();
        using var client = new HttpClient();

        using var response = await client.GetAsync(Endpoint(server, "/api/recordings"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task RecordingDataChannel_RequiresBrokerGrantedReadScope(
        bool grantRecordingRead,
        HttpStatusCode expectedStatusCode)
    {
        var webRtcSessionFactory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(CreateMinimalRecordingSource(), webRtcSessionFactory);
        await server.StartAsync();
        using var client = new HttpClient();
        var grantedScopes = grantRecordingRead ? new[] { "view", "recordings.read" } : ["view"];

        var offerBody = JsonSerializer.Serialize(
            new
            {
                deviceUdid = "device-1",
                framesPerSecond = 30,
                type = "offer",
                sdp = "v=0\r\n",
                grantedScopes
            });
        using var offerResponse = await client.PostAsync(
            Endpoint(server, "/api/webrtc/offer"),
            new StringContent(offerBody, Encoding.UTF8, "application/json"));
        Assert.True(
            offerResponse.IsSuccessStatusCode,
            await offerResponse.Content.ReadAsStringAsync());
        var offerAnswer = await offerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var answerScopes = offerAnswer.GetProperty("grantedScopes")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();

        var session = Assert.IsType<FakeWebRtcSession>(webRtcSessionFactory.LastSession);
        session.Receive("""
            {"kind":"recording_request","requestId":"request-1","operation":"list"}
            """);
        var responseMessage = await session.WaitForMessageAsync();
        using var responseDocument = JsonDocument.Parse(responseMessage);

        Assert.Equal((int)expectedStatusCode, responseDocument.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(grantedScopes.OrderBy(scope => scope, StringComparer.Ordinal), answerScopes);
    }

    [Fact]
    public async Task InputDataChannel_ForwardsMouseButtonAsPointerMetadata()
    {
        var inputSink = new FakeRemoteSimulatorInputSink();
        var webRtcSessionFactory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(
            CreateMinimalRecordingSource(),
            webRtcSessionFactory,
            inputSink);
        await server.StartAsync();
        using var client = new HttpClient();
        var offerBody = JsonSerializer.Serialize(
            new
            {
                deviceUdid = "device-1",
                framesPerSecond = 30,
                type = "offer",
                sdp = "v=0\r\n",
                grantedScopes = new[] { "view" },
            });
        using var offerResponse = await client.PostAsync(
            Endpoint(server, "/api/webrtc/offer"),
            new StringContent(offerBody, Encoding.UTF8, "application/json"));
        Assert.True(
            offerResponse.IsSuccessStatusCode,
            await offerResponse.Content.ReadAsStringAsync());

        var session = Assert.IsType<FakeWebRtcSession>(webRtcSessionFactory.LastSession);
        session.Receive("""
            {"udid":"device-1","phase":"down","x":0.25,"y":0.75,"pointerId":1,"timestamp":123,"mouseButton":"right"}
            """);
        var pointerEvent = await inputSink.WaitForPointerAsync();

        Assert.Equal("down", pointerEvent.Phase);
        Assert.Equal("right", pointerEvent.MouseButton);
        Assert.Equal(0.25, pointerEvent.NormalizedX);
        Assert.Equal(0.75, pointerEvent.NormalizedY);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task InspectionDataChannel_RequiresScopeAndBindsRequestToDevice(
        bool grantInspectionRead,
        HttpStatusCode expectedStatusCode)
    {
        var inspectionSource = new FakeRemoteAppInspectionSource();
        var webRtcSessionFactory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(
            CreateMinimalRecordingSource(),
            webRtcSessionFactory,
            appInspectionSource: inspectionSource);
        await server.StartAsync();
        using var client = new HttpClient();
        var grantedScopes = grantInspectionRead ? new[] { "view", "inspect.read" } : ["view"];
        var offerBody = JsonSerializer.Serialize(
            new
            {
                deviceUdid = "device-1",
                framesPerSecond = 30,
                type = "offer",
                sdp = "v=0\r\n",
                grantedScopes
            });
        using var offerResponse = await client.PostAsync(
            Endpoint(server, "/api/webrtc/offer"),
            new StringContent(offerBody, Encoding.UTF8, "application/json"));
        Assert.True(offerResponse.IsSuccessStatusCode, await offerResponse.Content.ReadAsStringAsync());

        var session = Assert.IsType<FakeWebRtcSession>(webRtcSessionFactory.LastSession);
        session.Receive("""
            {
              "kind":"inspection_request",
              "requestId":"request-1",
              "operation":"files.list",
              "sessionId":"app-session-1",
              "arguments":{"path":"logs"}
            }
            """);
        var responseMessage = await session.WaitForMessageAsync();
        using var responseDocument = JsonDocument.Parse(responseMessage);

        Assert.Equal("inspection_response", responseDocument.RootElement.GetProperty("kind").GetString());
        Assert.Equal((int)expectedStatusCode, responseDocument.RootElement.GetProperty("statusCode").GetInt32());
        if (grantInspectionRead)
        {
            var invocation = await inspectionSource.WaitForInvocationAsync();
            Assert.Equal("device-1", invocation.DeviceUdid);
            Assert.Equal("app-session-1", invocation.Request.SessionId);
            Assert.Equal("files.list", invocation.Request.Operation);
            var content = Convert.FromBase64String(responseDocument.RootElement.GetProperty("data").GetString()!);
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(content));
        }
    }

    private static RemoteControlServer CreateServer(
        IRemoteRecordingSource? recordingSource,
        ISimulatorWebRtcSessionFactory? webRtcSessionFactory = null,
        ISimulatorInputSink? inputSink = null,
        IRemoteAppInspectionSource? appInspectionSource = null)
        => new(
            new FakeRuntimeSource(),
            new FakeRemoteSimulatorFrameSource(),
            inputSink ?? new FakeRemoteSimulatorInputSink(),
            requestedPort: 0,
            sessionToken: SessionToken,
            webRtcSessionFactory: webRtcSessionFactory,
            recordingSource: recordingSource,
            appInspectionSource: appInspectionSource);

    private static FakeRemoteRecordingSource CreateMinimalRecordingSource()
    {
        var capturedAt = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        var summary = new RemoteRecordingSummary(
            "session-1",
            "Checkout review",
            "Sample App",
            "iPhone 17 Pro",
            "ios",
            "recorded",
            capturedAt,
            capturedAt.AddSeconds(1),
            1_000,
            1,
            false);
        return new FakeRemoteRecordingSource(
            summary,
            new RemoteRecordingFrameSummary("frame-1", capturedAt, 100, 200, "image/png", 1),
            new RemoteRecordingFrame("image/png", [1]),
            new RemoteRecordingEvidenceSlice(
                capturedAt,
                capturedAt.AddSeconds(1),
                [],
                [],
                [],
                [],
                false,
                false));
    }

    private static string Endpoint(RemoteControlServer server, string path, string? query = null)
    {
        var separator = string.IsNullOrWhiteSpace(query) ? string.Empty : $"&{query}";
        return $"http://127.0.0.1:{server.Port}{path}?token={SessionToken}{separator}";
    }

    private sealed class FakeRuntimeSource : IRemoteRuntimeSource
    {
        public RemoteRuntimeSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            [new RemoteRuntimeDevice("device-1", "iPhone", "Booted", true, "iOS 26", "ios")],
            null);
    }

    private sealed class FakeRemoteRecordingSource : IRemoteRecordingSource
    {
        private readonly RemoteRecordingSummary summary;
        private readonly RemoteRecordingFrameSummary frameSummary;
        private readonly RemoteRecordingFrame frame;
        private readonly RemoteRecordingEvidenceSlice evidence;

        public FakeRemoteRecordingSource(
            RemoteRecordingSummary summary,
            RemoteRecordingFrameSummary frameSummary,
            RemoteRecordingFrame frame,
            RemoteRecordingEvidenceSlice evidence)
        {
            this.summary = summary;
            this.frameSummary = frameSummary;
            this.frame = frame;
            this.evidence = evidence;
        }

        public Task<IReadOnlyList<RemoteRecordingSummary>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteRecordingSummary>>([summary]);

        public Task<RemoteRecordingDetails?> GetAsync(
            string recordingId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteRecordingDetails?>(
                string.Equals(recordingId, summary.Id, StringComparison.Ordinal)
                    ? new RemoteRecordingDetails(
                        summary,
                        [frameSummary],
                        [new RemoteRecordingLogStreamSummary("app", "application", "App", "complete", 1)],
                        evidence.MetricChannels)
                    : null);

        public Task<RemoteRecordingFrame?> GetFrameAsync(
            string recordingId,
            string frameId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteRecordingFrame?>(
                string.Equals(recordingId, summary.Id, StringComparison.Ordinal)
                && string.Equals(frameId, frameSummary.Id, StringComparison.Ordinal)
                    ? frame
                    : null);

        public Task<RemoteRecordingFrame?> GetAppIconAsync(
            string recordingId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteRecordingFrame?>(
                string.Equals(recordingId, summary.Id, StringComparison.Ordinal)
                    ? frame
                    : null);

        public Task<RemoteRecordingEvidenceSlice?> GetEvidenceAsync(
            string recordingId,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maximumLogCount,
            int maximumMetricSampleCount,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteRecordingEvidenceSlice?>(
                string.Equals(recordingId, summary.Id, StringComparison.Ordinal)
                    ? evidence
                    : null);
    }

    private sealed class FakeRemoteAppInspectionSource : IRemoteAppInspectionSource
    {
        private readonly TaskCompletionSource<Invocation> invocationReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteAppInspectionResult> InvokeAsync(
            string deviceUdid,
            RemoteAppInspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            invocationReceived.TrySetResult(new Invocation(deviceUdid, request));
            return Task.FromResult(RemoteAppInspectionResult.Json(Encoding.UTF8.GetBytes("{\"ok\":true}")));
        }

        public Task<Invocation> WaitForInvocationAsync()
            => invocationReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public sealed record Invocation(string DeviceUdid, RemoteAppInspectionRequest Request);
    }

    private sealed class FakeWebRtcSessionFactory : ISimulatorWebRtcSessionFactory
    {
        public string BackendName => "fake";

        public string Status => "Ready";

        public ISimulatorWebRtcSession? LastSession { get; private set; }

        public bool SupportsDevice(string deviceUdid) => true;

        public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
        {
            var session = new FakeWebRtcSession(deviceUdid);
            LastSession = session;
            return session;
        }
    }

    private sealed class FakeWebRtcSession : ISimulatorWebRtcSession
    {
        private readonly TaskCompletionSource<string> messageSent = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeWebRtcSession(string deviceUdid)
        {
            DeviceUdid = deviceUdid;
        }

        public event EventHandler<WebRtcInputMessageEventArgs>? InputReceived;

        public string DeviceUdid { get; }

        public Task<WebRtcSessionDescription> CreateAnswerAsync(
            WebRtcSessionDescription offer,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription("answer", offer.Sdp));

        public bool TrySendMessage(string message)
        {
            messageSent.TrySetResult(message);
            return true;
        }

        public void Receive(string message)
            => InputReceived?.Invoke(this, new WebRtcInputMessageEventArgs(message));

        public Task<string> WaitForMessageAsync()
            => messageSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
