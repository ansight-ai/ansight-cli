namespace Ansight.Host.Runtime.AppTools;

using System.Text.Json.Nodes;

internal static class AppToolProtocolContracts
{
    public const string BatchType = "tool.batch";
    public const string BatchResultType = "tool.batch.result";
}

internal sealed record AppToolBatchCall(
    string ToolId,
    JsonObject? Arguments = null,
    JsonObject? After = null,
    string? CallId = null);
