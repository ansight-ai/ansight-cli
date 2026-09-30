using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLocation;

internal abstract class DeviceLocationOperation : Operation
{
    protected DeviceLocationOperation(OperationServices services)
        : base(services)
    {
    }

    protected static Dictionary<string, ToolSchema> BuildTargetProperties()
        => new()
        {
            ["deviceId"] = ToolSchema.String(
                "Native iOS Simulator UDID or Android emulator serial. Alternatively provide sessionId/appId. Omit all targets only when the configured location driver supports a host-selected device.",
                nullable: true),
            ["sessionId"] = ToolSchema.String(
                "Live Ansight session whose device.nativeDeviceId should be targeted.",
                nullable: true),
            ["appId"] = ToolSchema.String(
                "Live app id to target when exactly one session is connected for that app.",
                nullable: true)
        };

    protected bool TryResolveTarget(
        JsonObject? arguments,
        out DeviceLocationTarget target,
        out string error)
    {
        var requestedDeviceIdentifier = NormalizeOptionalString(arguments?["deviceId"]?.GetValue<string>());
        if (requestedDeviceIdentifier is not null)
        {
            target = new DeviceLocationTarget(
                requestedDeviceIdentifier,
                SessionId: null,
                AppId: null,
                Source: "deviceId");
            error = string.Empty;
            return true;
        }

        var requestedSessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var requestedAppId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        if (requestedSessionId is null && requestedAppId is null)
        {
            target = new DeviceLocationTarget(
                DeviceIdentifier: null,
                SessionId: null,
                AppId: null,
                Source: "hostSelection");
            error = string.Empty;
            return true;
        }

        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out error))
        {
            target = DeviceLocationTarget.Empty;
            return false;
        }

        var deviceIdentifier = ResolveNativeDeviceIdentifier(snapshot!);
        if (deviceIdentifier is null)
        {
            target = DeviceLocationTarget.Empty;
            error = "The live session does not report device.nativeDeviceId, so Ansight cannot safely target its simulator or emulator.";
            return false;
        }

        target = new DeviceLocationTarget(
            deviceIdentifier,
            snapshot!.SessionId,
            snapshot.AppId,
            Source: "liveSession");
        return true;
    }

    protected static JsonObject BuildResultPayload(
        string operation,
        DeviceLocationTarget target,
        DeviceLocationResult result)
        => new()
        {
            ["operation"] = operation,
            ["isSuccess"] = result.IsSuccess,
            ["targetSource"] = target.Source,
            ["sessionId"] = target.SessionId,
            ["appId"] = target.AppId,
            ["deviceId"] = result.DeviceIdentifier ?? target.DeviceIdentifier,
            ["deviceName"] = result.DeviceName,
            ["platform"] = result.Platform,
            ["backend"] = result.Backend,
            ["message"] = result.Message
        };

    private static string? ResolveNativeDeviceIdentifier(AppSessionSnapshot snapshot)
        => DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot);
}

internal sealed record DeviceLocationTarget(
    string? DeviceIdentifier,
    string? SessionId,
    string? AppId,
    string Source)
{
    public static DeviceLocationTarget Empty { get; } = new(null, null, null, string.Empty);
}
