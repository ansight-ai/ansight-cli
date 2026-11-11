using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.AppInspection;
using Ansight.RemoteSimulator.Core.Annotations;
using Ansight.RemoteSimulator.Core.Devices;
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

    [Theory]
    [InlineData(false, "batch-1", HttpStatusCode.Forbidden)]
    [InlineData(true, "batch-1", HttpStatusCode.OK)]
    [InlineData(true, "wrong-batch", HttpStatusCode.NotFound)]
    [InlineData(true, "", HttpStatusCode.BadRequest)]
    public async Task AnnotationFrame_DoesNotRequireRecordingServiceAndPreservesScopeAndBatchChecks(
        bool grantRead,
        string batchId,
        HttpStatusCode expectedStatus)
    {
        var factory = new FakeWebRtcSessionFactory();
        var annotations = new FakeAnnotationFrameSource();
        await using var server = CreateServer(null, factory, annotationSource: annotations);
        await server.StartAsync();

        using var client = new HttpClient();
        var offerBody = JsonSerializer.Serialize(new
        {
            deviceUdid = "device-1",
            framesPerSecond = 30,
            type = "offer",
            sdp = "v=0\r\n",
            grantedScopes = grantRead ? new[] { "view", "recordings.read" } : ["view"]
        });
        using var offer = await client.PostAsync(Endpoint(server, "/api/webrtc/offer"),
            new StringContent(offerBody, Encoding.UTF8, "application/json"));
        Assert.True(offer.IsSuccessStatusCode, await offer.Content.ReadAsStringAsync());

        var session = Assert.IsType<FakeWebRtcSession>(factory.LastSession);
        session.Receive(JsonSerializer.Serialize(new
        {
            kind = "recording_request",
            requestId = "annotation-request",
            operation = "annotation_frame",
            recordingId = "live-session",
            frameId = "frame-1",
            batchId
        }));

        using var response = JsonDocument.Parse(await session.WaitForMessageAsync());

        Assert.Equal((int)expectedStatus, response.RootElement.GetProperty("statusCode").GetInt32());

        if (expectedStatus == HttpStatusCode.OK)
        {
            Assert.Equal("device-1", annotations.RequestedDevice);
            Assert.Equal(new byte[] { 1, 2, 3 },
                Convert.FromBase64String(response.RootElement.GetProperty("data").GetString()!));
        }
        else if (!grantRead)
        {
            Assert.Null(annotations.RequestedDevice);
        }

        var state = await client.GetFromJsonAsync<JsonElement>(Endpoint(server, "/api/state"));
        Assert.True(state.GetProperty("supportsAnnotationFrames").GetBoolean());
        Assert.False(state.GetProperty("supportsRecordingReview").GetBoolean());
        Assert.False(state.GetProperty("supportsAgentSubmission").GetBoolean());
    }

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
    [InlineData(false, "list", HttpStatusCode.Forbidden)]
    [InlineData(true, "list", HttpStatusCode.OK)]
    [InlineData(false, "network.list", HttpStatusCode.Forbidden)]
    [InlineData(true, "network.list", HttpStatusCode.OK)]
    [InlineData(false, "artifacts.download", HttpStatusCode.Forbidden)]
    [InlineData(true, "artifacts.download", HttpStatusCode.OK)]
    [InlineData(false, "trends.history", HttpStatusCode.Forbidden)]
    [InlineData(true, "trends.history", HttpStatusCode.OK)]
    public async Task RecordingDataChannel_RequiresBrokerGrantedReadScope(
        bool grantRecordingRead,
        string operation,
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
        session.Receive(JsonSerializer.Serialize(new
        {
            kind = "recording_request", requestId = "request-1", operation,
            recordingId = "recording-1", arguments = new { }
        }));

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
                grantedScopes = new[] { "view", "control" },
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputKinds_RequireControlScope(bool grantControl)
    {
        var sink = new FakeRemoteSimulatorInputSink();
        var factory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(CreateMinimalRecordingSource(), factory, sink);
        await server.StartAsync();
        using var client = new HttpClient();
        using var offer = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/offer"), new
        {
            deviceUdid = "device-1", framesPerSecond = 30, type = "offer", sdp = "v=0\r\n",
            grantedScopes = grantControl ? new[] { "view", "control" } : new[] { "view" }
        });
        Assert.True(offer.IsSuccessStatusCode, await offer.Content.ReadAsStringAsync());
        var session = Assert.IsType<FakeWebRtcSession>(factory.LastSession);
        session.Receive("""{"udid":"device-1","phase":"down","x":0.2,"y":0.3,"pointerId":1}""");
        session.Receive("""{"udid":"device-1","button":"home"}""");
        session.Receive("""{"udid":"device-1","usageCode":4,"phase":"down"}""");
        session.Receive("""{"udid":"device-1","text":"forbidden"}""");
        // The reply is a queue barrier: all earlier input has been processed.
        session.Receive("""{"kind":"recording_request","requestId":"barrier","operation":"list"}""");
        await session.WaitForMessageAsync();
        Assert.Equal(grantControl ? 4 : 0, sink.DeliveredCount);
    }

    [Fact]
    public async Task SessionCapacity_IsReleasedWhenAPeerCloses()
    {
        var factory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(CreateMinimalRecordingSource(), factory);
        await server.StartAsync();
        using var client = new HttpClient();
        var body = new { deviceUdid = "device-1", framesPerSecond = 30, type = "offer", sdp = "v=0\r\n", grantedScopes = new[] { "view" } };
        string? firstSessionId = null;
        for (var index = 0; index < 8; index++)
        {
            using var offer = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/offer"), body);
            Assert.True(offer.IsSuccessStatusCode, await offer.Content.ReadAsStringAsync());
            var answer = await offer.Content.ReadFromJsonAsync<JsonElement>();
            firstSessionId ??= answer.GetProperty("sessionId").GetString();
        }

        using var overflow = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/offer"), body);
        Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
        await server.DisconnectCompanionAsync(firstSessionId!);
        using var replacement = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/offer"), body);
        Assert.True(replacement.IsSuccessStatusCode);
    }

    [Fact]
    public async Task RemoteSession_ExpiresDespiteDataChannelKeepalive_AndRejectsWrongLease()
    {
        var factory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(CreateMinimalRecordingSource(), factory);
        await server.StartAsync();
        using var client = new HttpClient();
        var leaseId = Guid.NewGuid().ToString("D");
        using var offer = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/offer"), new
        {
            deviceUdid = "device-1", framesPerSecond = 30, type = "offer", sdp = "v=0\r\n",
            grantedScopes = new[] { "view" }, authorizationLeaseId = leaseId,
            authorizationExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(1)
        });
        Assert.True(offer.IsSuccessStatusCode, await offer.Content.ReadAsStringAsync());
        var answer = await offer.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = answer.GetProperty("sessionId").GetString();
        var session = Assert.IsType<FakeWebRtcSession>(factory.LastSession);
        session.Receive("""{"kind":"companion_keepalive"}""");
        using var localKeepalive = await client.PostAsync(
            Endpoint(server, "/api/webrtc/keepalive") + "&session=" + Uri.EscapeDataString(sessionId!), null);
        Assert.Equal(HttpStatusCode.Forbidden, localKeepalive.StatusCode);
        using var wrongLease = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/renew"), new
        {
            sessionId, authorizationLeaseId = Guid.NewGuid().ToString("D"),
            authorizationExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(90)
        });
        Assert.Equal(HttpStatusCode.Forbidden, wrongLease.StatusCode);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await server.RemoveExpiredWebRtcSessionsAsync();
        Assert.True(session.IsDisposed);
        using var expiredRenewal = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/renew"), new
        {
            sessionId, authorizationLeaseId = leaseId,
            authorizationExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(90)
        });
        Assert.Equal(HttpStatusCode.Forbidden, expiredRenewal.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidRenewal_PreservesThePeerPastItsOriginalExpiry(bool localViewer)
    {
        var factory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(CreateMinimalRecordingSource(), factory);
        await server.StartAsync();
        using var client = new HttpClient();
        var leaseId = localViewer ? null : Guid.NewGuid().ToString("D");
        using var offer = await PostJsonAsync(client, Endpoint(server, "/api/webrtc/offer"), new
        {
            deviceUdid = "device-1", framesPerSecond = 30, type = "offer", sdp = "v=0\r\n",
            grantedScopes = new[] { "view" }, authorizationLeaseId = leaseId,
            authorizationExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(1)
        });
        Assert.True(offer.IsSuccessStatusCode, await offer.Content.ReadAsStringAsync());
        var answer = await offer.Content.ReadFromJsonAsync<JsonElement>();
        using var renewal = localViewer
            ? await client.PostAsync(Endpoint(server, "/api/webrtc/keepalive")
                + "&session=" + Uri.EscapeDataString(answer.GetProperty("sessionId").GetString()!), null)
            : await PostJsonAsync(client, Endpoint(server, "/api/webrtc/renew"), new
        {
            sessionId = answer.GetProperty("sessionId").GetString(), authorizationLeaseId = leaseId,
            authorizationExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(90)
        });
        Assert.True(renewal.IsSuccessStatusCode);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await server.RemoveExpiredWebRtcSessionsAsync();
        Assert.False(Assert.IsType<FakeWebRtcSession>(factory.LastSession).IsDisposed);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string endpoint, object body)
    {
        return client.PostAsync(endpoint, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
    }

    [Theory]
    [InlineData(false, false, "files.list", HttpStatusCode.Forbidden)]
    [InlineData(true, false, "files.list", HttpStatusCode.OK)]
    [InlineData(true, false, "files.write", HttpStatusCode.Forbidden)]
    [InlineData(true, false, "files.rename", HttpStatusCode.Forbidden)]
    [InlineData(true, false, "files.delete", HttpStatusCode.Forbidden)]
    [InlineData(true, true, "files.write", HttpStatusCode.OK)]
    [InlineData(true, true, "files.rename", HttpStatusCode.OK)]
    [InlineData(true, true, "files.delete", HttpStatusCode.OK)]
    public async Task InspectionDataChannel_RequiresScopeAndBindsRequestToDevice(
        bool grantInspectionRead,
        bool grantControl,
        string operation,
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
        var grantedScopes = new List<string> { "view" };

        if (grantInspectionRead)
        {
            grantedScopes.Add("inspect.read");
        }

        if (grantControl)
        {
            grantedScopes.Add("control");
        }

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
        session.Receive($$"""
            {
              "kind":"inspection_request",
              "requestId":"request-1",
              "operation":"{{operation}}",
              "sessionId":"app-session-1",
              "arguments":{"path":"logs"}
            }
            """);

        var responseMessage = await session.WaitForMessageAsync();
        using var responseDocument = JsonDocument.Parse(responseMessage);

        Assert.Equal("inspection_response", responseDocument.RootElement.GetProperty("kind").GetString());
        Assert.Equal((int)expectedStatusCode, responseDocument.RootElement.GetProperty("statusCode").GetInt32());

        if (expectedStatusCode == HttpStatusCode.OK)
        {
            var invocation = await inspectionSource.WaitForInvocationAsync();
            Assert.Equal("device-1", invocation.DeviceUdid);
            Assert.Equal("app-session-1", invocation.Request.SessionId);
            Assert.Equal(operation, invocation.Request.Operation);

            var content = Convert.FromBase64String(responseDocument.RootElement.GetProperty("data").GetString()!);
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(content));
        }
    }

    private static RemoteControlServer CreateServer(
        IRemoteRecordingSource? recordingSource,
        ISimulatorWebRtcSessionFactory? webRtcSessionFactory = null,
        ISimulatorInputSink? inputSink = null,
        IRemoteAppInspectionSource? appInspectionSource = null,
        IRemoteAnnotationSource? annotationSource = null)
        => new(
            new FakeRuntimeSource(),
            new FakeRemoteSimulatorFrameSource(),
            inputSink ?? new FakeRemoteSimulatorInputSink(),
            requestedPort: 0,
            sessionToken: SessionToken,
            webRtcSessionFactory: webRtcSessionFactory,
            recordingSource: recordingSource,
            appInspectionSource: appInspectionSource,
            annotationSource: annotationSource);

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

    private sealed class FakeAnnotationFrameSource : IRemoteAnnotationSource, IRemoteAnnotationFrameSource
    {
        public string? RequestedDevice { get; private set; }

        public Task<RemoteRecordingFrame?> GetBatchFrameAsync(
            string deviceIdentifier, string batchId, string sessionId, string frameId,
            CancellationToken cancellationToken = default)
        {
            RequestedDevice = deviceIdentifier;

            var matchesBatch = batchId == "batch-1"
                && sessionId == "live-session"
                && frameId == "frame-1";

            return Task.FromResult<RemoteRecordingFrame?>(matchesBatch
                ? new RemoteRecordingFrame("image/png", [1, 2, 3])
                : null);
        }

        public Task<IReadOnlyList<RemoteAnnotationSession>> ListLiveSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RemoteAnnotationSession>>([]);
        }

        public Task<RemoteAnnotationResult> CreateAnnotationAsync(RemoteAnnotationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteAnnotationResult> UpdateAnnotationAsync(RemoteAnnotationUpdateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteOperationResult> DeleteAnnotationAsync(RemoteAnnotationDeleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteAnnotationBatchStartResult> StartAnnotationBatchAsync(RemoteAnnotationBatchStartRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRuntimeSource : IRemoteRuntimeSource
    {
        public RemoteRuntimeSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            [new RemoteRuntimeDevice("device-1", "iPhone", "Booted", true, "iOS 26", "ios")],
            null);
    }

    private sealed class FakeRemoteRecordingSource : IRemoteRecordingSource, IRemoteRecordingReviewSource
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

        public Task<RemoteRecordingEvidenceSlice?> GetEvidenceAsync(
            RemoteRecordingEvidenceQuery query, CancellationToken cancellationToken = default)
        {
            return GetEvidenceAsync(query.RecordingId, query.StartUtc, query.EndUtc,
                query.MaximumLogCount, query.MaximumMetricSampleCount, cancellationToken);
        }

        public Task<RemoteRecordingReviewResult> ReviewAsync(
            string operation, string? recordingId, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RemoteRecordingReviewResult(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{}")));
        }

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

        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
