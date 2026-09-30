using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class GetLiveVisualTreeTool : RemoteAppOperation
{
    private const int DefaultMaxNodes = 5000;
    private const int DefaultDetailedMaxNodes = 1000;
    private const int DefaultReactComponentTreeMaxDepth = 28;

    public GetLiveVisualTreeTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_live_visual_tree";

    protected override string Title => "Get Live Visual Tree";

    protected override string Description => "Describe the rendered UI from the device accessibility hierarchy first. Falls back to the connected app's MAUI, React Native, Flutter widget, Cordova/Capacitor DOM, or native visual tree when device accessibility is unavailable; pass toolId to force an app tree.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["toolId"] = ToolSchema.String(
                "Optional visual-tree tool id to force.",
                nullable: true,
                enumValues:
                [
                    VisualTreeContract.DomToolId,
                    VisualTreeContract.FlutterToolId,
                    VisualTreeContract.MauiToolId,
                    VisualTreeContract.ReactComponentToolId,
                    VisualTreeContract.ReactShadowToolId,
                    VisualTreeContract.NativeToolId
                ]),
            ["arguments"] = ToolSchema.Object(
                description: "Optional remote visual-tree arguments. Direct arguments on this operation override these values.",
                properties: new Dictionary<string, ToolSchema>(),
                additionalProperties: true,
                nullable: true),
            ["root"] = ToolSchema.String(
                "MAUI root scope. Defaults to currentPage.",
                nullable: true,
                enumValues:
                [
                    VisualTreeContract.CurrentPageRootScope,
                    VisualTreeContract.RootPageRootScope,
                    VisualTreeContract.WindowRootScope
                ]),
            ["includeBounds"] = ToolSchema.Boolean("Include element bounds. Defaults to true.", nullable: true),
            ["includeProperties"] = ToolSchema.Boolean("Include detailed framework properties. Defaults to false and forces framework capture when true.", nullable: true),
            ["includeBindableProperties"] = ToolSchema.Boolean("Include MAUI bindable properties. Defaults to false.", nullable: true),
            ["includeBindingContexts"] = ToolSchema.Boolean("Include MAUI binding context data. Defaults to includeProperties.", nullable: true),
            ["includeInactivePages"] = ToolSchema.Boolean("Include inactive MAUI pages. Defaults to false.", nullable: true),
            ["includeComputedStyles"] = ToolSchema.Boolean("Include computed UI styles. Defaults to false.", nullable: true),
            ["includeProps"] = ToolSchema.Boolean("Include sanitized React props for React tree tools. Defaults to false.", nullable: true),
            ["includeState"] = ToolSchema.Boolean("Include sanitized React Fiber state for React component-tree tools. Defaults to false.", nullable: true),
            ["maxDepth"] = ToolSchema.Integer("Maximum traversal depth. Defaults to 32, except React component-tree tools default to 28.", nullable: true),
            ["maxNodes"] = ToolSchema.Integer("Maximum nodes to return. Defaults to 256.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var includeProperties = arguments?["includeProperties"]?.GetValue<bool>() ?? false;
        var includeBounds = arguments?["includeBounds"]?.GetValue<bool>() ?? true;
        if (NormalizeOptionalString(arguments?["toolId"]?.GetValue<string>()) is null
            && !includeProperties)
        {
            var maxNodes = Math.Clamp(
                LiveUiToolSchemas.ReadInteger(arguments, "maxNodes", 256),
                1,
                2_000);
            var maxDepth = Math.Clamp(
                LiveUiToolSchemas.ReadInteger(arguments, "maxDepth", 32),
                1,
                64);
            var accessibilityResult = await LiveUiTreeCapture.CaptureDeviceAccessibilityAsync(
                snapshot!,
                uiInputRouter,
                maxNodes,
                maxDepth,
                ToolExecutionCancellation.Current);
            if (accessibilityResult.Capture is { } accessibilityCapture)
            {
                var persisted = SessionVisualTreePersistence.Persist(
                    runtimeState,
                    accessibilityCapture.Session,
                    accessibilityCapture.ToolId,
                    accessibilityCapture.Payload);
                var responsePayload = accessibilityCapture.Payload.DeepClone().AsObject();
                if (!includeBounds)
                {
                    RemoveNodeBounds(responsePayload["root"] as JsonObject);
                }

                return RequestResult.ToolResult(
                    new JsonObject
                    {
                        ["sessionId"] = accessibilityCapture.Session.SessionId,
                        ["appId"] = accessibilityCapture.Session.AppId,
                        ["toolId"] = accessibilityCapture.ToolId,
                        ["responseType"] = "device-accessibility",
                        ["payload"] = new JsonObject
                        {
                            ["result"] = responsePayload
                        },
                        ["persistedVisualTree"] = new JsonObject
                        {
                            ["persisted"] = persisted.IsSuccess,
                            ["snapshotId"] = persisted.Snapshot?.SnapshotId,
                            ["treeHash"] = persisted.Snapshot?.TreeHash,
                            ["message"] = persisted.Message
                        }
                    },
                    isError: false);
            }
        }

        if (snapshot!.CaptureSource == WorkspaceExecutionModes.Device)
            return ToolError("Device accessibility is unavailable or app-only tree details were requested. Use screenshot text; app trees require SDK mode.");

        var toolsResponse = await appToolBridge.QueryToolsAsync(
            snapshot!.SessionId,
            ToolExecutionCancellation.Current,
            new AppToolBridgeRequestContext("host-ui", Name, correlationId));
        if (!toolsResponse.Success || toolsResponse.Envelope is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["message"] = toolsResponse.Message
                },
                isError: true);
        }

        if (string.Equals(toolsResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["error"] = toolsResponse.Envelope.Payload?.DeepClone()
                },
                isError: true);
        }

        var toolId = ResolveToolId(arguments, toolsResponse.Envelope.Payload);
        if (toolId is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["message"] = "The selected app does not expose a supported MAUI, React Native, Flutter, Cordova/Capacitor DOM, or native visual-tree tool.",
                    ["catalog"] = toolsResponse.Envelope.Payload?.DeepClone()
                },
                isError: true);
        }

        var effectiveArguments = arguments?.DeepClone().AsObject() ?? new JsonObject();
        effectiveArguments["maxNodes"] ??= 256;
        effectiveArguments["maxDepth"] ??= 32;
        if (!TryBuildVisualTreeArguments(effectiveArguments, toolId, out var remoteArguments, out var errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid live visual-tree arguments.");
        }

        return await BuildCallAppToolResultAsync(
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["toolId"] = toolId,
                ["arguments"] = remoteArguments
            },
            correlationId,
            Name,
            requestSource: "host-ui");
    }

    internal static string? ResolveToolId(JsonObject? arguments, JsonNode? catalogPayload)
    {
        var requestedToolId = NormalizeOptionalString(arguments?["toolId"]?.GetValue<string>());
        if (requestedToolId is not null)
        {
            return AppToolPayloadNormalizer.IsVisualTreeTool(requestedToolId)
                   && RemoteAppToolCatalog.HasTool(catalogPayload, requestedToolId)
                ? requestedToolId
                : null;
        }

        if (RemoteAppToolCatalog.HasTool(catalogPayload, RemoteAppToolIds.MauiGetVisualTree))
        {
            return RemoteAppToolIds.MauiGetVisualTree;
        }

        if (RemoteAppToolCatalog.HasTool(catalogPayload, RemoteAppToolIds.ReactGetShadowTree))
        {
            return RemoteAppToolIds.ReactGetShadowTree;
        }

        if (RemoteAppToolCatalog.HasTool(catalogPayload, RemoteAppToolIds.ReactGetComponentTree))
        {
            return RemoteAppToolIds.ReactGetComponentTree;
        }

        if (RemoteAppToolCatalog.HasTool(catalogPayload, RemoteAppToolIds.FlutterGetWidgetTree))
        {
            return RemoteAppToolIds.FlutterGetWidgetTree;
        }

        if (RemoteAppToolCatalog.HasTool(catalogPayload, RemoteAppToolIds.DomGetDocument))
        {
            return RemoteAppToolIds.DomGetDocument;
        }

        return RemoteAppToolCatalog.HasTool(catalogPayload, RemoteAppToolIds.UiGetVisualTree)
            ? RemoteAppToolIds.UiGetVisualTree
            : null;
    }

    internal static void RemoveNodeBounds(JsonObject? node)
    {
        if (node is null)
        {
            return;
        }

        node.Remove("bounds");
        if (node["children"] is not JsonArray children)
        {
            return;
        }

        foreach (var child in children.OfType<JsonObject>())
        {
            RemoveNodeBounds(child);
        }
    }

    internal static bool TryBuildVisualTreeArguments(
        JsonObject? arguments,
        string toolId,
        out JsonObject remoteArguments,
        out string? errorMessage)
    {
        remoteArguments = BuildDefaultVisualTreeArguments(toolId);
        errorMessage = null;
        if (arguments?["arguments"] is JsonObject suppliedArguments)
        {
            foreach (var pair in suppliedArguments)
            {
                remoteArguments[pair.Key] = pair.Value?.DeepClone();
            }
        }

        if (!TryApplyOptionalBoolean(arguments, remoteArguments, "includeBounds", out errorMessage)
            || !TryApplyOptionalBoolean(arguments, remoteArguments, "includeProperties", out errorMessage)
            || !TryApplyOptionalBoolean(arguments, remoteArguments, "includeBindableProperties", out errorMessage)
            || !TryApplyOptionalBoolean(arguments, remoteArguments, "includeBindingContexts", out errorMessage)
            || !TryApplyOptionalBoolean(arguments, remoteArguments, "includeInactivePages", out errorMessage)
            || !TryApplyOptionalBoolean(arguments, remoteArguments, "includeComputedStyles", out errorMessage)
            || !TryApplyOptionalBoolean(arguments, remoteArguments, "includeProps", out errorMessage)
            || !TryApplyOptionalBoolean(arguments, remoteArguments, "includeState", out errorMessage)
            || !TryApplyOptionalInteger(arguments, remoteArguments, "maxDepth", 0, SessionEvidenceDefaults.DefaultVisualTreeMaxDepth, out errorMessage)
            || !TryApplyOptionalInteger(arguments, remoteArguments, "maxNodes", 1, int.MaxValue, out errorMessage))
        {
            return false;
        }

        var root = NormalizeOptionalString(arguments?["root"]?.GetValue<string>());
        if (root is not null)
        {
            remoteArguments["root"] = root;
        }

        if (string.Equals(toolId, RemoteAppToolIds.MauiGetVisualTree, StringComparison.Ordinal)
            && !TryNormalizeMauiRootScope(remoteArguments, out errorMessage))
        {
            return false;
        }

        ApplyReactPropertyAlias(toolId, arguments, remoteArguments);
        ApplyMauiDefaultMaxNodes(toolId, remoteArguments);
        return true;
    }

    private static void ApplyReactPropertyAlias(string toolId, JsonObject? arguments, JsonObject remoteArguments)
    {
        if (!IsReactTreeTool(toolId)
            || arguments?["includeProps"] is not null
            || !ReadBoolean(remoteArguments, "includeProperties"))
        {
            return;
        }

        remoteArguments["includeProps"] = true;
    }

    internal static JsonObject BuildDefaultVisualTreeArguments(string toolId)
    {
        if (string.Equals(toolId, RemoteAppToolIds.MauiGetVisualTree, StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["root"] = MauiVisualTreeRootScope.Default,
                ["includeBounds"] = true,
                ["includeProperties"] = false,
                ["includeBindableProperties"] = false,
                ["includeBindingContexts"] = false,
                ["includeInactivePages"] = false,
                ["maxDepth"] = SessionEvidenceDefaults.DefaultVisualTreeMaxDepth
            };
        }

        if (string.Equals(toolId, RemoteAppToolIds.ReactGetComponentTree, StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["includeBounds"] = true,
                ["includeProps"] = false,
                ["includeState"] = false,
                ["maxDepth"] = DefaultReactComponentTreeMaxDepth,
                ["maxNodes"] = DefaultMaxNodes
            };
        }

        if (string.Equals(toolId, RemoteAppToolIds.ReactGetShadowTree, StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["includeBounds"] = true,
                ["includeProps"] = false,
                ["maxDepth"] = SessionEvidenceDefaults.DefaultVisualTreeMaxDepth,
                ["maxNodes"] = DefaultMaxNodes
            };
        }

        if (string.Equals(toolId, RemoteAppToolIds.FlutterGetWidgetTree, StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["maxDepth"] = SessionEvidenceDefaults.DefaultVisualTreeMaxDepth,
                ["maxNodes"] = DefaultMaxNodes
            };
        }

        if (string.Equals(toolId, RemoteAppToolIds.DomGetDocument, StringComparison.Ordinal))
        {
            return [];
        }

        return new JsonObject
        {
            ["includeBounds"] = true,
            ["includeComputedStyles"] = false,
            ["maxDepth"] = SessionEvidenceDefaults.DefaultVisualTreeMaxDepth
        };
    }

    private static bool IsReactTreeTool(string toolId)
        => string.Equals(toolId, RemoteAppToolIds.ReactGetShadowTree, StringComparison.Ordinal)
           || string.Equals(toolId, RemoteAppToolIds.ReactGetComponentTree, StringComparison.Ordinal);

    private static bool TryNormalizeMauiRootScope(JsonObject arguments, out string? errorMessage)
    {
        errorMessage = null;
        if (arguments["root"] is null)
        {
            arguments["root"] = MauiVisualTreeRootScope.Default;
            return true;
        }

        if (arguments["root"] is not JsonValue rootValue
            || !rootValue.TryGetValue<string>(out var root))
        {
            errorMessage = MauiVisualTreeRootScope.ValidationMessage;
            return false;
        }

        if (!MauiVisualTreeRootScope.TryNormalize(root, out var normalizedRootScope))
        {
            errorMessage = MauiVisualTreeRootScope.ValidationMessage;
            return false;
        }

        arguments["root"] = normalizedRootScope;
        return true;
    }

    private static void ApplyMauiDefaultMaxNodes(string toolId, JsonObject remoteArguments)
    {
        if (!string.Equals(toolId, RemoteAppToolIds.MauiGetVisualTree, StringComparison.Ordinal)
            || remoteArguments["maxNodes"] is not null)
        {
            return;
        }

        var includeProperties = ReadBoolean(remoteArguments, "includeProperties");
        var includeBindableProperties = ReadBoolean(remoteArguments, "includeBindableProperties");
        remoteArguments["maxNodes"] = includeProperties || includeBindableProperties
            ? DefaultDetailedMaxNodes
            : DefaultMaxNodes;
    }

    private static bool ReadBoolean(JsonObject arguments, string propertyName)
        => arguments[propertyName] is JsonValue jsonValue
           && jsonValue.TryGetValue<bool>(out var value)
           && value;

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
