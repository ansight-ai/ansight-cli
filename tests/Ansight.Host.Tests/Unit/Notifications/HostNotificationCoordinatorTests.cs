namespace Ansight.Host.Tests.Unit.Notifications;

public sealed class HostNotificationCoordinatorTests
{
    [Fact]
    public void SessionCachePressure_WarnsOnceAndReportsAutomaticCleanup()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var now = DateTimeOffset.UtcNow;

        coordinator.HandleSessionCacheMaintenanceCompleted(new SessionCacheMaintenanceResult(now, 80, 100, 0, 0));
        coordinator.HandleSessionCacheMaintenanceCompleted(new SessionCacheMaintenanceResult(now, 90, 100, 0, 0));
        Assert.Single(sink.Notifications);
        Assert.Equal(NotificationCoordinator.SessionCacheWarningNotificationIdentifier,
            sink.Notifications[0].Identifier);

        coordinator.HandleSessionCacheMaintenanceCompleted(new SessionCacheMaintenanceResult(now, 85, 100, 0, 2));
        Assert.Equal(2, sink.Notifications.Count);
        Assert.Equal(NotificationCoordinator.SessionCacheCleanupNotificationIdentifier,
            sink.Notifications[1].Identifier);
        Assert.Contains("2 old, unpinned", sink.Notifications[1].Body);

