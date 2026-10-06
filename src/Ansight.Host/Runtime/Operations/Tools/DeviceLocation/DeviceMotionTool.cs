using System.Text.Json.Nodes;
using System.Text.Json;
using Ansight.Adb;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Devices.Motion;
using Ansight.MacSimulatorHid;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DeviceLocation;

internal sealed class DeviceMotionTool : DeviceHostTargetOperation
{
    private readonly bool shake;

    public DeviceMotionTool(OperationServices services, bool shake) : base(services)
    {
        this.shake = shake;
    }

    public override string Name => shake ? "ansight_shake_device" : "ansight_play_accelerometer";
    protected override string Title => shake ? "Shake Device" : "Play Accelerometer Samples";
    protected override string Description =>
        shake
            ? "Send a UIKit shake gesture to an iOS Simulator or acceleration pulses to an Android Emulator."
            : "Deliver bounded acceleration samples to an Android Emulator, then restore its previous sensor value.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildProperties(),
        required: shake ? [] : ["samples"],
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        try
        {
            if (!TryResolveTarget(arguments, out var target, out var targetError))
                return ToolError(targetError);
            if (target.DeviceIdentifier is null)
                return ToolError("A live session or explicit virtual-device deviceId is required for motion simulation.");

            if (shake && !target.DeviceIdentifier.StartsWith("emulator-", StringComparison.Ordinal))
            {
                if (arguments?["intensity"] is not null || arguments?["repetitions"] is not null
                    || arguments?["intervalMs"] is not null)
                    return ToolError("iOS Simulator shake is a fixed UIKit gesture; intensity, repetitions, and intervalMs apply only to Android Emulator.");

                await deviceMotionRouter.ShakeIosSimulatorAsync(target.DeviceIdentifier,
                        ToolExecutionCancellation.Current)
                    .ConfigureAwait(false);
                PublishMotionInjection(target, "shake", "core-simulator-darwin-notification", 0);
                return RequestResult.ToolResult(new JsonObject
                {
                    ["isSuccess"] = true,
                    ["operation"] = "shake",
                    ["targetSource"] = target.Source,
                    ["sessionId"] = target.SessionId,
                    ["deviceId"] = target.DeviceIdentifier,
                    ["platform"] = "ios",
                    ["backend"] = "core-simulator-darwin-notification",
                    ["sampleCount"] = 0,
                    ["gestureCount"] = 1,
                    ["durationMs"] = 0,
                    ["message"] = "UIKit shake gesture posted. App response must be asserted separately."
                }, isError: false);
            }

            var samples = shake ? BuildShake(arguments) : ReadSamples(arguments);
            AndroidEmulatorMotionPlayer.ValidateDeviceSerial(target.DeviceIdentifier);

            await deviceMotionRouter.PlayAsync(target.DeviceIdentifier, samples, ToolExecutionCancellation.Current)
                .ConfigureAwait(false);
            PublishMotionInjection(target, shake ? "shake" : "playAccelerometer", "adb-emulator-console", samples.Count);
            return RequestResult.ToolResult(new JsonObject
            {
                ["isSuccess"] = true,
                ["operation"] = shake ? "shake" : "playAccelerometer",
                ["targetSource"] = target.Source,
                ["sessionId"] = target.SessionId,
                ["deviceId"] = target.DeviceIdentifier,
                ["platform"] = "android",
                ["backend"] = "adb-emulator-console",
                ["sampleCount"] = samples.Count,
                ["durationMs"] = samples.Sum(static sample => sample.HoldMilliseconds),
                ["message"] = "Acceleration samples delivered and prior sensor value restored. App response must be asserted separately."
            }, isError: false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                           or PlatformNotSupportedException or JsonException or IOException
                                           or DllNotFoundException or MacSimulatorHidException)
        {
            return ToolError(exception.Message);
        }
    }

    private void PublishMotionInjection(DeviceHostTarget target, string operation, string backend, int sampleCount)
    {
        if (target.SessionId is null) return;
        HostSessionEvents.Publish(runtimeState, target.SessionId, "motion.injected." + operation, "Motion",
            JsonSerializer.Serialize(new
            {
                source = "host",
                deviceId = target.DeviceIdentifier,
                backend,
                sampleCount,
                operation
            }));
    }

