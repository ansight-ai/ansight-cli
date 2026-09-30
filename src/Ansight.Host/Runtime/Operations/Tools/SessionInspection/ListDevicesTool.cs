using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class ListDevicesTool : Operation
{
    public ListDevicesTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_devices";

    protected override string Title => "List Devices";

    protected override string Description => "List connected or historical devices and simulators paired through Ansight.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(RequestResult.ToolResult(
            SessionListingPayloads.BuildListDevicesPayload(runtimeState, sessionResolver, arguments),
            isError: false));

    private static Dictionary<string, ToolSchema> BuildProperties()
    {
        var properties = new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Optional app id to filter results.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("Include disconnected and persisted sessions.", nullable: true)
        };
        SessionListingFilters.AddProperties(properties);
        return properties;
    }
}
