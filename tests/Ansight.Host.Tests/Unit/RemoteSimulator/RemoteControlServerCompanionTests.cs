using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Streaming;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class RemoteControlServerCompanionTests
{
    [Fact]
    public async Task StartAsync_ExposesTokenizedLoopbackBaseUrlForOutboundBroker()
    {
        await using var server = CreateServer(
            new FakeDeviceLifecycleSource(),
            new FakeAnnotationSource());

        await server.StartAsync();

        var baseUrl = new Uri(server.LoopbackBaseUrl);
        Assert.Equal("127.0.0.1", baseUrl.Host);
        Assert.Equal(server.Port, baseUrl.Port);
        Assert.Contains("token=", baseUrl.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_DoesNotServeLegacyBrowserViewer()
    {
        await using var server = CreateServer(
            new FakeDeviceLifecycleSource(),
            new FakeAnnotationSource());
        await server.StartAsync();
        using var client = new HttpClient();

        using var response = await client.GetAsync(Endpoint(server, "/"));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private const string SessionToken = "companion-test-token";

    [Fact]
    public async Task State_ReportsStoppedDevicesAndLiveAnnotationSessions()
    {
        var lifecycleSource = new FakeDeviceLifecycleSource();
        var annotationSource = new FakeAnnotationSource();
        await using var server = CreateServer(lifecycleSource, annotationSource);
        await server.StartAsync();
        using var client = new HttpClient();

        var state = await client.GetFromJsonAsync<CompanionStateResponse>(
            Endpoint(server, "/api/state"));

        var stoppedDevice = Assert.Single(state!.BootableDevices);
        Assert.Equal("simulator-stopped", stoppedDevice.Identifier);
        var installedApplication = Assert.Single(state.InstalledApplications["simulator-stopped"]);
        Assert.Equal("Red-Point", installedApplication.Name);
        Assert.Equal("com.alphaoutdoors.redpoint", installedApplication.BundleIdentifier);
        var annotationSession = Assert.Single(state.AnnotationSessions);
        Assert.Equal("live-session", annotationSession.SessionId);
        Assert.Equal("simulator-active", annotationSession.DeviceUdid);
    }

    [Fact]
    public async Task CompanionActions_StartDeviceAndMutateBatchAnnotation()
    {
        var lifecycleSource = new FakeDeviceLifecycleSource();
        var annotationSource = new FakeAnnotationSource();
        await using var server = CreateServer(lifecycleSource, annotationSource);
        await server.StartAsync();
        using var client = new HttpClient();

        using var startResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/device/start"),
            new DeviceStartRequest("simulator-stopped"));
        using var annotationResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/annotation"),
            new RemoteAnnotationRequest(
                "simulator-active",
                "rectangle",
                0.1,
                0.2,
                0.3,
                0.4,
                "Fix this control",
                "act",
                "batch-1",
                "frame-1"));
        using var updateResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/annotation/update"),
            new RemoteAnnotationUpdateRequest(
                "simulator-active",
                "annotation-1",
                "point",
                0.5,
                0.6,
                null,
                null,
                "Updated control",
                "investigate",
                "batch-1",
                "frame-1"));
        using var deleteResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/annotation/delete"),
            new RemoteAnnotationDeleteRequest(
                "simulator-active",
                "annotation-1",
                "batch-1",
                "frame-1"));

        Assert.True(
            startResponse.IsSuccessStatusCode,
            await startResponse.Content.ReadAsStringAsync());
        Assert.True(
            annotationResponse.IsSuccessStatusCode,
            await annotationResponse.Content.ReadAsStringAsync());
        Assert.True(updateResponse.IsSuccessStatusCode, await updateResponse.Content.ReadAsStringAsync());
        Assert.True(deleteResponse.IsSuccessStatusCode, await deleteResponse.Content.ReadAsStringAsync());
        Assert.Equal("simulator-stopped", lifecycleSource.StartedIdentifier);
        Assert.Equal("Fix this control", annotationSource.LastRequest?.Comment);
        Assert.Equal("act", annotationSource.LastRequest?.AgentAction);
        Assert.Equal("batch-1", annotationSource.LastRequest?.BatchId);
        Assert.Equal("frame-1", annotationSource.LastRequest?.FrameId);
        Assert.Equal("Updated control", annotationSource.LastUpdateRequest?.Comment);
        Assert.Equal("annotation-1", annotationSource.LastDeleteRequest?.AnnotationId);
    }

    [Fact]
    public async Task CompanionActions_SetAndClearSelectedDeviceLocation()
    {
        var locationSource = new FakeDeviceLocationSource();
        await using var server = CreateServer(
            new FakeDeviceLifecycleSource(),
            new FakeAnnotationSource(),
            deviceLocationSource: locationSource);
        await server.StartAsync();
        using var client = new HttpClient();

        using var setResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/location/set"),
            new RemoteDeviceLocationRequest("simulator-active", -33.8688, 151.2093));
        using var clearResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/location/clear"),
            new RemoteDeviceLocationClearRequest("simulator-active"));

        Assert.True(setResponse.IsSuccessStatusCode, await setResponse.Content.ReadAsStringAsync());
        Assert.True(clearResponse.IsSuccessStatusCode, await clearResponse.Content.ReadAsStringAsync());
        Assert.Equal("simulator-active", locationSource.LastSetRequest?.DeviceUdid);
        Assert.Equal(-33.8688, locationSource.LastSetRequest?.Latitude);
        Assert.Equal(151.2093, locationSource.LastSetRequest?.Longitude);
        Assert.Equal("simulator-active", locationSource.LastClearRequest?.DeviceUdid);
    }

    [Theory]
    [InlineData(-91, 0)]
    [InlineData(91, 0)]
    [InlineData(0, -181)]
    [InlineData(0, 181)]
    public async Task SetLocation_RejectsOutOfRangeCoordinates(double latitude, double longitude)
    {
        var locationSource = new FakeDeviceLocationSource();
        await using var server = CreateServer(
            new FakeDeviceLifecycleSource(),
            new FakeAnnotationSource(),
            deviceLocationSource: locationSource);
        await server.StartAsync();
        using var client = new HttpClient();

        using var response = await PostJsonAsync(
            client,
            Endpoint(server, "/api/location/set"),
            new RemoteDeviceLocationRequest("simulator-active", latitude, longitude));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(locationSource.LastSetRequest);
    }

    [Fact]
    public async Task AnnotationBatch_StartsOnOneFrameAndSubmitsToSelectedAgentChat()
    {
        var annotationSource = new FakeAnnotationSource();
        var agentChatSource = new FakeAgentChatSource();
        await using var server = CreateServer(
            new FakeDeviceLifecycleSource(),
            annotationSource,
            agentChatSource);
        await server.StartAsync();
        using var client = new HttpClient();

        using var startResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/annotation/batch/start"),
            new RemoteAnnotationBatchStartRequest("simulator-active", "batch-1"));
        using var chatsResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/agent/chats"),
            new RemoteAgentChatListRequest("live-session"));
        using var tasksResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/agent/tasks"),
            new RemoteAgentTaskListRequest("live-session"));
        using var submitResponse = await PostJsonAsync(
            client,
            Endpoint(server, "/api/annotation/batch/submit"),
            new RemoteAnnotationBatchSubmissionRequest(
                "batch-1",
                "live-session",
                "frame-1",
                ["annotation-1", "annotation-2"],
                "codex",
                "thread-1"));

        Assert.True(startResponse.IsSuccessStatusCode, await startResponse.Content.ReadAsStringAsync());
        Assert.True(chatsResponse.IsSuccessStatusCode, await chatsResponse.Content.ReadAsStringAsync());
        Assert.True(tasksResponse.IsSuccessStatusCode, await tasksResponse.Content.ReadAsStringAsync());
        var taskHistory = await tasksResponse.Content.ReadFromJsonAsync<RemoteAgentTaskListResult>();
        var linkedTask = Assert.Single(taskHistory!.Tasks);
        Assert.Equal("thread-1", linkedTask.ProviderTaskId);
        Assert.Equal("Fix the annotated control.", linkedTask.SubmittedPrompt);
        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        Assert.Equal("batch-1", annotationSource.LastBatchRequest?.BatchId);
        Assert.Equal("thread-1", agentChatSource.LastSubmission?.ChatSessionId);
        Assert.Equal(["annotation-1", "annotation-2"], agentChatSource.LastSubmission?.AnnotationIds);
    }

    private static RemoteControlServer CreateServer(
        IRemoteDeviceLifecycleSource lifecycleSource,
        IRemoteAnnotationSource annotationSource,
        IRemoteAgentChatSource? agentChatSource = null,
        IRemoteDeviceLocationSource? deviceLocationSource = null)
        => new(
            new FakeRuntimeSource(),
            new FakeRemoteSimulatorFrameSource(),
            new FakeRemoteSimulatorInputSink(),
            requestedPort: 0,
            sessionToken: SessionToken,
            deviceLifecycleSource: lifecycleSource,
            annotationSource: annotationSource,
            agentChatSource: agentChatSource ?? new FakeAgentChatSource(),
            deviceLocationSource: deviceLocationSource);

    private static string Endpoint(RemoteControlServer server, string path)
        => $"http://127.0.0.1:{server.Port}{path}?token={SessionToken}";

    private static Task<HttpResponseMessage> PostJsonAsync<T>(
        HttpClient client,
        string endpoint,
        T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return client.PostAsync(endpoint, content);
    }

    private sealed class FakeDeviceLifecycleSource : IRemoteDeviceLifecycleSource
    {
        public string StartedIdentifier { get; private set; } = string.Empty;

        public Task<IReadOnlyList<RemoteBootableDevice>> ListBootableDevicesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteBootableDevice>>(
            [
                new RemoteBootableDevice(
                    "simulator-stopped",
                    "iPhone 17 Pro",
                    "iOS 26.0",
                    "ios")
            ]);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<RemoteInstalledApplication>>> ListInstalledApplicationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<RemoteInstalledApplication>>>(
                new Dictionary<string, IReadOnlyList<RemoteInstalledApplication>>
                {
                    ["simulator-stopped"] =
                    [
                        new RemoteInstalledApplication(
                            "com.alphaoutdoors.redpoint",
                            "Red-Point")
                    ]
                });

        public Task<RemoteOperationResult> StartDeviceAsync(
            string identifier,
            CancellationToken cancellationToken = default)
        {
            StartedIdentifier = identifier;
            return Task.FromResult(new RemoteOperationResult(true, "Starting."));
        }
    }

    private sealed class FakeAnnotationSource : IRemoteAnnotationSource
    {
        public RemoteAnnotationRequest? LastRequest { get; private set; }

        public RemoteAnnotationBatchStartRequest? LastBatchRequest { get; private set; }

        public RemoteAnnotationUpdateRequest? LastUpdateRequest { get; private set; }

        public RemoteAnnotationDeleteRequest? LastDeleteRequest { get; private set; }

        public Task<IReadOnlyList<RemoteAnnotationSession>> ListLiveSessionsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteAnnotationSession>>(
            [
                new RemoteAnnotationSession(
                    "simulator-active",
                    "live-session",
                    "Sample App",
                    "com.ansight.sample")
            ]);

        public Task<RemoteAnnotationResult> CreateAnnotationAsync(
            RemoteAnnotationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new RemoteAnnotationResult(
                true,
                "Saved.",
                "annotation-1",
                "live-session"));
        }

        public Task<RemoteAnnotationBatchStartResult> StartAnnotationBatchAsync(
            RemoteAnnotationBatchStartRequest request,
            CancellationToken cancellationToken = default)
        {
            LastBatchRequest = request;
            return Task.FromResult(new RemoteAnnotationBatchStartResult(
                true,
                "Started.",
                request.BatchId,
                "live-session",
                "frame-1",
                DateTimeOffset.UtcNow));
        }

        public Task<RemoteAnnotationResult> UpdateAnnotationAsync(
            RemoteAnnotationUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUpdateRequest = request;
            return Task.FromResult(new RemoteAnnotationResult(
                true,
                "Updated.",
                request.AnnotationId,
                "live-session",
                request.BatchId,
                request.FrameId,
                DateTimeOffset.UtcNow));
        }

        public Task<RemoteOperationResult> DeleteAnnotationAsync(
            RemoteAnnotationDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastDeleteRequest = request;
            return Task.FromResult(new RemoteOperationResult(true, "Deleted."));
        }
    }

    private sealed class FakeDeviceLocationSource : IRemoteDeviceLocationSource
    {
        public RemoteDeviceLocationRequest? LastSetRequest { get; private set; }

        public RemoteDeviceLocationClearRequest? LastClearRequest { get; private set; }

        public Task<RemoteDeviceLocationResult> SetLocationAsync(
            RemoteDeviceLocationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSetRequest = request;
            return Task.FromResult(new RemoteDeviceLocationResult(
                true,
                "fake",
                "Location set.",
                request.DeviceUdid,
                "iPhone 17 Pro",
                "ios"));
        }

        public Task<RemoteDeviceLocationResult> ClearLocationAsync(
            RemoteDeviceLocationClearRequest request,
            CancellationToken cancellationToken = default)
        {
            LastClearRequest = request;
            return Task.FromResult(new RemoteDeviceLocationResult(
                true,
                "fake",
                "Location cleared.",
                request.DeviceUdid,
                "iPhone 17 Pro",
                "ios"));
        }
    }

    private sealed class FakeAgentChatSource : IRemoteAgentChatSource
    {
        public RemoteAnnotationBatchSubmissionRequest? LastSubmission { get; private set; }

        public Task<RemoteAgentChatListResult> ListAgentChatsAsync(
            RemoteAgentChatListRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteAgentChatListResult(
                true,
                "Found.",
                [
                    new RemoteAgentChatSession(
                        "codex",
                        "thread-1",
                        "Fix annotations",
                        "/workspace",
                        DateTimeOffset.UtcNow,
                        "idle")
                ]));

        public Task<RemoteAgentTaskListResult> ListAgentTasksAsync(
            RemoteAgentTaskListRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteAgentTaskListResult(
                true,
                "Found.",
                [
                    new RemoteAgentTaskLink(
                        "link-1",
                        null,
                        "live-session",
                        "batch-1",
                        ["annotation-1"],
                        ["frame-1"],
                        "legacy-desktop",
                        "codex",
                        "thread-1",
                        "turn-1",
                        "Fix annotations",
                        "/workspace",
                        "Fix the annotated control.",
                        "inProgress",
                        "Accepted.",
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)
                ]));

        public Task<RemoteAnnotationBatchSubmissionResult> SubmitAnnotationBatchAsync(
            RemoteAnnotationBatchSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSubmission = request;
            return Task.FromResult(new RemoteAnnotationBatchSubmissionResult(
                true,
                "Accepted.",
                "accepted",
                "turn-1"));
        }
    }

    private sealed class FakeRuntimeSource : IRemoteRuntimeSource
    {
        public RemoteRuntimeSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            [
                new RemoteRuntimeDevice(
                    "simulator-active",
                    "iPhone 17 Pro",
                    "Booted",
                    true,
                    "iOS 26.0",
                    "ios")
            ],
            null);
    }

    private sealed record DeviceStartRequest(string Identifier);

    private sealed record CompanionStateResponse(
        IReadOnlyList<RemoteRuntimeDevice> Devices,
        IReadOnlyList<RemoteBootableDevice> BootableDevices,
        IReadOnlyDictionary<string, IReadOnlyList<RemoteInstalledApplication>> InstalledApplications,
        IReadOnlyList<RemoteAnnotationSession> AnnotationSessions);
}