        coordinator.HandleSessionCacheMaintenanceCompleted(new SessionCacheMaintenanceResult(now, 79, 100, 0, 0));
        coordinator.HandleSessionCacheMaintenanceCompleted(new SessionCacheMaintenanceResult(now, 81, 100, 0, 0));
        Assert.Equal(3, sink.Notifications.Count);
    }

    [Fact]
    public async Task FirstHostStart_InvitesNewUserOnce()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);

        await coordinator.InitializeAsync();
        await coordinator.NotifyHostStartedAsync();

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("getting-started", notification.Identifier);
        Assert.Equal("Welcome to Ansight", notification.Title);
        Assert.Contains("local player is ready", notification.Body);
        Assert.Null(notification.SessionId);
        Assert.Null(notification.Schedule);
        Assert.Null(notification.Action);

        await coordinator.NotifyHostStartedAsync();
        Assert.Equal(NotificationCoordinator.HostStartedNotificationIdentifier, sink.Notifications[1].Identifier);
    }

    [Fact]
    public async Task SkippedGettingStarted_DoesNotSendTheWelcomeAgain()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var store = new Ansight.Host.Explorer.GettingStartedStore(environment.ApplicationPaths.ApplicationDataPath);
        store.Update(state => state with { Opened = true, Skipped = true });
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);

        await coordinator.InitializeAsync();
        await coordinator.NotifyHostStartedAsync();

        Assert.Equal(NotificationCoordinator.HostStartedNotificationIdentifier,
            Assert.Single(sink.Notifications).Identifier);
    }

    [Fact]
    public void FirstPairing_NotifiesWithRegisteredAppName()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var registration = runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.weather",
            "Weather"));
        Assert.True(registration.IsSuccess);
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var pairingEvent = new RuntimePairingEvent(
            DateTimeOffset.UtcNow,
            RuntimePairingEventKind.PairingAccepted,
            "session-1",
            "com.example.weather",
            "iPhone 17",
            "127.0.0.1",
            "invite-1",
            "Ok",
            "Device registered and WebSocket endpoint issued.",
            IsFirstConnection: true);

        coordinator.HandlePairingEvent(pairingEvent);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("app-paired-com.example.weather-invite-1", notification.Identifier);
        Assert.Equal("App paired with Ansight", notification.Title);
        Assert.Equal("Weather paired from iPhone 17 (127.0.0.1).", notification.Body);
        Assert.Equal("session-1", notification.SessionId);
    }

    [Fact]
    public void FirstSessionTransfer_NotifiesOnce()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var transferEvent = new RuntimeSessionTransferEvent(
            DateTimeOffset.UtcNow,
            RuntimeSessionTransferKind.Telemetry,
            "session-2",
            "com.example.weather",
            "Weather",
            1,
            "Telemetry received.",
            DateTimeOffset.UtcNow);

        coordinator.HandleSessionTransferEvent(transferEvent);
        coordinator.HandleSessionTransferEvent(transferEvent);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("session-recording-session-2", notification.Identifier);
        Assert.Equal("Session recording started", notification.Title);
        Assert.Equal("Weather started streaming session data.", notification.Body);
    }

    [Fact]
    public void PairingRejected_NotifiesWithReason()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var pairingEvent = new RuntimePairingEvent(
            DateTimeOffset.UtcNow,
            RuntimePairingEventKind.PairingRejected,
            string.Empty,
            "com.alphaoutdoors.redpoint",
            "Red-Point",
            "192.168.1.20",
            "invite-1",
            "EnrollmentRequired",
            "Ansight does not recognize this enrollment invite. Scan a fresh QR code.");

        coordinator.HandlePairingEvent(pairingEvent);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("App session connection refused", notification.Title);
        Assert.Equal(
            "Red-Point was refused: Ansight does not recognize this enrollment invite. Scan a fresh QR code.",
            notification.Body);
        Assert.Null(notification.SessionId);
    }

    [Fact]
    public void ExistingPairing_DoesNotNotifyAgain()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var pairingEvent = new RuntimePairingEvent(
            DateTimeOffset.UtcNow,
            RuntimePairingEventKind.PairingAccepted,
            "session-pending",
            "com.example.weather",
            "iPhone 17",
            "127.0.0.1",
            "invite-1",
            "Ok",
            "Device registered and WebSocket endpoint issued.");

        coordinator.HandlePairingEvent(pairingEvent);

        Assert.Empty(sink.Notifications);
    }

    [Fact]
    public void TrendsRegression_NotifiesWithDefinition()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var registration = runtime.Apps.Register(new AppRegistrationRequest(
            "com.alphaoutdoors.redpoint",
            "Redpoint"));
        Assert.True(registration.IsSuccess);
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var trendsEvent = new RuntimeTrendsEvent(
            DateTimeOffset.UtcNow,
            RuntimeTrendsEventKind.RegressionDetected,
            "session-4",
            "com.alphaoutdoors.redpoint",
            "ascent-duration",
            "Regression detected after 2 consecutive comparable runs.",
            HistoryStatus: Ansight.Host.Trends.WorkspaceTrendsHistoryStatus.Regressed);

        coordinator.HandleTrendsEvent(trendsEvent);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("trends-regression-session-4-ascent-duration", notification.Identifier);
        Assert.Equal("Trends regression detected", notification.Title);
        Assert.Equal(
            "Redpoint: ascent-duration regressed. Regression detected after 2 consecutive comparable runs.",
            notification.Body);
        Assert.Equal("session-4", notification.SessionId);
    }

    [Fact]
    public void CompletedAnalysis_NotifiesOnce()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var snapshot = CreateSnapshot([
            new SessionAnalysisRecord
            {
                AnalysisId = "analysis-1",
                AgentId = "Codex",
                AnalysisKind = SessionAnalysisKind.General,
                StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                CompletedUtc = DateTimeOffset.UtcNow,
                Success = true
            }
        ]);

        coordinator.HandleSessionUpdated(snapshot);
        coordinator.HandleSessionUpdated(snapshot);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("analysis-finished-analysis-1", notification.Identifier);
        Assert.Equal("Codex analysis finished", notification.Title);
        Assert.Equal("Weather analysis completed.", notification.Body);
        Assert.Equal("session-3", notification.SessionId);
    }

    [Fact]
    public void IncompleteAnalysis_DoesNotNotify()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var snapshot = CreateSnapshot([
            new SessionAnalysisRecord
            {
                AnalysisId = "analysis-2",
                AgentId = "Codex",
                StartedUtc = DateTimeOffset.UtcNow,
                Success = false
            }
        ]);

        coordinator.HandleSessionUpdated(snapshot);

        Assert.Empty(sink.Notifications);
    }

    [Fact]
    public void CompanionConnection_NotifiesOnceForTheConnectedSession()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var connection = new CompanionConnection(
            "companion-session-1",
            "companion-device-1",
            "Matthew's iPhone",
            "iPhone 17 Pro",
            "iOS",
            "26.0",
            "1.2.3",
            "simulator-1",
            DateTimeOffset.UtcNow);

        coordinator.HandleCompanionConnectionsChanged([connection]);
        coordinator.HandleCompanionConnectionsChanged([connection]);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal("companion-connected-companion-session-1", notification.Identifier);
        Assert.Equal("Companion app connected", notification.Title);
        Assert.Equal("Matthew's iPhone connected to companion access.", notification.Body);
        Assert.Null(notification.Schedule);
    }

    [Fact]
    public void AlwaysMode_SchedulesNonDisableableFortnightlyReminderUntilAccessIsDisabled()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);
        var alwaysStatus = new CompanionAccessStatus(
            CompanionAccessMode.Always,
            false,
            true,
            "Ready when Host is open",
            0);

        coordinator.HandleCompanionAccessStatusChanged(alwaysStatus);
        coordinator.HandleCompanionAccessStatusChanged(alwaysStatus);

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal(NotificationCoordinator.CompanionAccessReminderIdentifier, notification.Identifier);
        Assert.Equal("Companion access is still enabled", notification.Title);
        Assert.Equal(NotificationCoordinator.CompanionAccessReminderInterval, notification.Schedule!.Delay);
        Assert.Equal(NotificationCoordinator.CompanionAccessReminderInterval, notification.Schedule.RepeatInterval);
        Assert.True(notification.Schedule.PreserveExisting);
        Assert.Equal(
            NotificationCoordinator.DeferCompanionAccessReminderActionIdentifier,
            notification.Action!.Identifier);
        Assert.Equal("Remind me in 2 weeks", notification.Action.Title);
        Assert.Equal(
            NotificationCoordinator.CompanionAccessReminderInterval,
            notification.Action.DeferralInterval);

        coordinator.HandleCompanionAccessStatusChanged(alwaysStatus with
        {
            Mode = CompanionAccessMode.Disabled,
            IsEnabled = false
        });

        Assert.Equal(
            [NotificationCoordinator.CompanionAccessReminderIdentifier],
            sink.RemovedNotificationIdentifiers);
    }

    [Fact]
    public async Task InitializeAsync_WhenAlwaysAccessIsNotEnabled_RemovesAnyStaleReminder()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var sink = new RecordingNotificationSink();
        using var coordinator = new NotificationCoordinator(runtime, sink);

        await coordinator.InitializeAsync();

        Assert.Equal(
            [NotificationCoordinator.CompanionAccessReminderIdentifier],
            sink.RemovedNotificationIdentifiers);
    }

    private static AppSessionSnapshot CreateSnapshot(
        IReadOnlyList<SessionAnalysisRecord> analyses,
        string sessionId = "session-3",
        string clientName = "Weather")
    {
        return new AppSessionSnapshot
        {
            SessionId = sessionId,
            AppId = "com.example.weather",
            ClientName = clientName,
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            Analyses = analyses,
            MetricChannels = [],
            Metrics = []
        };
    }

    private sealed class RecordingNotificationSink : INotificationSink
    {
        public List<Notification> Notifications { get; } = [];

        public List<string> RemovedNotificationIdentifiers { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ShowAsync(
            Notification notification,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            string notificationIdentifier,
            CancellationToken cancellationToken = default)
        {
            RemovedNotificationIdentifiers.Add(notificationIdentifier);
            return Task.CompletedTask;
        }
    }
}
