using System.Text.Json;
using Ansight.Infrastructure.Logging;

namespace Ansight.Host.Runtime.Diagnostics;

internal static class RuntimeEventLogger
{
    private static readonly ILogger log = Ansight.Infrastructure.Logging.Logger.Create();

    public static void Log(RuntimeEvent runtimeEvent)
    {
        switch (runtimeEvent)
        {
            case RuntimeLifecycleEvent lifecycleEvent:
                LogDebug(
                    "host_runtime_event",
                    new LogParameter("kind", lifecycleEvent.Kind.ToString()),
                    new LogParameter("message", lifecycleEvent.Message));
                break;
            case RuntimePairingEvent pairingEvent:
                LogDebug(
                    "pairing_event",
                    new LogParameter("kind", pairingEvent.Kind.ToString()),
                    new LogParameter("sessionId", pairingEvent.SessionId),
                    new LogParameter("appId", pairingEvent.AppId),
                    new LogParameter("clientName", pairingEvent.ClientName),
                    new LogParameter("remoteAddress", pairingEvent.RemoteAddress),
                    new LogParameter("configId", pairingEvent.ConfigId),
                    new LogParameter("reasonCode", pairingEvent.ReasonCode),
                    new LogParameter("reasonMessage", pairingEvent.ReasonMessage));
                if (pairingEvent.Kind is RuntimePairingEventKind.PairingAccepted or RuntimePairingEventKind.PairingRejected)
                {
                    log.Event(
                        "pairing_completed",
                        ("accepted", pairingEvent.Kind == RuntimePairingEventKind.PairingAccepted),
                        ("appId", pairingEvent.AppId),
                        ("configId", pairingEvent.ConfigId),
                        ("reasonCode", pairingEvent.ReasonCode));
                }

                break;
            case RuntimeClientAppStateChangedEvent clientAppStateChangedEvent:
                LogDebug(
                    "client_app_state_changed",
                    new LogParameter("sessionId", clientAppStateChangedEvent.SessionId),
                    new LogParameter("appId", clientAppStateChangedEvent.AppId),
                    new LogParameter("clientName", clientAppStateChangedEvent.ClientName),
                    new LogParameter("previousState", clientAppStateChangedEvent.PreviousState.ToString()),
                    new LogParameter("currentState", clientAppStateChangedEvent.CurrentState.ToString()),
                    new LogParameter("changedAtUtc", clientAppStateChangedEvent.ChangedAtUtc));
                break;
            case RuntimeSessionCaptureEvent sessionCaptureEvent:
                if (sessionCaptureEvent.Kind == RuntimeSessionCaptureEventKind.Updated)
                {
                    LogVerbose(
                        "session_capture_event",
                        new LogParameter("kind", sessionCaptureEvent.Kind.ToString()),
                        new LogParameter("sessionId", sessionCaptureEvent.SessionId),
                        new LogParameter("appId", sessionCaptureEvent.AppId),
                        new LogParameter("clientName", sessionCaptureEvent.ClientName),
                        new LogParameter("status", sessionCaptureEvent.Status),
                        new LogParameter("message", sessionCaptureEvent.Message));
                }
                else
                {
                    LogDebug(
                        "session_capture_event",
                        new LogParameter("kind", sessionCaptureEvent.Kind.ToString()),
                        new LogParameter("sessionId", sessionCaptureEvent.SessionId),
                        new LogParameter("appId", sessionCaptureEvent.AppId),
                        new LogParameter("clientName", sessionCaptureEvent.ClientName),
                        new LogParameter("status", sessionCaptureEvent.Status),
                        new LogParameter("message", sessionCaptureEvent.Message));
                    if (sessionCaptureEvent.Kind is RuntimeSessionCaptureEventKind.Started or RuntimeSessionCaptureEventKind.Stopped)
                    {
                        log.Event(
                            sessionCaptureEvent.Kind == RuntimeSessionCaptureEventKind.Started
                                ? "live_session_started"
                                : "live_session_stopped",
                            ("appId", sessionCaptureEvent.AppId),
                            ("sessionId", sessionCaptureEvent.SessionId),
                            ("status", sessionCaptureEvent.Status));
                    }
                }
                break;
            case RuntimeSessionTransferEvent sessionTransferEvent:
                if (sessionTransferEvent.Kind == RuntimeSessionTransferKind.Telemetry)
                {
                    LogVerbose(
                        "session_transfer_event",
                        new LogParameter("kind", sessionTransferEvent.Kind.ToString()),
                        new LogParameter("sessionId", sessionTransferEvent.SessionId),
                        new LogParameter("appId", sessionTransferEvent.AppId),
                        new LogParameter("clientName", sessionTransferEvent.ClientName),
                        new LogParameter("itemCount", sessionTransferEvent.ItemCount),
                        new LogParameter("message", sessionTransferEvent.Message),
                        new LogParameter("capturedAtUtc", sessionTransferEvent.CapturedAtUtc));
                }
                else
                {
                    LogDebug(
                        "session_transfer_event",
                        new LogParameter("kind", sessionTransferEvent.Kind.ToString()),
                        new LogParameter("sessionId", sessionTransferEvent.SessionId),
                        new LogParameter("appId", sessionTransferEvent.AppId),
                        new LogParameter("clientName", sessionTransferEvent.ClientName),
                        new LogParameter("itemCount", sessionTransferEvent.ItemCount),
                        new LogParameter("message", sessionTransferEvent.Message),
                        new LogParameter("capturedAtUtc", sessionTransferEvent.CapturedAtUtc));
                }
                break;
            case RuntimeAppEvent appEvent:
                LogVerbose(
                    "app_event",
                    new LogParameter("eventId", appEvent.EventId),
                    new LogParameter("sessionId", appEvent.SessionId),
                    new LogParameter("appId", appEvent.AppId),
                    new LogParameter("clientName", appEvent.ClientName),
                    new LogParameter("label", appEvent.Label),
                    new LogParameter("eventType", appEvent.EventType),
                    new LogParameter("details", appEvent.Details),
                    new LogParameter("channelId", appEvent.ChannelId));
                break;
            case RuntimeTrendsEvent trendsEvent:
                LogDebug(
                    "trends_event",
                    new LogParameter("kind", trendsEvent.Kind.ToString()),
                    new LogParameter("sessionId", trendsEvent.SessionId),
                    new LogParameter("appId", trendsEvent.AppId),
                    new LogParameter("definitionId", trendsEvent.DefinitionId),
                    new LogParameter("spanGroup", trendsEvent.SpanGroup),
                    new LogParameter("historyComparison", trendsEvent.HistoryComparison?.ToString()),
                    new LogParameter("message", trendsEvent.Message));
                break;
        }
    }

    private static void LogDebug(string eventName, params LogParameter[] parameters)
        => log.Debug(BuildMessage(eventName, parameters));

    private static void LogVerbose(string eventName, params LogParameter[] parameters)
        => log.Verbose(BuildMessage(eventName, parameters));

    private static string BuildMessage(string eventName, IReadOnlyList<LogParameter> parameters)
    {
        var builder = new StringBuilder(eventName.Trim());
        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            builder.Append(' ');
            builder.Append(parameter.Name.Trim());
            builder.Append('=');
            builder.Append(SerializeValue(parameter.Value));
        }

        return builder.ToString();
    }

    private static string SerializeValue(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return JsonSerializer.Serialize(value?.ToString());
        }
    }

    private static int GetPayloadCharacterCount(string? payload)
        => string.IsNullOrWhiteSpace(payload) ? 0 : payload.Length;
}
