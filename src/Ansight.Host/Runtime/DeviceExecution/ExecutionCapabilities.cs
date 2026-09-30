using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class ExecutionRequirements
{
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> AppTools { get; init; } = [];

    internal void Validate(int version)
    {
        if (version < 2) throw new InvalidDataException("Capability requirements require schemaVersion 2.");
        if (Capabilities is null || AppTools is null || Capabilities.Count > 64 || AppTools.Count > 64
            || Capabilities.Concat(AppTools).Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl)))
            throw new InvalidDataException("Requirements must contain at most 64 non-empty capability and app-tool identifiers each.");
    }
}

internal static class ExecutionCapabilities
{
    public const string ErrorCode = "capability_unavailable";
    public const string SdkSetupUrl = "https://www.ansight.ai/docs/sdk/";
    public const string SdkSetupMessage = "Add the Ansight SDK and the relevant tools to the app, then start an SDK session to enable app-provided inspection and automation. Setup: " + SdkSetupUrl;

    public static async Task<JsonObject> ResolveAsync(IRuntimeState state, IAppToolBridge bridge,
        AppSessionSnapshot session, CancellationToken cancellationToken, DeviceSessionEvidence? evidence = null)
    {
        var device = session.CaptureSource == WorkspaceExecutionModes.Device;
        var sdk = !device && bridge.IsSessionConnected(session.SessionId);
        var live = device ? state.IsDeviceSessionActive(session.SessionId) : sdk;
        var values = session.CustomProperties?["deviceExecution"]?["capabilities"]?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var value in values.Select(pair => pair.Value).OfType<JsonObject>())
            if (!live) { value["available"] = false; value["reason"] = "The live capture has ended."; }
        if (device && evidence?.IsCapturing(session.SessionId) == true)
            foreach (var key in new[] { "files.read", "files.capture" })
                if (session.CustomProperties?["deviceExecution"]?["capabilities"]?[key] is { } fileCapability)
                    values[key] = fileCapability.DeepClone();
        foreach (var capability in new[] { "ui.semantic", "ui.screenshot", "lifecycle", "host.spans" })
            values[capability] = Entry(live, device ? "device" : "sdk", live ? null : "A live session is required.");
        if (device && evidence?.IsCapturing(session.SessionId) == true)
            values["ui.screenshot"] = Entry(true, "device", null);
        foreach (var capability in new[] { "evidence.read", "evidence.telemetry", "evidence.logs", "evidence.artifacts" })
            values[capability] = Entry(true, "session-store", null);
        values["app.tools"] = Entry(sdk, "sdk", sdk ? null : "The app has no connected SDK tool provider. " + (device ? SdkSetupMessage : "Reconnect the SDK session to use app tools."));
        values["logs.native"] = Entry(live && session.LogStreams.Any(stream => stream.Status == SessionLogStreamStatuses.Active
            && stream.StreamId is SessionLogStreamIds.AndroidLogcat or SessionLogStreamIds.AppleUnifiedLog), "native-logs", "Inspect native log streams for availability details.");
        var appTools = new JsonArray();
        if (sdk)
        {
            var catalog = await bridge.QueryToolsAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            if (catalog.Success && catalog.Envelope?.Payload?["tools"] is JsonArray tools)
                foreach (var tool in tools.OfType<JsonObject>().Where(tool => tool["executable"]?.GetValue<bool>() != false && tool["denial"] is null))
                    if (tool["id"] is JsonValue id) appTools.Add(id.DeepClone());
        }
        return new JsonObject
        {
            ["sessionId"] = session.SessionId, ["executionMode"] = device ? "device" : "sdk",
            ["live"] = live, ["appAvailable"] = sdk, ["appTools"] = appTools, ["capabilities"] = values
        };
    }

    public static JsonObject? Missing(JsonObject snapshot, ExecutionRequirements? requirements, bool legacy = false)
    {
        if (legacy && snapshot["appAvailable"]?.GetValue<bool>() != true)
            return Unavailable("app.tools", snapshot, "Legacy modules require SDK mode. Declare schemaVersion 2 and explicit requirements for portable execution.");
        foreach (var capability in requirements?.Capabilities ?? [])
            if (snapshot["capabilities"]?[capability]?["available"]?.GetValue<bool>() != true)
                return Unavailable(capability, snapshot, snapshot["capabilities"]?[capability]?["reason"]?.GetValue<string>() ?? "No provider exposes this capability.");
        var tools = (snapshot["appTools"] as JsonArray ?? []).Select(node => node?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in requirements?.AppTools ?? [])
            if (!tools.Contains(tool)) return Unavailable("app.tool:" + tool, snapshot, "This tool is not available in the current app tool catalog.");
        return null;
    }

    public static JsonObject Unavailable(string capability, JsonObject? snapshot, string reason, bool requiresSdk = false)
    {
        var offerSdk = snapshot?["executionMode"]?.GetValue<string>() == "device"
            && (requiresSdk || capability == "app.tools" || capability.StartsWith("app.tool:", StringComparison.Ordinal)
                || capability is "ui.sdkOverlay" or "ui.afterScreenUpdates");
        var result = new JsonObject
        {
            ["code"] = ErrorCode, ["capability"] = capability, ["executionMode"] = snapshot?["executionMode"]?.DeepClone(),
            ["retryable"] = false,
            ["message"] = $"Capability '{capability}' is unavailable: {reason}" + (offerSdk && !reason.Contains(SdkSetupMessage, StringComparison.Ordinal) ? " " + SdkSetupMessage : "")
        };
        if (offerSdk) result["action"] = new JsonObject { ["label"] = "Add the Ansight SDK", ["url"] = SdkSetupUrl };
        return result;
    }

    public static RequestResult? RejectTool(IRuntimeState state, IAppToolBridge bridge, AppSessionSnapshot session, string name)
    {
        if (session.CaptureSource != WorkspaceExecutionModes.Device) return null;
        if (DeviceExecutionTools.IsSupported(name)) return null;
        return RequestResult.ToolResult(Unavailable("tool:" + name, new JsonObject { ["executionMode"] = "device" },
            "This operation requires an SDK app-tool provider or is unsupported in device mode.", requiresSdk: true), isError: true);
    }

    private static JsonObject Entry(bool available, string provider, string? reason)
        => new() { ["available"] = available, ["provider"] = provider, ["reason"] = available ? null : reason };
}
