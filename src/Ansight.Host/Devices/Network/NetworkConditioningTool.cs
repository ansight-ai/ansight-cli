using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.DeviceLocation;
using Ansight.Tools;

namespace Ansight.Host.Devices.Network;

internal sealed class NetworkConditioningTool : DeviceHostTargetOperation
{
    private readonly NetworkConditioningManager manager;
    private readonly string action;
    public NetworkConditioningTool(OperationServices services, string action) : base(services)
    {
        manager = services.NetworkConditioning;
        this.action = action;
    }
    public override string Name => "ansight_network_conditioning_" + action;
    protected override string Title => "Network conditioning: " + action;
    protected override string Description => "Bounded host-side TCP conditioning for one simulator app or Android emulator; preserves Ansight control traffic. No TLS decryption or radio simulation.";
    protected override JsonObject InputSchema => ToolSchema.Object(properties: Properties(), required: [], additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        try
        {
            var token = ToolExecutionCancellation.Current;
            var leaseId = arguments?["leaseId"]?.GetValue<string>();
            JsonObject result;
            if (action == "reset")
            {
                await manager.ResetAsync(leaseId ?? throw new ArgumentException("leaseId is required.")).ConfigureAwait(false);
                result = new JsonObject { ["leaseId"] = leaseId, ["status"] = "stopped" };
            }
            else if (action == "get")
                result = await manager.GetAsync(leaseId ?? throw new ArgumentException("leaseId is required."), token).ConfigureAwait(false);
            else
            {
                if (!TryResolveTarget(arguments, out var resolved, out var error)) return ToolError(error);
                var appId = resolved.AppId ?? arguments?["appId"]?.GetValue<string>();
                if (resolved.DeviceIdentifier is null || string.IsNullOrWhiteSpace(appId))
                    return ToolError("Provide a live session, or an exact virtual deviceId and appId.");
                var target = new NetworkConditionTarget(resolved.DeviceIdentifier, appId, resolved.SessionId);
                if (action == "capabilities") result = await manager.CapabilitiesAsync(target, token).ConfigureAwait(false);
                else
                {
                    var steps = new List<NetworkConditionStep>();
                    var duration = arguments?["durationMs"]?.GetValue<int>() ?? 120_000;
                    var profile = new NetworkConditionProfile();
                    if (action == "scenario")
                    {
                        var array = arguments?["steps"] as JsonArray ?? throw new ArgumentException("steps is required.");
                        if (array.Count is < 1 or > 50) throw new ArgumentException("Supply 1–50 scenario steps.");
                        foreach (var step in array)
                        {
                            var hold = step?["durationMs"]?.GetValue<int>() ?? 0;
                            if (hold is < 1000 or > 300_000) throw new ArgumentException("Each step durationMs must be 1000–300000.");
                            steps.Add(new(NetworkConditionProfile.Read(step?["profile"]), hold));
                        }
                        duration = steps.Sum(step => step.DurationMs);
                        if (leaseId is not null) throw new ArgumentException("Scenarios require a new lease.");
                    }
                    else profile = NetworkConditionProfile.Read(arguments?["profile"]);
                    var ports = arguments?["destinationPorts"]?.Deserialize<int[]>() ?? [];
                    result = await manager.SetAsync(target, profile, steps, ports, duration, leaseId, token).ConfigureAwait(false);
                }
            }
            return RequestResult.ToolResult(result, isError: false);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or IOException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        { return ToolError(error.Message); }
    }

    private Dictionary<string, ToolSchema> Properties()
    {
        var properties = BuildTargetProperties();
        if (action is "set" or "get" or "reset") properties["leaseId"] = ToolSchema.String("Lease identifier returned by set/scenario; required for update, get and reset.");
        if (action is "set" or "scenario")
        {
            properties["destinationPorts"] = ToolSchema.Array(ToolSchema.Integer("Destination port."), "Optional TCP destination allowlist; control ports always bypass.");
            if (action == "set")
            {
                properties["durationMs"] = ToolSchema.Integer("Initial hard lifetime, 1000–300000ms; default 120000; updates do not extend it.");
                properties["profile"] = ProfileSchema();
            }
            else properties["steps"] = ToolSchema.Array(ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
            {
                ["profile"] = ProfileSchema(), ["durationMs"] = ToolSchema.Integer("Step duration, at least 1000ms.")
            }, required: ["profile", "durationMs"], additionalProperties: false), "1–50 ordered steps, at most 300000ms total. Starts asynchronously so the test can interact with the app.");
        }
        return properties;
    }

    private static ToolSchema ProfileSchema() => ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
    {
        ["uploadKbps"] = ToolSchema.Integer("Aggregate upstream rate, 0–1000000 kilobits/s; 0 unlimited."),
        ["downloadKbps"] = ToolSchema.Integer("Aggregate downstream rate, 0–1000000 kilobits/s; 0 unlimited."),
        ["latencyMs"] = ToolSchema.Integer("First payload delay per direction/connection, 0–10000ms."),
        ["jitterMs"] = ToolSchema.Integer("Uniform variation around first payload delay, 0–10000ms."),
        ["seed"] = ToolSchema.Integer("Jitter random seed; connection ordering still affects reproducibility."),
        ["stall"] = ToolSchema.Boolean("Hold TCP payloads until profile changes or expiry."),
        ["blocked"] = ToolSchema.Boolean("Close TCP connections. Does not change OS connectivity.")
    }, required: [], additionalProperties: false);
}
