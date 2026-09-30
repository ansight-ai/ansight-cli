using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;

internal enum DeviceLifecycleAction
{
    StartDevice,
    LaunchApplication,
    ForegroundApplication,
    BackgroundApplication,
    TerminateApplication
}

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

internal sealed class DeviceLifecycleTool : Operation
{
    private readonly DeviceLifecycleAction action;

    public DeviceLifecycleTool(OperationServices services, DeviceLifecycleAction action)
        : base(services)
    {
        this.action = action;
    }

    public override string Name => action switch
    {
        DeviceLifecycleAction.StartDevice => "ansight_start_device",
        DeviceLifecycleAction.LaunchApplication => "ansight_launch_app",
        DeviceLifecycleAction.ForegroundApplication => "ansight_foreground_app",
        DeviceLifecycleAction.BackgroundApplication => "ansight_background_app",
        DeviceLifecycleAction.TerminateApplication => "ansight_terminate_app",
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override string Title => action switch
    {
        DeviceLifecycleAction.StartDevice => "Start Host Device",
        DeviceLifecycleAction.LaunchApplication => "Launch App On Host Device",
        DeviceLifecycleAction.ForegroundApplication => "Foreground App On Host Device",
        DeviceLifecycleAction.BackgroundApplication => "Background App On Host Device",
        DeviceLifecycleAction.TerminateApplication => "Terminate App On Host Device",
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override string Description => action switch
    {
        DeviceLifecycleAction.StartDevice =>
            "Boot an iOS Simulator or start an Android virtual device through Ansight's local device host.",
        DeviceLifecycleAction.LaunchApplication =>
            "Launch or foreground an installed app on a booted local simulator or emulator.",
        DeviceLifecycleAction.ForegroundApplication =>
            "Launch or activate the selected app to move it to the foreground on its booted local simulator or emulator.",
        DeviceLifecycleAction.BackgroundApplication =>
            "Move the selected app to the background by pressing Home on its booted local simulator or emulator.",
        DeviceLifecycleAction.TerminateApplication =>
            "Terminate an installed app on a booted local simulator or emulator.",
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildProperties(),
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryResolveTarget(arguments, out var target, out var error))
        {
            return ToolError(error);
        }

        var result = action switch
        {
            DeviceLifecycleAction.StartDevice => await deviceLifecycleRouter.StartDeviceAsync(
                target.DeviceIdentifier,
                CancellationToken.None),
            DeviceLifecycleAction.LaunchApplication => await deviceLifecycleRouter.LaunchApplicationAsync(
                target.DeviceIdentifier,
                target.BundleIdentifier!,
                CancellationToken.None),
            DeviceLifecycleAction.ForegroundApplication => await deviceLifecycleRouter.ForegroundApplicationAsync(
                target.DeviceIdentifier,
                target.BundleIdentifier!,
                CancellationToken.None),
            DeviceLifecycleAction.BackgroundApplication => await deviceLifecycleRouter.BackgroundApplicationAsync(
                target.DeviceIdentifier,
                target.BundleIdentifier!,
                CancellationToken.None),
            DeviceLifecycleAction.TerminateApplication => await deviceLifecycleRouter.TerminateApplicationAsync(
                target.DeviceIdentifier,
                target.BundleIdentifier!,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException()
        };

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["operation"] = result.Operation,
                ["isSuccess"] = result.IsSuccess,
                ["deviceId"] = result.DeviceIdentifier,
                ["platform"] = result.Platform,
                ["bundleIdentifier"] = result.BundleIdentifier,
                ["targetSource"] = target.Source,
                ["sessionId"] = target.SessionId,
                ["message"] = result.Message
            },
            isError: !result.IsSuccess);
    }

    private Dictionary<string, ToolSchema> BuildProperties()
    {
        var properties = new Dictionary<string, ToolSchema>
        {
            ["deviceId"] = ToolSchema.String(
                "Native iOS Simulator UDID or Android emulator/AVD identifier. When sessionId or appId is supplied, that session's recorded native device is enforced.",
                nullable: true),
            ["sessionId"] = ToolSchema.String(
                "Optional live or captured Ansight session whose recorded device should be targeted.",
                nullable: true),
            ["appId"] = ToolSchema.String(
                "Optional app id to resolve to one captured session. Prefer sessionId when relaunching a disconnected app.",
                nullable: true)
        };
        if (action is DeviceLifecycleAction.LaunchApplication
            or DeviceLifecycleAction.ForegroundApplication
            or DeviceLifecycleAction.BackgroundApplication
            or DeviceLifecycleAction.TerminateApplication)
        {
            properties["bundleIdentifier"] = ToolSchema.String(
                "Installed iOS bundle identifier or Android package identifier. A supplied session enforces its own app id.",
                nullable: true);
        }

        return properties;
    }

