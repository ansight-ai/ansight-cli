using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

internal sealed class NativeProfileOperationServices
{
    public NativeProfileOperationServices(NativeProfilingService profiling)
    {
        Profiling = profiling ?? throw new ArgumentNullException(nameof(profiling));
    }

    public NativeProfilingService Profiling { get; }
}

internal abstract class NativeProfileOperation : IOperation
{
    protected NativeProfileOperation(NativeProfileOperationServices services)
    {
        Services = services;
    }

    protected NativeProfileOperationServices Services { get; }

    public abstract string Name { get; }

    protected abstract string Title { get; }

    protected abstract string Description { get; }

    protected abstract JsonObject InputSchema { get; }

    public JsonObject Definition => PayloadJson.CreateToolDefinition(Name, Title, Description, InputSchema);

    public abstract Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId);

    protected static JsonObject CaptureIdSchema()
        => ToolSchema.Object(
            properties: new Dictionary<string, ToolSchema>
            {
                ["captureId"] = ToolSchema.String("Required native profile capture identifier.")
            },
            required: ["captureId"],
            additionalProperties: false).ToJson();

    protected static bool TryReadCaptureId(
        JsonObject? arguments,
        out string? captureId,
        out RequestResult? error)
    {
        captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
        {
            error = ToolError("captureId is required.");
            return false;
        }

        error = null;
        return true;
    }

    protected static RequestResult ToolError(string message)
        => RequestResult.ToolResult(new JsonObject { ["message"] = message }, isError: true);

    protected static RequestResult ToolSuccess(JsonObject payload)
        => RequestResult.ToolResult(payload, isError: false);

    protected static JsonObject Serialize<T>(T value)
        => JsonSerializer.SerializeToNode(value, JsonUtil.Compact)?.AsObject() ?? new JsonObject();

    protected static Task<RequestResult> Completed(RequestResult result)
        => Task.FromResult(result);
}
