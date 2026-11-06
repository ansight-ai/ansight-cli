namespace Ansight.Host.Runtime.AppTools;

using System.Text.Json.Nodes;

internal sealed record AppToolBatchCall(
    string ToolId,
    JsonObject? Arguments = null,
    JsonObject? After = null,
    string? CallId = null);
