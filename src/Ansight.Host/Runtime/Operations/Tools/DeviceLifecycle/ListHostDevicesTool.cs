using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;

internal sealed class ListHostDevicesTool : Operation
{
    public ListHostDevicesTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_host_devices";

    protected override string Title => "List Host Devices";

    protected override string Description =>
        "List the local iOS Simulators and Android virtual devices that Ansight can start or control, including installed app identifiers when available.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Optional live session used by a host-enforced agent scope.", nullable: true),
            ["appId"] = ToolSchema.String("Optional live app used by a host-enforced agent scope.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var devices = await deviceLifecycleRouter.ListDevicesAsync(CancellationToken.None);
        var requestedSessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var requestedAppId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());

        if (requestedSessionId is not null || requestedAppId is not null)
        {
            if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var error))
            {
                return ToolError(error);
            }

            var enforcedDeviceIdentifier = uiInputRouter.ResolveDeviceIdentifier(
                snapshot!.SessionId,
                DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot));

            if (enforcedDeviceIdentifier is null)
            {
                return ToolError(
                    "The live session does not report device.nativeDeviceId, so Ansight cannot identify its host device.");
            }

            devices = devices
                .Where(device =>
                    string.Equals(
                        device.Identifier,
                        enforcedDeviceIdentifier,
                        StringComparison.OrdinalIgnoreCase)
                        || device.IsBooted &&
                    device.Platform == DevicePlatforms.Android &&
                    DeviceKinds.IsVirtual(device.Kind) &&
                    string.Equals(
                        device.Name,
                        enforcedDeviceIdentifier,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["count"] = devices.Count,
                ["devices"] = PayloadJson.CreateJsonArray(devices.Select(static device =>
                    (JsonNode?)new JsonObject
                    {
                        ["deviceId"] = device.Identifier,
                        ["name"] = device.Name,
                        ["platform"] = device.Platform,
                        ["runtime"] = device.Runtime,
                        ["state"] = device.State,
                        ["isBooted"] = device.IsBooted,
                        ["kind"] = device.Kind,
                        ["isPhysical"] = DeviceKinds.IsPhysical(device.Kind),
                        ["installedApplications"] = PayloadJson.CreateJsonArray(
                            device.InstalledApplications.Select(static application =>
                                (JsonNode?)new JsonObject
                                {
                                    ["bundleIdentifier"] = application.BundleIdentifier,
                                    ["name"] = application.Name
                                }))
                    }))
            },
            isError: false);
    }
}
