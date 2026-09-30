using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal abstract class TrimSessionTool : Operation
{
    private readonly SessionTimelineTrimMode mode;
    private readonly string operation;

    protected TrimSessionTool(OperationServices services, SessionTimelineTrimMode mode, string operation)
        : base(services)
    {
        this.mode = mode;
        this.operation = operation;
    }

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: TrimProperties(),
        required: ["startUtc", "endUtc"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Select a session to trim."));
        }

        if (!TryReadRange(arguments, out var startUtc, out var endUtc, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid trim range."));
        }

        var beforeCounts = BuildCountsPayload(snapshot!);
        var rangePayload = BuildRangePayload(startUtc, endUtc);
        var result = runtimeState.TrimSessionTimeline(snapshot!.SessionId, startUtc, endUtc, mode);
        if (!result.IsSuccess)
        {
            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject
                {
                    ["message"] = result.Message,
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["operation"] = operation,
                    ["mode"] = mode.ToString(),
                    ["range"] = rangePayload,
                    ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, SessionReviewContext.IsLiveSession(sessionResolver, snapshot))
                },
                isError: true));
        }

        if (!runtimeState.TryGetSessionSnapshot(snapshot.SessionId, out var updatedSnapshot) || updatedSnapshot is null)
        {
            updatedSnapshot = snapshot;
        }

        var afterCounts = BuildCountsPayload(updatedSnapshot);
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sessionId"] = updatedSnapshot.SessionId,
                ["appId"] = updatedSnapshot.AppId,
                ["operation"] = operation,
                ["mode"] = mode.ToString(),
                ["range"] = rangePayload,
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(updatedSnapshot, SessionReviewContext.IsLiveSession(sessionResolver, updatedSnapshot)),
                ["beforeCounts"] = beforeCounts,
                ["afterCounts"] = afterCounts,
                ["removedCounts"] = BuildRemovedCountsPayload(snapshot, updatedSnapshot)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> TrimProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["startUtc"] = ToolSchema.String("Inclusive trim selection start timestamp in ISO-8601 UTC.", format: "date-time");
        properties["endUtc"] = ToolSchema.String("Inclusive trim selection end timestamp in ISO-8601 UTC.", format: "date-time");
        return properties;
    }

    private static bool TryReadRange(
        JsonObject? arguments,
        out DateTimeOffset startUtc,
        out DateTimeOffset endUtc,
        out string? errorMessage)
    {
        startUtc = default;
        endUtc = default;
        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var parsedStartUtc, out errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var parsedEndUtc, out errorMessage))
        {
            return false;
        }

        if (!parsedStartUtc.HasValue || !parsedEndUtc.HasValue)
        {
            errorMessage = "startUtc and endUtc are required.";
            return false;
        }

        startUtc = parsedStartUtc.Value.ToUniversalTime();
        endUtc = parsedEndUtc.Value.ToUniversalTime();
        if (endUtc < startUtc)
        {
            (startUtc, endUtc) = (endUtc, startUtc);
        }

        if (endUtc == startUtc)
        {
            errorMessage = "startUtc and endUtc must define a non-empty range.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static JsonObject BuildRangePayload(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        return new JsonObject
        {
            ["startUtc"] = startUtc,
            ["endUtc"] = endUtc
        };
    }

    private static JsonObject BuildCountsPayload(AppSessionSnapshot snapshot)
    {
        return new JsonObject
        {
            ["logs"] = snapshot.Logs.Count,
            ["screenshots"] = snapshot.Images.Count,
            ["touches"] = snapshot.Touches.Count,
            ["visualTrees"] = snapshot.VisualTreeSnapshots.Count,
            ["artifactSnapshots"] = snapshot.ArtifactSnapshots.Count,
            ["annotations"] = snapshot.Annotations.Count,
            ["analyses"] = snapshot.Analyses.Count,
            ["metricChannels"] = snapshot.MetricChannels.Count,
            ["metricSamples"] = snapshot.Metrics.Count
        };
    }

    private static JsonObject BuildRemovedCountsPayload(AppSessionSnapshot before, AppSessionSnapshot after)
    {
        return new JsonObject
        {
            ["logs"] = Math.Max(0, before.Logs.Count - after.Logs.Count),
            ["screenshots"] = Math.Max(0, before.Images.Count - after.Images.Count),
            ["touches"] = Math.Max(0, before.Touches.Count - after.Touches.Count),
            ["visualTrees"] = Math.Max(0, before.VisualTreeSnapshots.Count - after.VisualTreeSnapshots.Count),
            ["artifactSnapshots"] = Math.Max(0, before.ArtifactSnapshots.Count - after.ArtifactSnapshots.Count),
            ["annotations"] = Math.Max(0, before.Annotations.Count - after.Annotations.Count),
            ["analyses"] = Math.Max(0, before.Analyses.Count - after.Analyses.Count),
            ["metricChannels"] = Math.Max(0, before.MetricChannels.Count - after.MetricChannels.Count),
            ["metricSamples"] = Math.Max(0, before.Metrics.Count - after.Metrics.Count)
        };
    }
}