    private bool TryResolveTarget(
        JsonObject? arguments,
        out DeviceLifecycleTarget target,
        out string error)
    {
        var requestedDeviceIdentifier = NormalizeOptionalString(arguments?["deviceId"]?.GetValue<string>());
        var requestedSessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var requestedAppId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        var requestedBundleIdentifier = NormalizeOptionalString(arguments?["bundleIdentifier"]?.GetValue<string>());
        if (requestedSessionId is not null || requestedAppId is not null)
        {
            if (!sessionResolver.TryResolveSession(
                    arguments,
                    requireLiveSession: false,
                    out var snapshot,
                    out error))
            {
                target = DeviceLifecycleTarget.Empty;
                return false;
            }

            var isLiveSession = sessionResolver.GetLiveSessionIds().Contains(snapshot!.SessionId);
            if (action == DeviceLifecycleAction.BackgroundApplication && !isLiveSession)
            {
                target = DeviceLifecycleTarget.Empty;
                error = "Moving an app to the background requires a live app session.";
                return false;
            }

            var sessionDeviceIdentifier = uiInputRouter.ResolveDeviceIdentifier(
                snapshot!.SessionId,
                ResolveNativeDeviceIdentifier(snapshot));
            if (sessionDeviceIdentifier is null)
            {
                target = DeviceLifecycleTarget.Empty;
                error = "The live session does not report device.nativeDeviceId, so Ansight cannot safely control its host device.";
                return false;
            }

            if (requestedDeviceIdentifier is not null
                && !string.Equals(requestedDeviceIdentifier, sessionDeviceIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                target = DeviceLifecycleTarget.Empty;
                error = "deviceId does not match the host-enforced live session device.";
                return false;
            }

            if (requestedBundleIdentifier is not null
                && !string.Equals(requestedBundleIdentifier, snapshot!.AppId, StringComparison.Ordinal))
            {
                target = DeviceLifecycleTarget.Empty;
                error = "bundleIdentifier does not match the host-enforced live session app.";
                return false;
            }

            target = new DeviceLifecycleTarget(
                sessionDeviceIdentifier,
                action == DeviceLifecycleAction.StartDevice ? null : snapshot!.AppId,
                snapshot!.SessionId,
                isLiveSession
                    ? "liveSession"
                    : "capturedSession");
            error = string.Empty;
            return true;
        }

        if (requestedDeviceIdentifier is null)
        {
            target = DeviceLifecycleTarget.Empty;
            error = "deviceId, sessionId, or appId is required.";
            return false;
        }

        if (action == DeviceLifecycleAction.BackgroundApplication)
        {
            target = DeviceLifecycleTarget.Empty;
            error = "Moving an app to the background requires a live app session.";
            return false;
        }

        if (action is not DeviceLifecycleAction.StartDevice
            && requestedBundleIdentifier is null)
        {
            target = DeviceLifecycleTarget.Empty;
            error = "bundleIdentifier is required when no live session is supplied.";
            return false;
        }

        target = new DeviceLifecycleTarget(
            requestedDeviceIdentifier,
            requestedBundleIdentifier,
            null,
            "deviceId");
        error = string.Empty;
        return true;
    }

    internal static string? ResolveNativeDeviceIdentifier(AppSessionSnapshot snapshot)
    {
        foreach (var stream in snapshot.LogStreams)
        {
            if (stream.Metadata.TryGetValue("deviceUdid", out var deviceUdid)
                && !string.IsNullOrWhiteSpace(deviceUdid))
            {
                return deviceUdid.Trim();
            }

            if (stream.Metadata.TryGetValue("deviceSerial", out var deviceSerial)
                && !string.IsNullOrWhiteSpace(deviceSerial))
            {
                return deviceSerial.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.DeviceProfileJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(snapshot.DeviceProfileJson);
            return document.RootElement.TryGetProperty("device", out var device)
                   && device.ValueKind == JsonValueKind.Object
                   && device.TryGetProperty("nativeDeviceId", out var nativeDeviceId)
                   && nativeDeviceId.ValueKind == JsonValueKind.String
                ? NormalizeOptionalString(nativeDeviceId.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record DeviceLifecycleTarget(
    string DeviceIdentifier,
    string? BundleIdentifier,
    string? SessionId,
    string Source)
{
    public static DeviceLifecycleTarget Empty { get; } = new(string.Empty, null, null, string.Empty);
}
