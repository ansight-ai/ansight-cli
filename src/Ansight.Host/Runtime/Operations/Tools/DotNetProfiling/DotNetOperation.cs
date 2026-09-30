using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal abstract class DotNetOperation : IOperation
{
    protected DotNetOperation(DotNetOperationServices services)
    {
        Services = services;
    }

    protected DotNetOperationServices Services { get; }

    public abstract string Name { get; }

    protected abstract string Title { get; }

    protected abstract string Description { get; }

    protected abstract JsonObject InputSchema { get; }

    public JsonObject Definition => PayloadJson.CreateToolDefinition(Name, Title, Description, InputSchema);

    public abstract Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId);

    protected static RequestResult ToolError(string message)
        => RequestResult.ToolResult(new JsonObject { ["message"] = message }, isError: true);

    protected static RequestResult ToolSuccess(JsonObject payload)
        => RequestResult.ToolResult(payload, isError: false);

    protected static Task<RequestResult> Completed(RequestResult result)
        => Task.FromResult(result);
}
