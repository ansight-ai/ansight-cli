using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Notifications;

public sealed class ExternalRecordingNotificationTests
{
    [Theory]
    [InlineData("ios", DeviceKinds.Simulator)]
    [InlineData("android", DeviceKinds.Emulator)]
    public async Task CaptureStartNotifiesOnceAndLinksTheExternalSession(string platform, string kind)
    {
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var runtime = composition.Get<RuntimeCoordinator>();
        var state = composition.Get<IRuntimeState>();
        runtime.Apps.Register(new AppRegistrationRequest("test.notes", "Notes"));
        var sink = new Sink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        await coordinator.InitializeAsync();
        var id = state.CreateDeviceSession(new WorkspaceTestTarget(platform, "sim", "Phone", "test.notes", false, false, false)
        { DeviceKind = kind, ExecutionMode = "device" });
        Assert.Empty(sink.Notifications); // Allocating a session is not a successful capture attachment.
        var started = new RuntimeSessionCaptureEvent(DateTimeOffset.UtcNow, RuntimeSessionCaptureEventKind.Started,
            id, "test.notes", "test.notes", "Capturing", "Started");
        state.PublishRuntimeEvent(started);
        state.PublishRuntimeEvent(started);
        state.PublishRuntimeEvent(started with { Kind = RuntimeSessionCaptureEventKind.Updated });
        coordinator.HandleSessionTransferEvent(new RuntimeSessionTransferEvent(DateTimeOffset.UtcNow,
            RuntimeSessionTransferKind.Screenshot, id, "test.notes", "test.notes", 1, "Frame", DateTimeOffset.UtcNow));
        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("Session recording started", notification.Title);
        Assert.Equal("Notes started recording via an external monitor.", notification.Body);
        Assert.Equal(id, notification.SessionId);
        Assert.Equal($"session-recording-{id}", notification.Identifier);

        coordinator.Dispose();
        state.PublishRuntimeEvent(started with { SessionId = "later" });
        Assert.Single(sink.Notifications);
    }

    [Fact]
    public async Task ExistingCapturesAreSeededWithoutRetroactiveAlerts()
    {
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var runtime = composition.Get<RuntimeCoordinator>();
        var state = composition.Get<IRuntimeState>();
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "sim", "Phone", "test.notes", false, false, false)
        { DeviceKind = DeviceKinds.Simulator, ExecutionMode = "device" });
        var sink = new Sink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        await coordinator.InitializeAsync();
        state.PublishRuntimeEvent(new RuntimeSessionCaptureEvent(DateTimeOffset.UtcNow, RuntimeSessionCaptureEventKind.Started,
            id, "test.notes", "Notes", "Capturing", "Started"));
        Assert.Empty(sink.Notifications);
    }

    private sealed class Sink : INotificationSink
    {
        public List<Notification> Notifications { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowAsync(Notification notification, CancellationToken cancellationToken = default)
        { Notifications.Add(notification); return Task.CompletedTask; }
        public Task RemoveAsync(string notificationIdentifier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
