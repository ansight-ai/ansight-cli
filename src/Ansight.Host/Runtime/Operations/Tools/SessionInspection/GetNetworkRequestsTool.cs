using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class GetNetworkRequestsTool : Operation
{
    private readonly NetworkRequestSnapshotStore snapshotStore;

    public GetNetworkRequestsTool(OperationServices services, NetworkRequestSnapshotStore? snapshotStore = null)
        : base(services)
        => this.snapshotStore = snapshotStore ?? new NetworkRequestSnapshotStore();

    public override string Name => "ansight_get_network_requests";
    protected override string Title => "Get Network Requests";
    protected override string Description => "Inspect received network capture records, newest first, using bounded pages of stable snapshot summaries without headers or body content.";
    protected override JsonObject InputSchema => NetworkInspectionToolSchemas.Query();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        try
        {
            var limit = NetworkInspectionArguments.ReadLimit(arguments, "limit", 200, 1000);
            var cursor = NetworkInspectionArguments.ReadString(arguments, "cursor");
            if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var session, out var error))
            {
                return Task.FromResult(ToolError(error));
            }

            var offset = 0;
            var snapshot = cursor is null
                ? snapshotStore.Create(session!, NetworkInspectionFilters.Read(arguments))
                : snapshotStore.Resolve(cursor, session!.SessionId, out offset);
            if (cursor is not null)
            {
                snapshot.Filters.ValidateContinuation(arguments);
            }

            var requests = new JsonArray();
            var payload = new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["filters"] = snapshot.Filters.ToJson(),
                ["requests"] = requests,
                ["matchedRequestCount"] = snapshot.Summaries.Count,
                ["returnedRequestCount"] = 0,
                ["isTruncated"] = false,
                ["hasMore"] = false,
                ["nextCursor"] = null
            };
            var serializedCharacters = payload.ToJsonString().Length + 1024;
            while (offset + requests.Count < snapshot.Summaries.Count && requests.Count < limit)
            {
                var summary = snapshot.Summaries[offset + requests.Count];
                if (summary.Length + 1 > NetworkInspectionPayload.MaximumSerializedCharacters - serializedCharacters)
                {
                    if (requests.Count == 0)
                    {
                        return Task.FromResult(ToolError("A network request summary exceeds the serialized output budget."));
                    }

                    break;
                }

                requests.Add(JsonNode.Parse(summary));
                serializedCharacters += summary.Length + 1;
            }

            var hasMore = offset + requests.Count < snapshot.Summaries.Count;
            payload["returnedRequestCount"] = requests.Count;
            payload["isTruncated"] = hasMore;
            payload["hasMore"] = hasMore;
            payload["nextCursor"] = hasMore ? NetworkRequestSnapshotStore.Cursor(snapshot, offset + requests.Count) : null;
            if (cursor is null && hasMore)
            {
                snapshotStore.Retain(snapshot);
            }

            return Task.FromResult(NetworkInspectionPayload.Result(payload));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(ToolError(exception.Message));
        }
    }
}
