using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLocation;

internal sealed class PlayDeviceLocationTool : DeviceLocationOperation
{
    public PlayDeviceLocationTool(OperationServices services) : base(services)
    {
    }

    public override string Name => "ansight_play_device_location";
    protected override string Title => "Play Device Location";
    protected override string Description =>
        "Start GPX or KML location playback on the selected simulator or emulator. Returns after playback starts; replaces the host's active route. Use the CLI location status/stop commands to inspect or stop playback.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildProperties(), required: ["routeContent"], additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        try
        {
            var content = arguments?["routeContent"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content))
                return ToolError("routeContent must contain GPX or KML XML.");
            var sourceFileName = NormalizeOptionalString(arguments?["sourceFileName"]?.GetValue<string>()) ?? "track.gpx";
            var mode = arguments?["mode"]?.GetValue<string>() ?? "recorded";
            if (mode is not ("recorded" or "fixed-speed"))
                return ToolError("mode must be recorded or fixed-speed.");
            var speed = arguments?["speed"]?.Deserialize<double>() ?? 1d;
            var fixedSpeedKph = arguments?["fixedSpeedKph"]?.Deserialize<double>() ?? 30d;
            if (!double.IsFinite(speed) || speed is < 0.1d or > 100d)
                return ToolError("speed must be between 0.1 and 100.");
            if (!double.IsFinite(fixedSpeedKph) || fixedSpeedKph is < 0.5d or > 500d)
                return ToolError("fixedSpeedKph must be between 0.5 and 500.");
            var loop = arguments?["loop"]?.GetValue<bool>() ?? false;
            if (!TryResolveTarget(arguments, out var target, out var targetError))
                return ToolError(targetError);
            if (target.DeviceIdentifier is null)
                return ToolError("Provide a live session or explicit deviceId for location playback.");

            var result = await deviceLocationRouter.PlayLocationAsync(new DeviceLocationPlaybackRequest(
                string.Empty, target.DeviceIdentifier, sourceFileName, content,
                mode == "recorded" ? DeviceLocationPlaybackMode.RecordedTiming : DeviceLocationPlaybackMode.FixedSpeed,
                speed, fixedSpeedKph, loop), ToolExecutionCancellation.Current).ConfigureAwait(false);
            if (!result.IsSuccess)
                return ToolError(result.Message);

            var playback = result.Playback;
            return RequestResult.ToolResult(new JsonObject
            {
                ["isSuccess"] = true,
                ["message"] = result.Message,
                ["runId"] = playback.RunId,
                ["sessionId"] = target.SessionId,
                ["deviceId"] = playback.DeviceIdentifier,
                ["platform"] = playback.Platform,
                ["sourceFileName"] = playback.SourceFileName,
                ["pointCount"] = playback.PointCount,
                ["mode"] = playback.Mode == DeviceLocationPlaybackMode.RecordedTiming ? "recorded" : "fixed-speed",
                ["speed"] = playback.PlaybackSpeedMultiplier,
                ["fixedSpeedKph"] = playback.FixedSpeedKph,
                ["loop"] = playback.Loop
            }, isError: false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            return ToolError(exception.Message);
        }
    }

    private static Dictionary<string, ToolSchema> BuildProperties()
    {
        var properties = BuildTargetProperties();
        properties["routeContent"] = ToolSchema.String("GPX or KML XML content, not a filesystem path.");
        properties["sourceFileName"] = ToolSchema.String("Display filename. Defaults to track.gpx.");
        properties["mode"] = ToolSchema.String("recorded (default) or fixed-speed. Missing track timestamps fall back to fixed speed.");
        properties["speed"] = ToolSchema.Number("Recorded-timing speed multiplier, 0.1–100. Defaults to 1; does not change fixed speed.");
        properties["fixedSpeedKph"] = ToolSchema.Number("Fixed or fallback speed in km/h, 0.5–500. Defaults to 30.");
        properties["loop"] = ToolSchema.Boolean("Repeat until stopped or replaced. Defaults to false.");
        return properties;
    }
}
