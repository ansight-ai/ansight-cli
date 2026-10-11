using System.Text.Json.Nodes;
using Ansight.Host.Connectivity;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class ConnectivityInspectionTool(OperationServices services, string mode) : Operation(services)
{
    public override string Name => $"ansight_get_connectivity_{mode}";
    protected override string Title => $"Get Connectivity {mode}";
    protected override string Description => "Inspect recorded connectivity, transitions, link quality and observation gaps in live or imported captures. Times accept ISO-8601 or mm:ss offsets; optional annotationId centres the window.";
    protected override JsonObject InputSchema => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject
        {
            ["sessionId"] = new JsonObject { ["type"] = "string" },
            ["from"] = new JsonObject { ["type"] = "string" }, ["to"] = new JsonObject { ["type"] = "string" },
            ["at"] = new JsonObject { ["type"] = "string" }, ["annotationId"] = new JsonObject { ["type"] = "string" },
            ["beforeSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3600 },
            ["afterSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3600 },
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("snapshot", "change", "quality", "gap") },
            ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000 },
            ["cursor"] = new JsonObject { ["type"] = "string" }
        }
    };

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        try
        {
            if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var session, out var error)) return Task.FromResult(ToolError(error));
            if (session is null) return Task.FromResult(ToolError("Session was not found."));
            var origin = session.CreatedUtc;
            var from = SessionConnectivity.ParseTime(NetworkInspectionArguments.ReadString(arguments, "from"), origin);
            var to = SessionConnectivity.ParseTime(NetworkInspectionArguments.ReadString(arguments, "to"), origin);
            var at = SessionConnectivity.ParseTime(NetworkInspectionArguments.ReadString(arguments, "at"), origin);
            var annotationId = NetworkInspectionArguments.ReadString(arguments, "annotationId");
            if (annotationId is not null)
            {
                var annotation = session.Annotations.FirstOrDefault(value => value.AnnotationId == annotationId) ?? throw new ArgumentException("Annotation was not found in this session.");
                at ??= annotation.StartUtc;
                from ??= annotation.StartUtc.AddSeconds(-NetworkInspectionArguments.ReadLimit(arguments, "beforeSeconds", 120, 3600));
                to ??= (annotation.EndUtc ?? annotation.StartUtc).AddSeconds(NetworkInspectionArguments.ReadLimit(arguments, "afterSeconds", 30, 3600));
            }
            var payload = SessionConnectivity.Query(session, mode, from, to, at,
                NetworkInspectionArguments.ReadString(arguments, "kind"), NetworkInspectionArguments.ReadLimit(arguments, "limit", 200, 1000),
                NetworkInspectionArguments.ReadString(arguments, "cursor"));
            return Task.FromResult(RequestResult.ToolResult(payload, isError: false));
        }
        catch (ArgumentException exception) { return Task.FromResult(ToolError(exception.Message)); }
    }
}
