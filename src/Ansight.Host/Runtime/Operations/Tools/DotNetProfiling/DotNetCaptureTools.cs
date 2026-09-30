using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetCaptureRequirementsTool : DotNetOperation
{
    public DotNetGetCaptureRequirementsTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_capture_requirements";

    protected override string Title => "Get .NET Capture Requirements";

    protected override string Description => "Check the host-owned .NET artifact capture tools for Android and iOS targets.";

    protected override JsonObject InputSchema => ToolSchema.Object(additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var toolchain = await Services.Profiling.GetToolchainAsync();
        var missingForAndroid = new JsonArray();
        var missingForIosSimulator = new JsonArray();
        var missingForIosDevice = new JsonArray();
        AddMissing(missingForAndroid, toolchain.DotNetTracePath, "dotnet-trace");
        AddMissing(missingForAndroid, toolchain.DotNetDsRouterPath, "dotnet-dsrouter");
        AddMissing(missingForAndroid, toolchain.AdbPath, "adb");
        AddMissing(missingForIosSimulator, toolchain.DotNetTracePath, "dotnet-trace");
        AddMissing(missingForIosSimulator, toolchain.DotNetDsRouterPath, "dotnet-dsrouter");
        AddMissing(missingForIosSimulator, toolchain.XcrunPath, "Xcode command-line tools");
        AddMissing(missingForIosSimulator, toolchain.SimCtlPath, "Xcode SimCtl");
        AddMissing(missingForIosDevice, toolchain.DotNetTracePath, "dotnet-trace");
        AddMissing(missingForIosDevice, toolchain.DotNetDsRouterPath, "dotnet-dsrouter");
        AddMissing(missingForIosDevice, toolchain.XcrunPath, "Xcode command-line tools");
        return ToolSuccess(new JsonObject
        {
            ["available"] = toolchain.IsAvailable,
            ["androidEmulatorCaptureAvailable"] = toolchain.IsAndroidEmulatorAvailable,
            ["androidDeviceCaptureAvailable"] = toolchain.IsAndroidDeviceAvailable,
            ["iosSimulatorCaptureAvailable"] = toolchain.IsIosSimulatorAvailable,
            ["iosDeviceCaptureAvailable"] = toolchain.IsIosDeviceAvailable,
            ["toolchain"] = JsonSerializer.SerializeToNode(toolchain, JsonUtil.Compact),
            ["capturePreset"] = "startup-explain-v2",
            ["supportedApplicationArtifacts"] = new JsonArray("app", "apk", "ipa"),
            ["supportedTraceArtifacts"] = new JsonArray(
                "nettrace",
                "speedscope",
                "portable-pdb",
                "application-metadata",
                "capture-log"),
            ["diagnosticConfigurations"] = new JsonObject
            {
                ["androidEmulator"] = "10.0.2.2:9000,suspend,connect",
                ["androidDevice"] = "127.0.0.1:9000,suspend,connect",
                ["iosSimulator"] = "127.0.0.1:9000,suspend,listen",
                ["iosDevice"] = "127.0.0.1:9000,suspend,listen"
            },
            ["missingForAndroid"] = missingForAndroid,
            ["missingForIosSimulator"] = missingForIosSimulator,
            ["missingForIosDevice"] = missingForIosDevice,
            ["notes"] = new JsonArray(
                "Ansight installs and launches supplied artifacts; it never restores, builds, or publishes applications.",
                "Artifacts must be built with EnableDiagnostics=true and the target-specific DiagnosticConfiguration.",
                "For an exact application-ready boundary, emit StartupComplete from an EventSource named Ansight-DotNet-Startup; otherwise analysis uses runtime and trace-boundary milestones.")
        });
    }

    private static void AddMissing(JsonArray missing, string? toolPath, string toolName)
    {
        if (toolPath is null)
        {
            missing.Add(toolName);
        }
    }
}

internal sealed class DotNetStartStartupCaptureTool : DotNetOperation
{
    public DotNetStartStartupCaptureTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_start_startup_capture";

    protected override string Title => "Start .NET Startup Capture";

    protected override string Description => "Install and launch a supplied profiling-ready .app, .apk, or .ipa under the Ansight startup EventPipe capture preset.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: DotNetCaptureToolSchemas.ArtifactCaptureProperties(includeDuration: true),
        required: ["applicationPath", "appId", "deviceId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!DotNetCaptureToolSchemas.TryReadRequest(arguments, out var request, out var error))
        {
            return Completed(ToolError(error!));
        }

        try
        {
            var captureId = Services.Profiling.StartStartupCapture(request!);
            Services.Profiling.TryGetCapture(captureId, out var snapshot);
            return Completed(ToolSuccess(new JsonObject
            {
                ["captureId"] = captureId,
                ["status"] = snapshot is null ? null : DotNetOperationPayloads.Snapshot(snapshot),
                ["message"] = "The .NET startup capture has been queued. Poll ansight_dotnet_get_capture_status for completion."
            }));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            return Completed(ToolError(ex.Message));
        }
    }
}

internal sealed class DotNetGetCaptureStatusTool : DotNetOperation
{
    public DotNetGetCaptureStatusTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_capture_status";

    protected override string Title => "Get .NET Capture Status";

