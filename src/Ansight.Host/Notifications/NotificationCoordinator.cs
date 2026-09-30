namespace Ansight.Host.Notifications;

public sealed class NotificationCoordinator : IDisposable
{
    internal const string HostStartedNotificationIdentifier = "host-started";
    internal const string CompanionAccessReminderIdentifier = "companion-access-still-enabled";
    internal const string DeferCompanionAccessReminderActionIdentifier = "defer-companion-access-reminder";
    internal static readonly TimeSpan CompanionAccessReminderInterval = TimeSpan.FromDays(14);

    private readonly object gate = new();
    private readonly RuntimeCoordinator hostRuntime;
    private readonly INotificationSink notificationSink;
    private readonly HashSet<string> recordingSessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> analysisIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> companionSessionIds = new(StringComparer.Ordinal);
    private bool? companionAccessReminderScheduled;
    private bool initialized;
    private bool disposed;

    public NotificationCoordinator(
        RuntimeCoordinator hostRuntime,
        INotificationSink notificationSink)
    {
        this.hostRuntime = hostRuntime ?? throw new ArgumentNullException(nameof(hostRuntime));
        this.notificationSink = notificationSink ?? throw new ArgumentNullException(nameof(notificationSink));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return;
        }

        lock (gate)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
        }

        try
        {
            await notificationSink.InitializeAsync(cancellationToken).ConfigureAwait(false);
            foreach (var snapshot in hostRuntime.Sessions.GetSummaries())
            {
                SeedSnapshot(snapshot);
            }

            if (hostRuntime.ActiveCompanion is { } companion) SeedCompanionConnections(companion.GetConnections());

            hostRuntime.PairingEventOccurred += RuntimeOnPairingEventOccurred;
            hostRuntime.TrendsEventOccurred += RuntimeOnTrendsEventOccurred;
            hostRuntime.SessionTransferEventOccurred += RuntimeOnSessionTransferEventOccurred;
            hostRuntime.SessionCaptureEventOccurred += RuntimeOnSessionCaptureEventOccurred;
            hostRuntime.SessionUpdated += RuntimeOnSessionUpdated;
            hostRuntime.SessionDeleted += RuntimeOnSessionDeleted;
            if (hostRuntime.ActiveCompanion is { } activeCompanion)
            {
                activeCompanion.ConnectionsChanged += CompanionOnConnectionsChanged;
                activeCompanion.AccessStatusChanged += CompanionOnAccessStatusChanged;
                HandleCompanionAccessStatusChanged(activeCompanion.GetAccessStatus());
            }
            else HandleCompanionAccessStatusChanged(new CompanionAccessStatus(CompanionAccessMode.Disabled, false, false, "Inactive", 0));
        }
        catch
        {
            lock (gate)
            {
                initialized = false;
            }

            throw;
        }
    }

    public Task NotifyHostStartedAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        return PublishNotificationAsync(
            new Notification(
                HostStartedNotificationIdentifier,
                "Ansight host started",
                "The local host is ready for app connections."),
            cancellationToken);
    }

    internal void HandlePairingEvent(RuntimePairingEvent pairingEvent)
    {
        ArgumentNullException.ThrowIfNull(pairingEvent);
        if (disposed)
        {
            return;
        }

        if (pairingEvent.Kind == RuntimePairingEventKind.PairingRejected)
        {
            var rejectedAppLabel = ResolveAppLabel(pairingEvent.AppId, pairingEvent.ClientName);
            var reason = string.IsNullOrWhiteSpace(pairingEvent.ReasonMessage)
                ? string.IsNullOrWhiteSpace(pairingEvent.ReasonCode)
                    ? "The host rejected the connection request."
                    : pairingEvent.ReasonCode.Trim()
                : pairingEvent.ReasonMessage.Trim();
            PublishNotification(
                new Notification(
                    $"session-refused-{pairingEvent.AppId}-{pairingEvent.ConfigId ?? pairingEvent.RemoteAddress}-{pairingEvent.ReasonCode}",
                    "App session connection refused",
                    $"{rejectedAppLabel} was refused: {reason}",
                    string.IsNullOrWhiteSpace(pairingEvent.SessionId) ? null : pairingEvent.SessionId));
            return;
        }

        if (pairingEvent.Kind != RuntimePairingEventKind.PairingAccepted
            || !pairingEvent.IsFirstConnection)
        {
            return;
        }

        var appLabel = ResolveAppLabel(pairingEvent.AppId, pairingEvent.ClientName);
        var pairingIdentity = pairingEvent.ConfigId ?? pairingEvent.RemoteAddress;
        PublishNotification(
            new Notification(
                $"app-paired-{pairingEvent.AppId}-{pairingIdentity}",
                "App paired with Ansight",
                $"{appLabel} paired from {pairingEvent.ClientName} ({pairingEvent.RemoteAddress}).",
                string.IsNullOrWhiteSpace(pairingEvent.SessionId) ? null : pairingEvent.SessionId));
    }

    internal void HandleTrendsEvent(RuntimeTrendsEvent trendsEvent)
    {
        ArgumentNullException.ThrowIfNull(trendsEvent);
        if (disposed || trendsEvent.Kind != RuntimeTrendsEventKind.RegressionDetected)
        {
            return;
        }

        var appLabel = ResolveAppLabel(trendsEvent.AppId, trendsEvent.AppId);
        var groupContext = string.IsNullOrWhiteSpace(trendsEvent.SpanGroup)
            ? string.Empty
            : $" [{trendsEvent.SpanGroup}]";
        var groupIdentifier = string.IsNullOrWhiteSpace(trendsEvent.SpanGroup)
            ? string.Empty
            : $"-{trendsEvent.SpanGroup}";
        var comparisonContext = trendsEvent.HistoryComparison is null
            ? string.Empty
            : $" ({trendsEvent.HistoryComparison})";
        var comparisonIdentifier = trendsEvent.HistoryComparison is null
            ? string.Empty
            : $"-{trendsEvent.HistoryComparison}";
        PublishNotification(
            new Notification(
                $"trends-regression-{trendsEvent.SessionId}-{trendsEvent.DefinitionId}{groupIdentifier}{comparisonIdentifier}",
                "Trends regression detected",
                $"{appLabel}: {trendsEvent.DefinitionId}{groupContext}{comparisonContext} regressed. {trendsEvent.Message}",
                trendsEvent.SessionId));
    }

    internal void HandleSessionTransferEvent(RuntimeSessionTransferEvent sessionTransferEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionTransferEvent);
        if (disposed)
        {
            return;
        }

        lock (gate)
        {
            if (!recordingSessionIds.Add(sessionTransferEvent.SessionId))
            {
                return;
            }
        }

        var appLabel = ResolveAppLabel(sessionTransferEvent.AppId, sessionTransferEvent.ClientName);
        PublishNotification(
            new Notification(
                $"session-recording-{sessionTransferEvent.SessionId}",
                "Session recording started",
                $"{appLabel} started streaming session data.",
                sessionTransferEvent.SessionId));
    }

    internal void HandleSessionUpdated(AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (disposed)
        {
            return;
        }

        foreach (var notification in GetAnalysisNotifications(snapshot))
        {
            PublishNotification(notification);
        }
    }

    internal void HandleSessionCaptureEvent(RuntimeSessionCaptureEvent captureEvent)
    {
        ArgumentNullException.ThrowIfNull(captureEvent);
        if (disposed || captureEvent.Kind != RuntimeSessionCaptureEventKind.Started
            || !hostRuntime.Sessions.TryGetSnapshot(captureEvent.SessionId, out var snapshot)
            || snapshot?.CaptureSource != WorkspaceExecutionModes.Device)
        {
            return;
        }

        lock (gate)
        {
            if (!recordingSessionIds.Add(captureEvent.SessionId)) return;
        }

        PublishNotification(new Notification(
            $"session-recording-{captureEvent.SessionId}",
            "Session recording started",
            $"{ResolveAppLabel(captureEvent.AppId, captureEvent.ClientName)} started recording via an external monitor.",
            captureEvent.SessionId));
    }

    internal void HandleSessionDeleted(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (gate)
        {
            recordingSessionIds.Remove(sessionId);
        }
    }

    internal void HandleCompanionConnectionsChanged(
        IReadOnlyList<CompanionConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (disposed)
        {
            return;
        }

        CompanionConnection[] newConnections;
        lock (gate)
        {
            newConnections = connections
                .Where(connection => !companionSessionIds.Contains(connection.SessionId))
                .ToArray();
            companionSessionIds.Clear();
            companionSessionIds.UnionWith(connections.Select(static connection => connection.SessionId));
        }

        foreach (var connection in newConnections)
        {
            var deviceName = string.IsNullOrWhiteSpace(connection.DeviceName)
                ? "A companion device"
                : connection.DeviceName.Trim();
            PublishNotification(
                new Notification(
                    $"companion-connected-{connection.SessionId}",
                    "Companion app connected",
                    $"{deviceName} connected to companion access."));
        }
    }

    internal void HandleCompanionAccessStatusChanged(CompanionAccessStatus accessStatus)
    {
        ArgumentNullException.ThrowIfNull(accessStatus);
        if (disposed)
        {
            return;
        }

        var shouldSchedule = accessStatus.Mode == CompanionAccessMode.Always;
        lock (gate)
        {
            if (companionAccessReminderScheduled == shouldSchedule)
            {
                return;
            }

            companionAccessReminderScheduled = shouldSchedule;
        }

        if (!shouldSchedule)
        {
            RemoveNotification(CompanionAccessReminderIdentifier);
            return;
        }

        PublishNotification(
            new Notification(
                CompanionAccessReminderIdentifier,
                "Companion access is still enabled",
                "Always-on companion access lets signed-in companion apps connect to this machine. Turn it off when you no longer need it.",
                Schedule: new NotificationSchedule(
                    CompanionAccessReminderInterval,
                    CompanionAccessReminderInterval,
                    PreserveExisting: true),
                Action: new NotificationAction(
                    DeferCompanionAccessReminderActionIdentifier,
                    "Remind me in 2 weeks",
                    CompanionAccessReminderInterval)));
    }

    private void RuntimeOnPairingEventOccurred(object? sender, RuntimePairingEvent pairingEvent)
        => HandlePairingEvent(pairingEvent);

    private void RuntimeOnTrendsEventOccurred(object? sender, RuntimeTrendsEvent trendsEvent)
        => HandleTrendsEvent(trendsEvent);

    private void RuntimeOnSessionTransferEventOccurred(
        object? sender,
        RuntimeSessionTransferEvent sessionTransferEvent)
        => HandleSessionTransferEvent(sessionTransferEvent);

    private void RuntimeOnSessionUpdated(object? sender, AppSessionSnapshot snapshot)
        => HandleSessionUpdated(snapshot);

    private void RuntimeOnSessionCaptureEventOccurred(object? sender, RuntimeSessionCaptureEvent captureEvent)
        => HandleSessionCaptureEvent(captureEvent);

    private void RuntimeOnSessionDeleted(object? sender, string sessionId)
        => HandleSessionDeleted(sessionId);

    private void CompanionOnConnectionsChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (hostRuntime.ActiveCompanion is { } companion) HandleCompanionConnectionsChanged(companion.GetConnections());
        if (hostRuntime.ActiveCompanion is { } activeCompanion) HandleCompanionAccessStatusChanged(activeCompanion.GetAccessStatus());
    }

    private void CompanionOnAccessStatusChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (hostRuntime.ActiveCompanion is { } activeCompanion) HandleCompanionAccessStatusChanged(activeCompanion.GetAccessStatus());
    }

    private void SeedSnapshot(AppSessionSnapshot snapshot)
    {
        lock (gate)
        {
            if (HasRecordingStarted(snapshot))
            {
                recordingSessionIds.Add(snapshot.SessionId);
            }

            foreach (var analysis in snapshot.Analyses)
            {
                if (ShouldNotifyAnalysis(analysis, out _))
                {
                    analysisIds.Add(analysis.AnalysisId);
                }
            }
        }
    }

    private void SeedCompanionConnections(IReadOnlyList<CompanionConnection> connections)
    {
        lock (gate)
        {
            companionSessionIds.UnionWith(connections.Select(static connection => connection.SessionId));
        }
    }

    private Notification[] GetAnalysisNotifications(AppSessionSnapshot snapshot)
    {
        var notifications = new List<Notification>();
        var appLabel = ResolveAppLabel(snapshot.AppId, snapshot.ClientName);

        lock (gate)
        {
            foreach (var analysis in snapshot.Analyses)
            {
                if (!ShouldNotifyAnalysis(analysis, out var agentName)
                    || !analysisIds.Add(analysis.AnalysisId))
                {
                    continue;
                }

                var resultText = analysis.Success ? "completed" : "failed";
                var analysisKind = SessionAnalysisKind.ToDisplayName(analysis.AnalysisKind);
                var body = analysis.Success
                    ? $"{appLabel} {analysisKind.ToLowerInvariant()} {resultText}."
                    : $"{appLabel} {analysisKind.ToLowerInvariant()} {resultText}: {analysis.StatusMessage ?? "Unknown error."}";

                notifications.Add(new Notification(
                    $"analysis-finished-{analysis.AnalysisId}",
                    $"{agentName} analysis finished",
                    body,
                    snapshot.SessionId));
            }
        }

        return notifications.ToArray();
    }

    private void PublishNotification(Notification notification)
        => _ = PublishNotificationAsync(notification);

    private void RemoveNotification(string notificationIdentifier)
        => _ = RemoveNotificationAsync(notificationIdentifier);

    private async Task PublishNotificationAsync(
        Notification notification,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await notificationSink.ShowAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Notification delivery must never affect host behavior.
        }
    }

    private async Task RemoveNotificationAsync(string notificationIdentifier)
    {
        try
        {
            await notificationSink.RemoveAsync(notificationIdentifier).ConfigureAwait(false);
        }
        catch
        {
            // Notification delivery must never affect host behavior.
        }
    }

    private string ResolveAppLabel(string appId, string clientName)
    {
        var app = hostRuntime.Apps.Get(appId);
        if (app is not null && !string.IsNullOrWhiteSpace(app.Name))
        {
            return app.Name.Trim();
        }

        return !string.IsNullOrWhiteSpace(clientName)
            ? clientName.Trim()
            : appId;
    }

    private static bool HasRecordingStarted(AppSessionSnapshot snapshot)
    {
        if (snapshot.DeviceProfile is not null || !string.IsNullOrWhiteSpace(snapshot.DeviceProfileJson))
        {
            return true;
        }

        if (snapshot.Metrics.Count > 0)
        {
            return true;
        }

        return snapshot.Logs.Any(log => !string.Equals(log.Source, "Host", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldNotifyAnalysis(SessionAnalysisRecord analysis, out string agentName)
    {
        agentName = string.Empty;
        if (analysis.CompletedUtc is null)
        {
            return false;
        }

        agentName = string.IsNullOrWhiteSpace(analysis.AgentId)
            ? "AI"
            : analysis.AgentId.Trim();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        hostRuntime.PairingEventOccurred -= RuntimeOnPairingEventOccurred;
        hostRuntime.TrendsEventOccurred -= RuntimeOnTrendsEventOccurred;
        hostRuntime.SessionTransferEventOccurred -= RuntimeOnSessionTransferEventOccurred;
        hostRuntime.SessionCaptureEventOccurred -= RuntimeOnSessionCaptureEventOccurred;
        hostRuntime.SessionUpdated -= RuntimeOnSessionUpdated;
        hostRuntime.SessionDeleted -= RuntimeOnSessionDeleted;
        if (hostRuntime.ActiveCompanion is { } companion)
        {
            companion.ConnectionsChanged -= CompanionOnConnectionsChanged;
            companion.AccessStatusChanged -= CompanionOnAccessStatusChanged;
        }
    }
}
