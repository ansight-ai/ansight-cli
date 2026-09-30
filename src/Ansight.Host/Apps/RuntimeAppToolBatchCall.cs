using System.Text.Json.Nodes;

namespace Ansight.Host.Apps;

public sealed record RuntimeAppToolBatchCall(
    string ToolId,
    JsonObject? Arguments = null,
    JsonObject? After = null,
    string? CallId = null);
