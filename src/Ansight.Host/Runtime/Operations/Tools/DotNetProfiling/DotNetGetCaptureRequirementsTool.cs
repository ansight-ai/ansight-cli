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
