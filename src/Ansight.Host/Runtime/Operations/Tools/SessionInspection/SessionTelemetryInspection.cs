using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class SessionTelemetryInspection
{
    public static RequestResult BuildGetTelemetryResult(SessionResolver sessionResolver, JsonObject? arguments)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", OperationDefaults.DefaultTelemetryResultLimit, OperationDefaults.MaxTelemetryResultLimit, out var limit, out errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid telemetry filters.");
        }

        var typeFilters = ArgumentReader.ReadStringSet(arguments, "types");
        typeFilters.Remove("all");
        var channelIdFilters = ArgumentReader.ReadByteSet(arguments, "channelIds");
        var channelNameFilters = ArgumentReader.ReadStringSet(arguments, "channelNames");
        var channelMap = snapshot!.MetricChannels.ToDictionary(channel => channel.ChannelId);

        var matchedSamples = snapshot.Metrics
            .Where(metric => PayloadJson.MatchesTimestamp(metric.CapturedAtUtc, startUtc, endUtc))
            .Where(metric => channelIdFilters.Count == 0 || channelIdFilters.Contains(metric.ChannelId))
            .Where(metric => channelNameFilters.Count == 0 || PayloadJson.MatchesChannelNameFilter(metric.ChannelId, channelMap, channelNameFilters))
            .Where(metric => typeFilters.Count == 0 || typeFilters.Contains(PayloadJson.ResolveTelemetryType(metric.ChannelId, channelMap)))
            .OrderBy(metric => metric.CapturedAtUtc)
            .ThenBy(metric => metric.ChannelId)
            .ToArray();

        var returnedSamples = matchedSamples.Length > limit
            ? matchedSamples.TakeLast(limit).ToArray()
            : matchedSamples;

        var groupedSamples = returnedSamples
            .GroupBy(metric => metric.ChannelId)
            .OrderBy(group => PayloadJson.ResolveTelemetryType(group.Key, channelMap), StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key)
            .Select(group =>
            {
                channelMap.TryGetValue(group.Key, out var channel);
                return (JsonNode?)new JsonObject
                {
                    ["channelId"] = group.Key,
                    ["name"] = channel?.Name ?? $"Channel {group.Key}",
                    ["type"] = PayloadJson.ResolveTelemetryType(group.Key, channelMap),
                    ["colorHex"] = channel?.ColorHex,
                    ["source"] = channel?.Source,
                    ["group"] = channel?.Group,
                    ["kind"] = channel?.Kind,
                    ["sampleCount"] = group.Count(),
                    ["samples"] = PayloadJson.CreateJsonArray(group.Select(metric => (JsonNode?)new JsonObject
                    {
                        ["capturedAtUtc"] = metric.CapturedAtUtc,
                        ["value"] = metric.Value
                    }))
                };
            });

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["clientName"] = snapshot.ClientName,
                ["status"] = snapshot.Status,
                ["filters"] = new JsonObject
                {
                    ["startUtc"] = startUtc,
                    ["endUtc"] = endUtc,
                    ["types"] = PayloadJson.CreateJsonArray(typeFilters.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).Select(type => JsonValue.Create(type))),
                    ["channelIds"] = PayloadJson.CreateJsonArray(channelIdFilters.OrderBy(id => id).Select(id => JsonValue.Create((int)id))),
                    ["channelNames"] = PayloadJson.CreateJsonArray(channelNameFilters.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => JsonValue.Create(name)))
                },
                ["matchedSampleCount"] = matchedSamples.Length,
                ["returnedSampleCount"] = returnedSamples.Length,
                ["isTruncated"] = matchedSamples.Length > returnedSamples.Length,
                ["telemetry"] = PayloadJson.CreateJsonArray(groupedSamples)
            },
            isError: false);
    }

    private static RequestResult ToolError(string message)
    {
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = message
            },
            isError: true);
    }
}
