using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class HostSpanTool(OperationServices services) : Operation(services)
{
    private readonly ConcurrentDictionary<string, SpanStart> starts = new(StringComparer.Ordinal);
    public override string Name => "ansight_record_host_span";
    protected override string Title => "Measure a host operation span";
    protected override string Description => "Start or complete a named host-observed operation. Publishes events and a duration metric for Trends.";
    protected override JsonObject InputSchema => ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
    {
        ["sessionId"] = ToolSchema.String("Exact live session."),
        ["name"] = ToolSchema.String("Stable operation name.", nullable: true),
        ["group"] = ToolSchema.String("Optional stable comparison group.", nullable: true),
        ["phase"] = ToolSchema.String("start, completed, or failed."),
        ["spanId"] = ToolSchema.String("Token returned by start.", nullable: true)
    }, additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var session, out var error)) return Task.FromResult(ToolError(error));
        var phase = arguments?["phase"]?.GetValue<string>();
        if (phase == "start")
        {
            var name = arguments?["name"]?.GetValue<string>();
            var group = arguments?["group"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || group.Length > 160 || name.Any(char.IsControl) || group.Any(char.IsControl))
                return Task.FromResult(ToolError("A stable name and optional group of at most 160 characters are required."));
            foreach (var pair in starts.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Tick).TotalMinutes > 5))
                starts.TryRemove(pair.Key, out _);
            if (starts.Count >= 1024) return Task.FromResult(ToolError("Too many active measurement spans."));
            var id = Guid.NewGuid().ToString("N");
            starts[id] = new SpanStart(session!.SessionId, name, group, Stopwatch.GetTimestamp());
            HostSessionEvents.Publish(runtimeState, session.SessionId, name + ".started", "host.span.started", group);
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["spanId"] = id }, isError: false));
        }
        var spanId = arguments?["spanId"]?.GetValue<string>();
        if (phase is not ("completed" or "failed") || spanId is null || !starts.TryGetValue(spanId, out var span)
            || span.SessionId != session!.SessionId || !starts.TryRemove(spanId, out _))
            return Task.FromResult(ToolError("A matching active span token and completed/failed phase are required."));
        var duration = (long)Stopwatch.GetElapsedTime(span.Tick).TotalMilliseconds;
        var channel = HostSessionEvents.AllocateChannel(runtimeState, session.SessionId, span.Name + " duration", "duration", "ms", "host-task-v1", "span-duration");
        runtimeState.AddSessionMetrics(session.SessionId, [new SessionMetricSample
        {
            ChannelId = channel, Value = duration, CapturedAtUtc = DateTimeOffset.UtcNow
        }], 1);
        HostSessionEvents.Publish(runtimeState, session.SessionId, span.Name + "." + phase, "host.span." + phase, span.Group);
        return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["spanId"] = spanId, ["durationMs"] = duration }, isError: false));
    }

    private sealed record SpanStart(string SessionId, string Name, string Group, long Tick);
}
