using System.Text.Json.Nodes;
using System.Text.Json;
using Ansight.MacSimulatorHid;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class LiveStylusTool(OperationServices services) : RemoteAppOperation(services)
{
    public override string Name => "ansight_stylus_ui";
    protected override string Title => "Play Pencil input";
    protected override string Description => "Experimental iOS Simulator stylus input with timed normalized position, pressure, UIKit orientation and proximity. Delivery is not proof of UIKit fidelity. Does not substitute finger input.";
    protected override JsonObject InputSchema => ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
    {
        ["sessionId"] = ToolSchema.String("Live session to target.", nullable: true),
        ["appId"] = ToolSchema.String("App with exactly one live session.", nullable: true),
        ["mode"] = ToolSchema.String("hid (default), hid+sdk (native UIKit), or sdk (registered framework surface, no HID). Hybrid mode requires the app to opt into Simulator pen input and allow the SDK write-policy tool ui.simulate_pen. Overrides app-visible Pencil properties; hover is dispatched in-process.", enumValues: ["hid", "hid+sdk", "sdk"], nullable: true),
        ["target"] = ToolSchema.String("Required for sdk: registered framework surface ID. Required for hid+sdk: unique native accessibility identifier of the drawing view. All sample points must hit that view or a descendant.", nullable: true),
        ["hoverRecognizer"] = ToolSchema.String("Explicit name of one idle Pencil hover recognizer directly attached to target. Required for hybrid hover samples; ancestors and other recognizers are never driven.", nullable: true),
        ["file"] = ToolSchema.String("Local ansight.pen-input/v1 JSON file. In tasks, relative to the repository root. Supply file or samples, never both.", nullable: true),
        ["samples"] = ToolSchema.Array(ToolSchema.Object(properties: new Dictionary<string, ToolSchema>
        {
            ["offsetMs"] = ToolSchema.Integer("Offset from sequence start, 0..30000, increasing."),
            ["phase"] = ToolSchema.String("Stylus transition.", enumValues: ["down", "move", "up", "cancel", "hoverEnter", "hoverMove", "hoverExit"]),
            ["x"] = ToolSchema.Number("Viewport-normalized X, 0..1."),
            ["y"] = ToolSchema.Number("Viewport-normalized Y, 0..1."),
            ["pressure"] = ToolSchema.Number("Normalized pressure, 0..1.", nullable: true),
            ["altitudeRadians"] = ToolSchema.Number("UIKit altitude, 0..pi/2.", nullable: true),
            ["azimuthRadians"] = ToolSchema.Number("UIKit azimuth in radians.", nullable: true),
            ["rollRadians"] = ToolSchema.Number("Barrel roll in radians; experimental.", nullable: true),
            ["distance"] = ToolSchema.Number("Normalized hover distance, 0..1; experimental.", nullable: true)
        }, required: ["offsetMs", "phase", "x", "y"], additionalProperties: false), description: "2..2000 samples forming complete strokes/hover sequences.")
    }, additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var error)) return ToolError(error);
        IReadOnlyList<TimedStylusSample> samples;
        try
        {
            var file = arguments?["file"]?.GetValue<string>();
            if ((file is null) == (arguments?["samples"] is null))
                return ToolError("Supply exactly one of file or samples.");
            var document = file is null ? arguments! : await PenInputFile.ReadAsync(file, ToolExecutionCancellation.Current);
            samples = ParseSamples(document["samples"] as JsonArray);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or OverflowException or JsonException or IOException or UnauthorizedAccessException)
        { return ToolError(exception.Message); }
        var mode = arguments?["mode"]?.GetValue<string>() ?? "hid";
        if (mode is not ("hid" or "hid+sdk" or "sdk")) return ToolError("mode must be hid, hid+sdk or sdk.");
        var target = arguments?["target"]?.GetValue<string>();
        var hoverRecognizer = arguments?["hoverRecognizer"]?.GetValue<string>();
        if (mode == "sdk" && hoverRecognizer is not null)
            return ToolError("SDK framework playback uses the registered surface's handlers; omit hoverRecognizer.");
        if (mode != "hid" && string.IsNullOrWhiteSpace(target))
            return ToolError("SDK playback requires target: a native drawing view identifier or registered framework surface ID.");
        if (mode == "hid+sdk" && samples.Any(item => item.Sample.Phase is MacSimulatorStylusPhase.HoverEnter or MacSimulatorStylusPhase.HoverMove)
            && string.IsNullOrWhiteSpace(hoverRecognizer))
            return ToolError("Hybrid hover requires hoverRecognizer: the name of one recognizer on the target drawing view.");
        var deviceId = uiInputRouter.ResolveDeviceIdentifier(snapshot!.SessionId, DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot));
        if (deviceId is null) return ToolError("The session has no native device identifier.");
        var token = ToolExecutionCancellation.Current;
        var preflight = await uiInputRouter.PrepareForInputAsync(new UiInputPreflightRequest(snapshot.SessionId, deviceId, snapshot.AppId), token);
        if (!preflight.IsSuccess) return ToolError(preflight.Message);
        var runId = Guid.NewGuid().ToString();
        var hybrid = mode == "hid+sdk";
        var framework = mode == "sdk";
        async Task<JsonObject> CallSdk(JsonObject input, CancellationToken cancellation)
        {
            input["runId"] = runId;
            var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(snapshot.SessionId,
                framework ? "ui.simulate_pen_framework" : "ui.simulate_pen",
                framework ? new JsonObject { ["command"] = input.ToJsonString() } : input, null, cancellation,
                new AppToolBridgeRequestContext("host-ui", Name, correlationId));
            if (!response.Success || response.Envelope?.Type != ToolProtocolMessageTypes.ResultType
                || response.Envelope.Payload?["success"]?.GetValue<bool>() != true)
                throw new InvalidOperationException(response.Envelope?.Payload?["error"]?["message"]?.GetValue<string>()
                    ?? response.Envelope?.Payload?["message"]?.GetValue<string>()
                    ?? response.Message ?? "Simulator SDK pen simulation failed.");
            return response.Envelope.Payload?["result"] as JsonObject ?? new JsonObject();
        }
        UiInputResult result;
        var prepared = false;
        try
        {
            if (hybrid || framework)
            {
                var preparationArguments = new JsonObject
                {
                    ["action"] = "prepare", ["target"] = target,
                    ["samples"] = new JsonArray(samples.Select(item => (JsonNode)SampleJson(item.Sample)).ToArray())
                };
                if (hoverRecognizer is not null) preparationArguments["hoverRecognizer"] = hoverRecognizer;
                var preparation = await CallSdk(preparationArguments, token);
                prepared = true;
                if (preparation["protocolVersion"]?.GetValue<int>() != (framework ? 1 : 2))
                    throw new InvalidOperationException("Update the optional Pencil package: incompatible scoped playback protocol.");
            }
            if (framework)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach (var timed in samples)
                {
                    var delay = TimeSpan.FromMilliseconds(timed.OffsetMilliseconds) - clock.Elapsed;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    await CallSdk(new JsonObject { ["action"] = "sample", ["sample"] = SampleJson(timed.Sample) }, token);
                }
                result = new UiInputResult(true, "sdk-framework", "Delivered Pencil samples to the registered framework surface.");
            }
            else
            {
            var request = new UiStylusRequest(snapshot.SessionId, deviceId, samples)
            {
                BeforeSample = !hybrid ? null : async (sample, cancellation) =>
                {
                    var hover = sample.Phase is MacSimulatorStylusPhase.HoverEnter or MacSimulatorStylusPhase.HoverMove or MacSimulatorStylusPhase.HoverExit;
                    await CallSdk(new JsonObject { ["action"] = hover ? "hover" : "arm", ["sample"] = SampleJson(sample) }, cancellation);
                }
            };
            result = await uiInputRouter.StylusAsync(request, token);
            // Wait for queued UIKit delivery before clearing getter overrides.
            if (hybrid) await Task.Delay(200, token);
            }
        }
        catch (InvalidOperationException exception) { return ToolError(exception.Message); }
        finally
        {
            if (prepared)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await CallSdk(new JsonObject { ["action"] = "finish" }, cleanup.Token);
            }
        }
        return RequestResult.ToolResult(new JsonObject
        {
            ["sessionId"] = snapshot.SessionId, ["performed"] = result.IsSuccess,
            ["mode"] = mode,
            ["fidelityWarning"] = framework ? "Synthetic framework events delivered only to the registered surface. No HID or UIKit interception. This does not exercise hardware or native gesture arbitration." : hybrid ? "App-visible properties were simulated by the SDK, and hover dispatched in-process. This verifies app behavior, not Pencil hardware fidelity. Captured samples identify their simulation source." : "Experimental Simulator transport. Pencil contact/position is verified on iOS 26.4; pressure, altitude, azimuth, roll and hover are not verified. Inspect SDK-captured fields rather than assuming requested values arrived.",
            ["sampleCount"] = samples.Count, ["inputBackend"] = result.Backend, ["message"] = result.Message
        }, isError: !result.IsSuccess);
    }

    private static JsonObject SampleJson(MacSimulatorStylusSample sample)
    {
        var result = new JsonObject()
    {
        ["phase"] = sample.Phase switch
        {
            MacSimulatorStylusPhase.HoverEnter => "hoverEnter", MacSimulatorStylusPhase.HoverMove => "hoverMove",
            MacSimulatorStylusPhase.HoverExit => "hoverExit", _ => sample.Phase.ToString().ToLowerInvariant()
        },
        ["x"] = sample.NormalizedX, ["y"] = sample.NormalizedY, ["pressure"] = sample.Pressure,
        ["altitudeRadians"] = sample.AltitudeRadians, ["azimuthRadians"] = sample.AzimuthRadians,
        ["rollRadians"] = sample.RollRadians, ["distance"] = sample.Distance
        };
        if (sample.EventTimestampSeconds is { } timestamp) result["eventTimestamp"] = timestamp;
        return result;
    }

    internal static IReadOnlyList<TimedStylusSample> ParseSamples(JsonArray? values)
    {
        if (values is null || values.Count < 2 || values.Count > 2000)
            throw new ArgumentException("Supply 2..2000 stylus samples.");
        var samples = new List<TimedStylusSample>();
        var touching = false;
        var hovering = false;
        var previous = -1;
        foreach (var node in values)
        {
            if (node is not JsonObject item) throw new ArgumentException("Each stylus sample must be an object.");
            if (item.Any(property => property.Key is not ("offsetMs" or "phase" or "x" or "y" or "pressure" or "altitudeRadians" or "azimuthRadians" or "rollRadians" or "distance")))
                throw new ArgumentException("Unknown stylus sample property.");
            var offset = item["offsetMs"]?.GetValue<int>() ?? -1;
            var phase = item["phase"]?.GetValue<string>() switch
            {
                "down" => MacSimulatorStylusPhase.Down, "move" => MacSimulatorStylusPhase.Move,
                "up" => MacSimulatorStylusPhase.Up, "cancel" => MacSimulatorStylusPhase.Cancel,
                "hoverEnter" => MacSimulatorStylusPhase.HoverEnter, "hoverMove" => MacSimulatorStylusPhase.HoverMove,
                "hoverExit" => MacSimulatorStylusPhase.HoverExit,
                _ => throw new ArgumentException("Unknown stylus phase.")
            };
            if (offset < 0 || offset > 30000 || offset <= previous)
                throw new ArgumentException("Stylus offsets must increase strictly within 0..30000 milliseconds.");
            if (phase == MacSimulatorStylusPhase.Down && touching
                || phase is MacSimulatorStylusPhase.Move or MacSimulatorStylusPhase.Up or MacSimulatorStylusPhase.Cancel && !touching
                || phase == MacSimulatorStylusPhase.HoverEnter && (touching || hovering)
                || phase is MacSimulatorStylusPhase.HoverMove or MacSimulatorStylusPhase.HoverExit && (!hovering || touching))
                throw new ArgumentException("Invalid stylus contact/proximity transition.");
            if (phase == MacSimulatorStylusPhase.Down) { touching = true; hovering = false; }
            if (phase == MacSimulatorStylusPhase.Up) { touching = false; hovering = true; }
            if (phase == MacSimulatorStylusPhase.HoverEnter) hovering = true;
            if (phase is MacSimulatorStylusPhase.Cancel or MacSimulatorStylusPhase.HoverExit) { touching = false; hovering = false; }
            var sample = new MacSimulatorStylusSample(phase,
                Read(item, "x", double.NaN), Read(item, "y", double.NaN),
                Read(item, "pressure", 0), Read(item, "altitudeRadians", Math.PI / 2),
                Read(item, "azimuthRadians", 0), Read(item, "rollRadians", 0), Read(item, "distance", 0));
            sample.Validate();
            if (!touching && sample.Pressure != 0) throw new ArgumentException("Non-contact samples must have zero pressure.");
            samples.Add(new TimedStylusSample(offset, sample)); previous = offset;
        }
        if (touching) throw new ArgumentException("End the stylus contact with up or cancel.");
        return samples;
    }
    private static double Read(JsonObject item, string name, double fallback) => item[name]?.Deserialize<double>() ?? fallback;
}
