using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLocation;

internal sealed class ClearDeviceLocationTool : DeviceHostTargetOperation
{
    public ClearDeviceLocationTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_clear_device_location";

    protected override string Title => "Clear Device Location";

    protected override string Description =>
        "Clear simulated location on an iOS Simulator. Android emulators retain their last injected fix until another location is set.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildTargetProperties(),
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryResolveTarget(arguments, out var target, out var targetError))
        {
            return ToolError(targetError);
        }

        var result = await deviceLocationRouter.ClearLocationAsync(
            new ClearDeviceLocationRequest(target.DeviceIdentifier),
            CancellationToken.None);
        return RequestResult.ToolResult(
            BuildResultPayload("clear", target, result),
            isError: !result.IsSuccess);
    }
}
