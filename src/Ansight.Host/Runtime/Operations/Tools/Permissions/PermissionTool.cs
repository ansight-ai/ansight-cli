using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Permissions;

internal sealed class PermissionTool : Operation
{
    private readonly DevicePermissionsRouter router;
    private readonly string action;
    private readonly string? platform;

    public PermissionTool(OperationServices services, string action, string? platform) : base(services)
    {
        router = services.DevicePermissionsRouter;
        this.action = action;
        this.platform = platform;
    }

    public override string Name => $"ansight_{action}{(platform is null ? "" : "_" + platform)}_permission";
    protected override string Title => $"{action} {platform ?? "app"} permission";
    protected override string Description =>
        "Grant, revoke, or query native OS permission state on Android devices and iOS Simulators for the enforced session. Permission changes may terminate the app.";
    protected override JsonObject InputSchema => ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
    {
        ["sessionId"] = ToolSchema.String("Session whose native device and application are enforced; captured sessions are allowed after app termination.", nullable: true),
        ["appId"] = ToolSchema.String("Resolve one captured session for this application.", nullable: true),
        ["permission"] = ToolSchema.String(platform switch
        {
            "ios" => "Exact iOS privacy service, for example microphone, photos, or location-always.",
            "android" => "Fully qualified Android permission, for example android.permission.RECORD_AUDIO.",
            _ => "Shared permission: camera, microphone, contacts, calendar, photos, location, locationAlways, mediaLibrary, motion, notifications."
        })
    }, required: ["permission"], additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (arguments?["permission"] is not JsonValue value || !value.TryGetValue<string>(out var permission) || string.IsNullOrWhiteSpace(permission))
            return ToolError("permission must be a non-empty string.");
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var session, out var error))
            return ToolError(error);
        var device = uiInputRouter.ResolveDeviceIdentifier(session!.SessionId, DeviceLifecycleTool.ResolveNativeDeviceIdentifier(session));
        if (device is null) return ToolError("The session does not report a native device identifier.");
        try
        {
            var result = await router.ExecuteAsync(action, permission, platform, device, session.AppId,
                ToolExecutionCancellation.Current).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToNode(result, JsonUtil.Compact)!.AsObject();
            payload["sessionId"] = session.SessionId;
            if (!result.Supported && !result.IsSuccess) payload["code"] = ExecutionCapabilities.ErrorCode;
            return RequestResult.ToolResult(payload, isError: !result.IsSuccess);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ToolError(exception.Message);
        }
    }
}
