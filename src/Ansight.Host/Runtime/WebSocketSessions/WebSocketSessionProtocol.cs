namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Text.Json.Nodes;
using AppLifecycleState = global::Ansight.AppLifecycleState;

internal static class WebSocketSessionProtocol
{
    internal static bool TryParseCustomProperties(
        JsonObject? payload,
        bool requireCustomProperties,
        out JsonObject? customProperties,
        out string? error)
    {
        customProperties = null;
        error = null;

        if (payload is null || !payload.TryGetPropertyValue("customProperties", out var customPropertiesNode))
        {
            if (requireCustomProperties)
            {
                error = "Session properties payload must include a customProperties object.";
                return false;
            }

            return true;
        }

        if (customPropertiesNode is not JsonObject customPropertiesObject)
        {
            error = "customProperties must be a JSON object.";
            return false;
        }

        customProperties = NormalizeCustomProperties(customPropertiesObject);
        return true;
    }

    private static JsonObject? NormalizeCustomProperties(JsonObject customProperties)
    {
        var normalized = new JsonObject();
        foreach (var group in customProperties)
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.Value is not JsonObject groupObject)
            {
                continue;
            }

            var normalizedGroup = new JsonObject();
            foreach (var property in groupObject)
            {
                if (string.IsNullOrWhiteSpace(property.Key) || property.Value is JsonObject or JsonArray)
                {
                    continue;
                }

                normalizedGroup[property.Key.Trim()] = property.Value?.DeepClone();
            }

            if (normalizedGroup.Count > 0)
            {
                normalized[group.Key.Trim()] = normalizedGroup;
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    internal static LogEntry CreateClientEventLog(SessionApplicationEvent @event)
    {
        var type = @event.EventType.ToUpperInvariant();
        var details = string.IsNullOrWhiteSpace(@event.Details)
            ? @event.Label
            : $"{@event.Label}: {@event.Details}";

        return new LogEntry(@event.CapturedAtUtc, details)
        {
            Source = "Client",
            Tag = type,
            EventId = @event.EventId,
            Priority = MapClientEventPriority(type)
        };
    }

    private static LogPriority MapClientEventPriority(string eventType)
    {
        return eventType switch
        {
            "DEBUG" => LogPriority.Debug,
            "WARNING" => LogPriority.Warning,
            "ERROR" or "EXCEPTION" => LogPriority.Error,
            _ => LogPriority.Information
        };
    }

    internal static LogEntry CreateClientLogEntry(string message, string tag, LogPriority priority = LogPriority.Information)
    {
        return new LogEntry(DateTimeOffset.UtcNow, message)
        {
            Source = "Client",
            Tag = tag,
            Priority = priority
        };
    }

    internal static LogEntry CreateLogEntry(string message, string tag, LogPriority priority = LogPriority.Information)
    {
        return new LogEntry(DateTimeOffset.UtcNow, message)
        {
            Source = "Host",
            Tag = tag,
            Priority = priority
        };
    }

    internal static DateTimeOffset? ParseControlChangedAtUtc(JsonObject? payload)
    {
        var value = payload?["changedAtUtc"]?.GetValue<string>();
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    internal static AppLifecycleState? ParseControlAppLifecycleState(JsonObject? payload)
    {
        var value = payload?["state"]?.GetValue<string>();
        return value?.Trim().ToLowerInvariant() switch
        {
            "foreground" => AppLifecycleState.Foreground,
            "background" => AppLifecycleState.Background,
            "unknown" => AppLifecycleState.Unknown,
            _ => null
        };
    }
}
