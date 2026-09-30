using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal static class AutomationEventNormalizer
{
    public const string SessionLogReceivedKind = "session.log.received";
    public const string AppEventKind = "app.event";

    public static bool TryGetTriggerIndex(
        RuntimeEvent runtimeEvent,
        out RepositoryAutomationTriggerIndexKey index)
    {
        index = runtimeEvent switch
        {
            RuntimeClientAppStateChangedEvent appState => new RepositoryAutomationTriggerIndexKey(
                "app.lifecycle.changed",
                appState.AppId),
            RuntimePairingEvent pairing => new RepositoryAutomationTriggerIndexKey(
                ResolvePairingKind(pairing.Kind),
                pairing.AppId),
            RuntimeSessionCaptureEvent capture => new RepositoryAutomationTriggerIndexKey(
                ResolveCaptureKind(capture.Kind),
                capture.AppId),
            RuntimeSessionTransferEvent transfer => new RepositoryAutomationTriggerIndexKey(
                ResolveTransferKind(transfer.Kind),
                transfer.AppId),
            RuntimeAppEvent appEvent => new RepositoryAutomationTriggerIndexKey(
                AppEventKind,
                appEvent.AppId),
            RuntimeTrendsEvent trendsEvent => new RepositoryAutomationTriggerIndexKey(
                ResolveTrendsKind(trendsEvent.Kind),
                trendsEvent.AppId),
            _ => default
        };

        return !string.IsNullOrEmpty(index.EventKind);
    }

    public static bool TryNormalize(
        RuntimeEvent runtimeEvent,
        out AutomationEventEnvelope? envelope)
    {
        if (!TryGetTriggerIndex(runtimeEvent, out var index))
        {
            envelope = null;
            return false;
        }

        envelope = runtimeEvent switch
        {
            RuntimeClientAppStateChangedEvent appState => Create(
                index.EventKind,
                appState.OccurredAtUtc,
                appState.AppId,
                appState.SessionId,
                new JsonObject
                {
                    ["clientName"] = appState.ClientName,
                    ["previousState"] = appState.PreviousState.ToString(),
                    ["currentState"] = appState.CurrentState.ToString(),
                    ["changedAtUtc"] = appState.ChangedAtUtc
                }),
            RuntimePairingEvent pairing => Create(
                index.EventKind,
                pairing.OccurredAtUtc,
                pairing.AppId,
                pairing.SessionId,
                new JsonObject
                {
                    ["clientName"] = pairing.ClientName,
                    ["remoteAddress"] = pairing.RemoteAddress,
                    ["configId"] = pairing.ConfigId,
                    ["reasonCode"] = pairing.ReasonCode,
                    ["reasonMessage"] = pairing.ReasonMessage
                }),
            RuntimeSessionCaptureEvent capture => Create(
                index.EventKind,
                capture.OccurredAtUtc,
                capture.AppId,
                capture.SessionId,
                new JsonObject
                {
                    ["clientName"] = capture.ClientName,
                    ["status"] = capture.Status,
                    ["message"] = capture.Message
                }),
            RuntimeSessionTransferEvent transfer => Create(
                index.EventKind,
                transfer.OccurredAtUtc,
                transfer.AppId,
                transfer.SessionId,
                new JsonObject
                {
                    ["clientName"] = transfer.ClientName,
                    ["itemCount"] = transfer.ItemCount,
                    ["message"] = transfer.Message,
                    ["capturedAtUtc"] = transfer.CapturedAtUtc
                }),
            RuntimeAppEvent appEvent => Create(
                index.EventKind,
                appEvent.OccurredAtUtc,
                appEvent.AppId,
                appEvent.SessionId,
                new JsonObject
                {
                    ["label"] = appEvent.Label,
                    ["eventType"] = appEvent.EventType,
                    ["details"] = appEvent.Details,
                    ["channelId"] = appEvent.ChannelId,
                    ["clientName"] = appEvent.ClientName
                },
                appEvent.EventId),
            RuntimeTrendsEvent trendsEvent => Create(
                index.EventKind,
                trendsEvent.OccurredAtUtc,
                trendsEvent.AppId,
                trendsEvent.SessionId,
                new JsonObject
                {
                    ["definitionId"] = trendsEvent.DefinitionId,
                    ["spanGroup"] = trendsEvent.SpanGroup,
                    ["message"] = trendsEvent.Message,
                    ["trendsStatus"] = trendsEvent.TrendsStatus?.ToString(),
                    ["historyStatus"] = trendsEvent.HistoryStatus?.ToString(),
                    ["historyComparison"] = trendsEvent.HistoryComparison?.ToString()
                }),
            _ => null
        };

        return envelope is not null;
    }

    public static AutomationEventEnvelope NormalizeLog(
        SessionLogBatchEventArgs batch,
        LogEntry entry)
    {
        return Create(
            SessionLogReceivedKind,
            entry.TimestampUtc,
            batch.AppId,
            batch.SessionId,
            new JsonObject
            {
                ["streamId"] = batch.StreamId,
                ["message"] = entry.Message,
                ["priority"] = entry.Priority.ToString(),
                ["source"] = entry.Source,
                ["tag"] = entry.Tag,
                ["sourceEventId"] = entry.EventId,
                ["processId"] = entry.ProcessId,
                ["threadId"] = entry.ThreadId
            });
    }

    private static AutomationEventEnvelope Create(
        string kind,
        DateTimeOffset occurredAtUtc,
        string appId,
        string? sessionId,
        JsonObject payload,
        string? sourceEventId = null)
    {
        var eventId = string.IsNullOrWhiteSpace(sourceEventId)
            ? Guid.CreateVersion7().ToString("N")
            : sourceEventId.Trim();
        return new AutomationEventEnvelope
        {
            EventId = eventId,
            Kind = kind,
            OccurredAtUtc = occurredAtUtc.ToUniversalTime(),
            AppId = appId,
            SessionId = sessionId,
            CorrelationId = eventId,
            Payload = payload
        };
    }

    private static string ResolvePairingKind(RuntimePairingEventKind kind)
        => kind switch
        {
            RuntimePairingEventKind.DiscoveryReceived => "app.pairing.discoveryReceived",
            RuntimePairingEventKind.PairingAccepted => "app.pairing.accepted",
            RuntimePairingEventKind.PairingRejected => "app.pairing.rejected",
            _ => "app.pairing.unknown"
        };

    private static string ResolveCaptureKind(RuntimeSessionCaptureEventKind kind)
        => kind switch
        {
            RuntimeSessionCaptureEventKind.Started => "session.capture.started",
            RuntimeSessionCaptureEventKind.Updated => "session.capture.updated",
            RuntimeSessionCaptureEventKind.Stopped => "session.capture.stopped",
            RuntimeSessionCaptureEventKind.Finalized => "session.capture.finalized",
            _ => "session.capture.unknown"
        };

    private static string ResolveTransferKind(RuntimeSessionTransferKind kind)
        => kind switch
        {
            RuntimeSessionTransferKind.Telemetry => "session.transfer.telemetry",
            RuntimeSessionTransferKind.Log => "session.transfer.log",
            RuntimeSessionTransferKind.AppEvent => "session.transfer.appEvent",
            RuntimeSessionTransferKind.AppProfile => "session.transfer.appProfile",
            RuntimeSessionTransferKind.Screenshot => "session.transfer.screenshot",
            RuntimeSessionTransferKind.VisualTree => "session.transfer.visualTree",
            RuntimeSessionTransferKind.TouchInput => "session.transfer.touchInput",
            RuntimeSessionTransferKind.AnnotatedFeedback => "session.transfer.annotatedFeedback",
            _ => "session.transfer.unknown"
        };

    private static string ResolveTrendsKind(RuntimeTrendsEventKind kind)
        => kind switch
        {
            RuntimeTrendsEventKind.CheckFailed => "trends.check.failed",
            RuntimeTrendsEventKind.RegressionDetected => "trends.regression.detected",
            RuntimeTrendsEventKind.RegressionRecovered => "trends.regression.recovered",
            _ => "trends.unknown"
        };
}
