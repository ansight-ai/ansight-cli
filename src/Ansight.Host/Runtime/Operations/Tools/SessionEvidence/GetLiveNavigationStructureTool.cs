using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.AppGraphs;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class GetLiveNavigationStructureTool : RemoteAppOperation
{
    public GetLiveNavigationStructureTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_live_navigation_structure";

    protected override string Title => "Get Live Navigation Structure";

    protected override string Description =>
        "Read toolkit-owned navigation structure from a connected app. Supports exact navigation tools exposed by "
        + "MAUI, React Native, Flutter, UIKit, SwiftUI, AppKit, Android Views, or Jetpack Compose.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["framework"] = ToolSchema.String(
                "Optional framework override. Omit to select from the live app tool catalog.",
                nullable: true,
                enumValues:
                [
                    "maui",
                    "react-native",
                    "flutter",
                    "ios-uikit",
                    "ios-swiftui",
                    "maccatalyst-uikit",
                    "maccatalyst-swiftui",
                    "macos-appkit",
                    "macos-swiftui",
                    "android-views",
                    "android-compose"
                ])
        },
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var requestContext = new AppToolBridgeRequestContext(
            "host-ui",
            Name,
            correlationId);
        var catalogResponse = await appToolBridge.QueryToolsAsync(
            snapshot!.SessionId,
            ToolExecutionCancellation.Current,
            requestContext);
        if (!catalogResponse.Success || catalogResponse.Envelope is null)
        {
            return ToolError(catalogResponse.Message);
        }

        var framework = NormalizeOptional(arguments?["framework"]?.GetValue<string>());
        if (!TryResolveNavigationTool(
                catalogResponse.Envelope.Payload,
                framework,
                out var selectedController,
                out var navigationToolId))
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["message"] = framework is null
                        ? "The live app does not expose a supported toolkit-specific navigation-state tool. Use its visual-tree platform, source, adapter, and type evidence instead."
                        : $"The live app does not expose navigation-state support for '{framework}'."
                },
                isError: true);
        }

        var result = await BuildCallAppToolResultAsync(
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["toolId"] = navigationToolId,
                ["arguments"] = new JsonObject()
            },
            correlationId,
            Name,
            requestSource: "host-ui");
        if (result.Payload?["structuredContent"] is not JsonObject structuredContent)
        {
            return result;
        }

        var navigationContent = PrepareStructuredContent(
            structuredContent,
            catalogResponse.Envelope.Payload,
            selectedController);
        return RequestResult.ToolResult(
            navigationContent,
            result.Payload["isError"]?.GetValue<bool>() ?? result.IsError);
    }

    internal static JsonObject PrepareStructuredContent(
        JsonObject structuredContent,
        JsonNode? catalogPayload,
        NavigationController? selectedController)
    {
        var navigationContent = structuredContent.DeepClone().AsObject();
        navigationContent["selectedNavigationController"] = selectedController?.Framework;
        navigationContent["availableNavigationControllers"] = BuildAvailableNavigationControllers(
            catalogPayload,
            selectedController?.Framework);
        return navigationContent;
    }

    internal static bool TryResolveNavigationTool(
        JsonNode? catalogPayload,
        string? framework,
        out NavigationController? controller,
        out string? navigationToolId)
    {
        controller = null;
        navigationToolId = null;
        var normalizedFramework = NormalizeOptional(framework);
        foreach (var candidate in NavigationControllerCatalog.All)
        {
            if (normalizedFramework is not null
                && !string.Equals(candidate.Framework, normalizedFramework, StringComparison.Ordinal))
            {
                continue;
            }

            navigationToolId = candidate.NavigationToolIds.FirstOrDefault(toolId =>
                RemoteAppToolCatalog.HasTool(catalogPayload, toolId));
            if (navigationToolId is not null)
            {
                controller = candidate;
                return true;
            }
        }
        return false;
    }

    internal static JsonArray BuildAvailableNavigationControllers(
        JsonNode? catalogPayload,
        string? preferredFramework)
    {
        var normalizedPreferredFramework = NormalizeOptional(preferredFramework);
        var candidates = NavigationControllerCatalog.All
            .OrderByDescending(candidate => string.Equals(
                candidate.Framework,
                normalizedPreferredFramework,
                StringComparison.Ordinal));
        var claimedToolIds = new HashSet<string>(StringComparer.Ordinal);
        var results = new JsonArray();
        foreach (var candidate in candidates)
        {
            var toolId = candidate.NavigationToolIds.FirstOrDefault(navigationToolId =>
                !claimedToolIds.Contains(navigationToolId)
                && RemoteAppToolCatalog.HasTool(catalogPayload, navigationToolId));
            if (toolId is null)
            {
                continue;
            }

            claimedToolIds.Add(toolId);
            results.Add(new JsonObject
            {
                ["framework"] = candidate.Framework,
                ["navigationToolId"] = toolId,
                ["guidance"] = NavigationGuidance.Read(candidate.Framework),
                ["technologyKinds"] = AppGraphNavigationTechnologyCatalog.BuildKindsJson(candidate.Framework)
            });
        }
        return results;
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
