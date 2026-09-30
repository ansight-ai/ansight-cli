using System.Text.Json.Nodes;
using Ansight.Tools;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class TakeScreenshotTool : RemoteAppOperation
{
    private readonly DeviceSessionEvidence? deviceEvidence;
    public TakeScreenshotTool(OperationServices services)
        : base(services)
    {
        deviceEvidence = services.DeviceEvidence;
    }

    public override string Name => "ansight_take_screenshot";

    protected override string Title => "Take Screenshot";

    protected override string Description => "Capture a live screenshot from a connected app through its ui.get_screenshot remote tool.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["arguments"] = ToolSchema.Object(
                description: "Optional arguments forwarded to ui.get_screenshot. Direct screenshot arguments on this operation override these values.",
                properties: new Dictionary<string, ToolSchema>(),
                additionalProperties: true,
                nullable: true),
            ["format"] = ToolSchema.String(
                "Screenshot format. Defaults to jpeg.",
                nullable: true,
                enumValues: ["jpeg", "png"]),
            ["quality"] = ToolSchema.Integer("JPEG quality. Defaults to 80.", nullable: true),
            ["maxWidth"] = ToolSchema.Integer("Maximum output width in pixels. Defaults to 1280.", nullable: true),
            ["afterScreenUpdates"] = ToolSchema.Boolean("Wait for pending screen updates before capture. Defaults to false.", nullable: true),
            ["annotateNodeIds"] = ToolSchema.Boolean("Ask the app to overlay visual node ids when supported. Defaults to false.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError)
            && !(arguments?["sessionId"]?.GetValue<string>() is { } exactId
                && deviceEvidence?.IsCapturing(exactId) == true
                && runtimeState.TryGetSessionSnapshot(exactId, out snapshot)))
        {
            return ToolError(resolutionError);
        }

        if (!TryBuildScreenshotArguments(arguments, out var remoteArguments, out var errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid screenshot arguments.");
        }

        if (snapshot!.CaptureSource == WorkspaceExecutionModes.Device)
        {
            if (remoteArguments["annotateNodeIds"]?.GetValue<bool>() == true)
                return RequestResult.ToolResult(ExecutionCapabilities.Unavailable("ui.sdkOverlay",
                    new JsonObject { ["executionMode"] = "device" }, "Node overlays require SDK app tools."), isError: true);
            if (remoteArguments["afterScreenUpdates"]?.GetValue<bool>() == true)
                return RequestResult.ToolResult(ExecutionCapabilities.Unavailable("ui.afterScreenUpdates",
                    new JsonObject { ["executionMode"] = "device" }, "An app render fence requires SDK app tools."), isError: true);
            var evidence = await LiveUiActionEvidence.CaptureScreenshotAsync(
                runtimeState, applicationPaths, appToolBridge, snapshot, Name, "current", correlationId,
                ToolExecutionCancellation.Current, externalScreenshots: externalScreenshots).ConfigureAwait(false);
            if (evidence is null) return ToolError("A fresh device screenshot is unavailable.");
            evidence = await EncodeDeviceScreenshotAsync(snapshot, evidence, remoteArguments).ConfigureAwait(false);
            var screenshot = new JsonObject
            {
                ["captureSource"] = "device", ["artifactPath"] = evidence.ArtifactPath,
                ["frameId"] = evidence.Frame.FrameId, ["format"] = evidence.Frame.Format,
                ["width"] = evidence.Frame.Width, ["height"] = evidence.Frame.Height,
                ["capturedAtUtc"] = evidence.Frame.CapturedAtUtc
            };
            return RequestResult.ToolResult(new JsonObject
            {
                ["sessionId"] = snapshot.SessionId, ["appId"] = snapshot.AppId,
                ["toolId"] = RemoteAppToolIds.UiGetScreenshot, ["captureSource"] = "device",
                ["responseType"] = "tool.result",
                ["payload"] = new JsonObject
                {
                    ["toolId"] = RemoteAppToolIds.UiGetScreenshot, ["success"] = true,
                    ["result"] = screenshot
                }
            }, isError: false);
        }

        return await BuildCallAppToolResultAsync(
            new JsonObject
            {
                ["sessionId"] = snapshot!.SessionId,
                ["appId"] = snapshot.AppId,
                ["toolId"] = RemoteAppToolIds.UiGetScreenshot,
                ["arguments"] = remoteArguments
            },
            correlationId,
            Name);
    }

    private async Task<PersistedScreenshotEvidence> EncodeDeviceScreenshotAsync(
        AppSessionSnapshot session, PersistedScreenshotEvidence evidence, JsonObject arguments)
    {
        var format = arguments["format"]!.GetValue<string>();
        var quality = arguments["quality"]!.GetValue<int>();
        var maximumWidth = arguments["maxWidth"]!.GetValue<int>();
        using var source = SKBitmap.Decode(await File.ReadAllBytesAsync(evidence.ArtifactPath, ToolExecutionCancellation.Current));
        if (source is null) throw new IOException("The captured device screenshot could not be decoded.");
        var width = Math.Min(maximumWidth, source.Width);
        var height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
        using var resized = width == source.Width ? source.Copy()
            : source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(resized ?? throw new IOException("The device screenshot could not be resized."));
        using var encoded = image.Encode(format == "png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, quality);
        var bytes = encoded.ToArray();
        var frame = await runtimeState.AddSessionEvidenceImageAsync(session.SessionId, evidence.Frame.CapturedAtUtc,
            format, width, height, quality, bytes).ConfigureAwait(false)
            ?? throw new IOException("The device screenshot could not be persisted.");
        return new PersistedScreenshotEvidence(frame, Convert.ToHexStringLower(SHA256.HashData(bytes)),
            SessionFileLocator.ResolveScreenshotPath(applicationPaths, session, frame));
    }

    private static bool TryBuildScreenshotArguments(
        JsonObject? arguments,
        out JsonObject remoteArguments,
        out string? errorMessage)
    {
        remoteArguments = new JsonObject
        {
            ["format"] = "jpeg",
            ["quality"] = SessionEvidenceDefaults.DefaultScreenshotQuality,
            ["maxWidth"] = SessionEvidenceDefaults.DefaultScreenshotMaxWidth,
            ["afterScreenUpdates"] = false
        };
        errorMessage = null;
        if (arguments?["arguments"] is JsonObject suppliedArguments)
        {
            foreach (var pair in suppliedArguments)
            {
                remoteArguments[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var format = NormalizeOptionalString(arguments?["format"]?.GetValue<string>());
        if (format is not null)
        {
            remoteArguments["format"] = format;
        }

        if (remoteArguments["format"] is not JsonValue formatValue || !formatValue.TryGetValue<string>(out var requestedFormat)
            || requestedFormat is not ("jpeg" or "png"))
        {
            errorMessage = "format must be jpeg or png.";
            return false;
        }
        return TryApplyOptionalInteger(arguments, remoteArguments, "quality", 1, 100, out errorMessage)
               && TryApplyOptionalInteger(arguments, remoteArguments, "maxWidth", 1, int.MaxValue, out errorMessage)
               && TryApplyOptionalBoolean(arguments, remoteArguments, "afterScreenUpdates", out errorMessage)
               && TryApplyOptionalBoolean(arguments, remoteArguments, "annotateNodeIds", out errorMessage)
               && TryApplyOptionalInteger(remoteArguments, remoteArguments, "quality", 1, 100, out errorMessage)
               && TryApplyOptionalInteger(remoteArguments, remoteArguments, "maxWidth", 1, int.MaxValue, out errorMessage)
               && TryApplyOptionalBoolean(remoteArguments, remoteArguments, "afterScreenUpdates", out errorMessage)
               && TryApplyOptionalBoolean(remoteArguments, remoteArguments, "annotateNodeIds", out errorMessage);
    }

    private static bool TryApplyOptionalBoolean(
        JsonObject? arguments,
        JsonObject target,
        string propertyName,
        out string? errorMessage)
    {
        if (!TouchReviewArgumentReader.TryReadBooleanArgument(arguments, propertyName, defaultValue: false, out var value, out errorMessage))
        {
            return false;
        }

        if (arguments?[propertyName] is not null)
        {
            target[propertyName] = value;
        }

        return true;
    }

    private static bool TryApplyOptionalInteger(
        JsonObject? arguments,
        JsonObject target,
        string propertyName,
        int minimum,
        int maximum,
        out string? errorMessage)
    {
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && jsonValue.TryGetValue<int>(out var value)
            && value >= minimum
            && value <= maximum)
        {
            target[propertyName] = value;
            return true;
        }

        errorMessage = $"{propertyName} must be an integer between {minimum} and {maximum}.";
        return false;
    }
}
