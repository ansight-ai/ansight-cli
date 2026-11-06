using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal static class DotNetCaptureToolSchemas
{
    public static Dictionary<string, ToolSchema> ArtifactCaptureProperties(bool includeDuration)
    {
        var properties = new Dictionary<string, ToolSchema>
        {
            ["applicationPath"] = ToolSchema.String("Absolute path to a profiling-ready .app, .apk, or .ipa artifact."),
            ["appId"] = ToolSchema.String("Bundle or package identifier to launch."),
            ["deviceId"] = ToolSchema.String("ADB serial, iOS Simulator UDID, or physical iOS device UDID."),
            ["symbolsPath"] = ToolSchema.String("Optional portable PDB file or directory.", nullable: true)
        };

        if (includeDuration)
        {
            properties["durationSeconds"] = ToolSchema.Integer(
                "Trace duration from 1 through 300 seconds. Defaults to 10.",
                nullable: true);
        }

        return properties;
    }

    public static JsonObject CaptureIdSchema()
        => ToolSchema.Object(
            properties: new Dictionary<string, ToolSchema>
            {
                ["captureId"] = ToolSchema.String("Required .NET trace capture id.")
            },
            required: ["captureId"],
            additionalProperties: false).ToJson();

    public static bool TryReadRequest(
        JsonObject? arguments,
        out DotNetStartupCaptureRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        var applicationPath = arguments?["applicationPath"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            error = "applicationPath is required.";

            return false;
        }

        var appId = arguments?["appId"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(appId))
        {
            error = "appId is required.";

            return false;
        }

        var deviceId = arguments?["deviceId"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            error = "deviceId is required.";

            return false;
        }

        if (!ArgumentReader.TryReadOptionalIntegerArgument(
            arguments,
            "durationSeconds",
            out var durationSeconds,
            out error))
        {
            return false;
        }

        durationSeconds ??= 10;

        if (durationSeconds is < 1 or > 300)
        {
            error = "durationSeconds must be between 1 and 300.";

            return false;
        }

        request = new DotNetStartupCaptureRequest(
            applicationPath,
            appId,
            deviceId,
            TimeSpan.FromSeconds(durationSeconds.Value),
            arguments?["symbolsPath"]?.GetValue<string>()?.Trim());

        return true;
    }
}
