namespace Ansight.Host.Models.Session;

using System.Globalization;
using System.Text.Json.Nodes;
using Ansight.Host.Workspaces.Targets;

internal static class SessionCaptureEndResolver
{
    internal static DateTimeOffset Resolve(string captureSource, bool isHistorical,
        JsonObject? customProperties, DateTimeOffset createdUtc, DateTimeOffset declaredEndUtc)
    {
        return TryResolve(captureSource, isHistorical, customProperties, createdUtc)
               ?? declaredEndUtc.ToUniversalTime();
    }

    internal static DateTimeOffset? TryResolve(string captureSource, bool isHistorical,
        JsonObject? customProperties, DateTimeOffset createdUtc)
    {
        var normalizedCreatedUtc = createdUtc.ToUniversalTime();
        if (!isHistorical || captureSource != WorkspaceExecutionModes.Device)
            return null;

        if (TryReadEnd(customProperties?["captureEndedUtc"], normalizedCreatedUtc, out var captureEndedUtc))
            return captureEndedUtc;

        // Captures recorded before captureEndedUtc was persisted can use the trace's stop time.
        if (TryReadEnd(customProperties?["instruments"]?["completedUtc"], normalizedCreatedUtc,
                out var instrumentsCompletedUtc))
            return instrumentsCompletedUtc;

        return null;
    }

    private static bool TryReadEnd(JsonNode? value, DateTimeOffset createdUtc,
        out DateTimeOffset endedUtc)
    {
        DateTimeOffset parsedUtc = default;
        var isValid = value is JsonValue jsonValue
                      && (jsonValue.TryGetValue(out parsedUtc)
                          || jsonValue.TryGetValue<string>(out var text)
                          && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                              DateTimeStyles.RoundtripKind, out parsedUtc));
        if (isValid
            && parsedUtc.ToUniversalTime() >= createdUtc)
        {
            endedUtc = parsedUtc.ToUniversalTime();
            return true;
        }

        endedUtc = default;
        return false;
    }
}