    private Dictionary<string, ToolSchema> BuildProperties()
    {
        var properties = BuildTargetProperties();
        if (shake)
        {
            properties["intensity"] = ToolSchema.Number("Peak X acceleration in m/s², 5–50. Default 20.");
            properties["repetitions"] = ToolSchema.Integer("Positive/negative pulse pairs, 1–8. Default 3.");
            properties["intervalMs"] = ToolSchema.Integer("Hold each pulse for 40–500 ms. Default 80.");
        }
        else
        {
            properties["samples"] = ToolSchema.Array(ToolSchema.Object(
                properties: new Dictionary<string, ToolSchema>
                {
                    ["x"] = ToolSchema.Number("X acceleration in m/s², -100–100."),
                    ["y"] = ToolSchema.Number("Y acceleration in m/s², -100–100."),
                    ["z"] = ToolSchema.Number("Z acceleration in m/s², -100–100."),
                    ["holdMs"] = ToolSchema.Integer("Hold for 10–1000 ms.")
                },
                required: ["x", "y", "z", "holdMs"],
                additionalProperties: false),
                description: "1–100 samples, at most 10 seconds total.");
        }
        return properties;
    }

    private static IReadOnlyList<DeviceMotionSample> BuildShake(JsonObject? arguments)
    {
        var intensity = arguments?["intensity"]?.Deserialize<double>() ?? 20d;
        var repetitions = arguments?["repetitions"]?.Deserialize<int>() ?? 3;
        var interval = arguments?["intervalMs"]?.Deserialize<int>() ?? 80;
        if (!double.IsFinite(intensity) || intensity is < 5 or > 50
            || repetitions is < 1 or > 8 || interval is < 40 or > 500)
            throw new ArgumentOutOfRangeException(nameof(arguments),
                "Shake requires intensity 5–50 m/s², repetitions 1–8, and intervalMs 40–500.");

        var samples = new List<DeviceMotionSample>(repetitions * 2);
        for (var index = 0; index < repetitions; index++)
        {
            samples.Add(new DeviceMotionSample(new EmulatorAcceleration(intensity, 0, 0), interval));
            samples.Add(new DeviceMotionSample(new EmulatorAcceleration(-intensity, 0, 0), interval));
        }
        return samples;
    }

    private static IReadOnlyList<DeviceMotionSample> ReadSamples(JsonObject? arguments)
    {
        if (arguments?["samples"] is not JsonArray values || values.Count is < 1 or > 100)
            throw new ArgumentException("samples must contain 1–100 accelerometer samples.");
        var samples = new List<DeviceMotionSample>(values.Count);
        foreach (var value in values)
        {
            if (value is not JsonObject sample)
                throw new ArgumentException("Each accelerometer sample must be an object.");
            var x = ReadAxis(sample, "x");
            var y = ReadAxis(sample, "y");
            var z = ReadAxis(sample, "z");
            var hold = sample["holdMs"]?.Deserialize<int>()
                       ?? throw new ArgumentException("Each sample requires holdMs.");
            if (hold is < 10 or > 1000)
                throw new ArgumentOutOfRangeException(nameof(arguments), "holdMs must be 10–1000.");
            samples.Add(new DeviceMotionSample(new EmulatorAcceleration(x, y, z), hold));
        }
        if (samples.Sum(static sample => sample.HoldMilliseconds) > 10_000)
            throw new ArgumentOutOfRangeException(nameof(arguments), "The sequence must last at most 10 seconds.");
        return samples;
    }

    private static double ReadAxis(JsonObject sample, string axis)
    {
        var value = sample[axis]?.Deserialize<double>()
                    ?? throw new ArgumentException($"Each sample requires {axis}.");
        if (!double.IsFinite(value) || value is < -100 or > 100)
            throw new ArgumentOutOfRangeException(axis, "Acceleration must be between -100 and 100 m/s².");
        return value;
    }
}
