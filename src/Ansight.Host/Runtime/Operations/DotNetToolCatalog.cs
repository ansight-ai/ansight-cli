using System.Text.Json.Nodes;
using static Ansight.Host.Runtime.Operations.Tools.Shared.OperationRegistration;

namespace Ansight.Host.Runtime.Operations;

internal sealed class DotNetToolCatalog
{
    private readonly OperationRegistry tools;

    public DotNetToolCatalog(DotNetOperationServices services)
    {
        tools = new OperationRegistry(new OperationRegistration[]
        {
            HostOnly(new DotNetGetCaptureRequirementsTool(services)),
            HostOnly(new DotNetStartStartupCaptureTool(services)),
            HostOnly(new DotNetGetCaptureStatusTool(services)),
            HostOnly(new DotNetCancelCaptureTool(services)),
            HostOnly(new DotNetListCapturesTool(services)),
            HostOnly(new DotNetImportTraceTool(services)),
            HostOnly(new DotNetGetCaptureManifestTool(services)),
            HostOnly(new DotNetGetTraceOverviewTool(services)),
            HostOnly(new DotNetGetStartupTimelineTool(services)),
            HostOnly(new DotNetGetCpuHotspotsTool(services)),
            HostOnly(new DotNetGetCallTreeTool(services)),
            HostOnly(new DotNetGetThreadActivityTool(services)),
            HostOnly(new DotNetGetGcSummaryTool(services)),
            HostOnly(new DotNetGetJitSummaryTool(services)),
            HostOnly(new DotNetGetExceptionsTool(services)),
            HostOnly(new DotNetExportSpeedScopeTool(services))
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
            : Task.FromResult(RequestResult.Error(-32602, $"Unknown .NET tool '{toolName}'."));
    }

    public bool Contains(string toolName)
        => tools.Contains(toolName);
}
