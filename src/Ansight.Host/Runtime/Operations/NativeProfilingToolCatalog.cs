using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.NativeProfiling;
using static Ansight.Host.Runtime.Operations.Tools.Shared.OperationRegistration;

namespace Ansight.Host.Runtime.Operations;

internal sealed class NativeProfilingToolCatalog
{
    private readonly OperationRegistry tools;

    public NativeProfilingToolCatalog(NativeProfileOperationServices services)
    {
        tools = new OperationRegistry(new OperationRegistration[]
        {
            HostOnly(new NativeListCapturesTool(services)),
            HostOnly(new NativeGetCaptureManifestTool(services)),
            HostOnly(new NativeGetCaptureArtifactTool(services)),
            HostOnly(new NativeGetTraceOverviewTool(services)),
            HostOnly(new IosGetInstrumentsTocTool(services)),
            HostOnly(new AndroidQueryPerfettoTool(services))
        });
    }

    public OperationRegistry HostToolRegistry => tools;

    public JsonObject BuildToolsListResult()
        => new() { ["tools"] = tools.BuildDefinitions() };

    public Task<RequestResult> HandleToolsCallAsync(JsonObject? parameters, string? correlationId = null)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Task.FromResult(RequestResult.Error(-32602, "tools/call requires a non-empty tool name."));
        }

        var arguments = parameters?["arguments"] as JsonObject;
        return tools.TryGet(toolName, out var tool)
            ? tool!.ExecuteAsync(arguments, correlationId)
            : Task.FromResult(RequestResult.Error(-32602, $"Unknown native profiling tool '{toolName}'."));
    }

    public bool Contains(string toolName)
        => tools.Contains(toolName);
}