    protected override string Description => "Return current artifact preparation, capture, derivation, completion, cancellation, or failure state for a .NET capture job.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        return Services.Profiling.TryGetCapture(captureId, out var snapshot) && snapshot is not null
            ? Completed(ToolSuccess(DotNetOperationPayloads.Snapshot(snapshot)))
            : Completed(ToolError($".NET trace capture '{captureId}' was not found."));
    }
}

internal sealed class DotNetCancelCaptureTool : DotNetOperation
{
    public DotNetCancelCaptureTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_cancel_capture";

    protected override string Title => "Cancel .NET Capture";

    protected override string Description => "Cancel an active .NET artifact preparation or EventPipe capture and terminate its owned profiler processes.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        var cancelled = Services.Profiling.CancelCapture(captureId);
        return Completed(cancelled
            ? ToolSuccess(new JsonObject
            {
                ["captureId"] = captureId,
                ["cancellationRequested"] = true
            })
            : ToolError($"Capture '{captureId}' is not active or was not found."));
    }
}

internal sealed class DotNetListCapturesTool : DotNetOperation
{
    public DotNetListCapturesTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_list_captures";

    protected override string Title => "List .NET Trace Captures";

    protected override string Description => "List persisted Ansight .NET captures and imported NetTrace artifacts with capture state and provenance.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Optional exact app id filter.", nullable: true),
            ["limit"] = ToolSchema.Integer("Maximum captures to return. Defaults to 100, maximum 1000.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 100, 1000, out var limit, out var error))
        {
            return Completed(ToolError(error!));
        }

        var appId = arguments?["appId"]?.GetValue<string>()?.Trim();
        var captures = Services.Profiling.ListCaptures()
            .Where(capture => string.IsNullOrWhiteSpace(appId)
                              || string.Equals(capture.AppId, appId, StringComparison.Ordinal))
            .Take(limit)
            .ToArray();
        return Completed(ToolSuccess(new JsonObject
        {
            ["captureCount"] = captures.Length,
            ["captures"] = JsonSerializer.SerializeToNode(captures, JsonUtil.Compact)
        }));
    }
}

internal sealed class DotNetImportTraceTool : DotNetOperation
{
    public DotNetImportTraceTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_import_trace";

    protected override string Title => "Import .NET Trace";

    protected override string Description => "Import an existing local .nettrace into Ansight's immutable .NET capture store and derive a SpeedScope profile when possible.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["tracePath"] = ToolSchema.String("Required absolute path to an existing .nettrace file."),
            ["appId"] = ToolSchema.String("Optional Ansight app id for provenance.", nullable: true),
            ["applicationPath"] = ToolSchema.String("Optional originating application artifact path for provenance.", nullable: true)
        },
        required: ["tracePath"],
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var tracePath = arguments?["tracePath"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(tracePath))
        {
            return ToolError("tracePath is required.");
        }

        try
        {
            var manifest = await Services.Profiling.ImportTraceAsync(
                tracePath,
                arguments?["appId"]?.GetValue<string>(),
                arguments?["applicationPath"]?.GetValue<string>());
            return ToolSuccess(new JsonObject
            {
                ["captureId"] = manifest.CaptureId,
                ["manifest"] = DotNetOperationPayloads.Manifest(manifest)
            });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            return ToolError(ex.Message);
        }
    }
}

internal sealed class DotNetGetCaptureManifestTool : DotNetOperation
{
    public DotNetGetCaptureManifestTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_capture_manifest";

    protected override string Title => "Get .NET Capture Manifest";

    protected override string Description => "Return the immutable trace inventory, application provenance, hashes, tool versions, warnings, and state for one .NET capture.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        return Services.Profiling.CaptureStore.TryLoadManifest(captureId, out var manifest) && manifest is not null
            ? Completed(ToolSuccess(DotNetOperationPayloads.Manifest(manifest)))
            : Completed(ToolError($".NET trace capture '{captureId}' was not found."));
    }
}

internal sealed class DotNetExportSpeedScopeTool : DotNetOperation
{
    public DotNetExportSpeedScopeTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_export_speedscope";

    protected override string Title => "Export .NET SpeedScope Profile";

    protected override string Description => "Resolve the derived SpeedScope JSON artifact for a .NET capture without treating it as the authoritative raw trace.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        if (!Services.Profiling.CaptureStore.TryLoadManifest(captureId, out var manifest)
            || manifest is null)
        {
            return Completed(ToolError($".NET trace capture '{captureId}' was not found."));
        }

        if (!Services.Profiling.CaptureStore.TryResolveArtifactPath(
                manifest,
                DotNetProfilingEngine.SpeedScopeArtifactKind,
                out var filePath)
            || filePath is null)
        {
            return Completed(ToolError($"Capture '{captureId}' does not contain a SpeedScope derivative."));
        }

        var artifact = manifest.Artifacts.First(candidate =>
            string.Equals(candidate.Kind, DotNetProfilingEngine.SpeedScopeArtifactKind, StringComparison.Ordinal));
        return Completed(ToolSuccess(new JsonObject
        {
            ["captureId"] = captureId,
            ["filePath"] = filePath,
            ["artifact"] = JsonSerializer.SerializeToNode(artifact, JsonUtil.Compact),
            ["authoritative"] = false,
            ["rawArtifactKind"] = DotNetProfilingEngine.NetTraceArtifactKind
        }));
    }
}

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
