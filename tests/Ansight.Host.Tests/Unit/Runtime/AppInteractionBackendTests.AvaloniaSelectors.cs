using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Theory]
    [InlineData("avalonia.get_accessibility_tree", 1)]
    [InlineData("avalonia.get_logical_tree", 2)]
    [InlineData("avalonia.get_visual_tree", 3)]
    [InlineData("ui.get_visual_tree", 4)]
    public async Task AvaloniaSelectorSearchUsesSemanticTreesBeforeNativeFallback(string matchingTool, int expectedCaptures)
    {
        var session = CreateModalTestSession();
        var bridge = new AvaloniaSelectorBridge(session.SessionId, matchingTool);
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Standard button" });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session, bridge, "ansight_tap_ui", null, selector, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(matchingTool, result.Capture?.ToolId);
        Assert.Equal(expectedCaptures, bridge.CapturedTools.Count);
        Assert.Equal(bridge.CapturedTools, result.AttemptedToolIds);
    }

    [Fact]
    public async Task AvaloniaSelectorSearchDoesNotRepeatAFailedProvider()
    {
        var session = CreateModalTestSession();
        var bridge = new AvaloniaSelectorBridge(session.SessionId, VisualTreeContract.AvaloniaLogicalToolId)
        {
            FailAccessibility = true
        };
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Standard button" });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session, bridge, "ansight_tap_ui", null, selector, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(VisualTreeContract.AvaloniaLogicalToolId, result.Capture?.ToolId);
        Assert.Equal(new[] { VisualTreeContract.AvaloniaAccessibilityToolId, VisualTreeContract.AvaloniaLogicalToolId },
            bridge.CapturedTools);
        Assert.Equal(bridge.CapturedTools, result.AttemptedToolIds);
    }

    [Fact]
    public async Task AvaloniaSelectorSearchRetainsSemanticEvidenceWhenNoProviderMatches()
    {
        var session = CreateModalTestSession();
        var bridge = new AvaloniaSelectorBridge(session.SessionId, matchingTool: null);
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Missing button" });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session, bridge, "ansight_tap_ui", null, selector, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(VisualTreeContract.AvaloniaAccessibilityToolId, result.Capture?.ToolId);
        Assert.Equal(4, result.AttemptedToolIds.Count);
        Assert.Empty(LiveUiNodeQuery.Find(result.Capture!.Root, selector, result.Capture.TypeRegistry));
    }

    [Theory]
    [InlineData(true, "button")]
    [InlineData(false, "parent")]
    public void AvaloniaSelectionPrefersAnEnabledControlOverItsInheritedParentLabel(bool enabled, string expectedId)
    {
        var payload = CreateAvaloniaSelectorTree("Standard button");
        var root = payload["root"]!.AsObject();
        root["label"] = "Standard button";
        root["children"]![0]!["enabled"] = enabled;
        var registry = VisualTreeTypeRegistry.FromPayload(payload);
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Standard button" });
        var matches = LiveUiNodeQuery.Find(root, selector, registry);

        var ordered = LiveUiNodeQuery.OrderMatchesForSelection(matches, payload, new LiveUiBounds(0, 0, 390, 844));

        Assert.Equal(2, ordered.Count);
        Assert.Equal(expectedId, ordered[0].Node["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, "avalonia:822")]
    [InlineData("avalonia:100", "avalonia:822")]
    public void AvaloniaSubtreeCaptureForwardsTopLevelRoot(string? nestedRoot, string expectedRoot)
    {
        var arguments = new JsonObject
        {
            ["rootNodeId"] = expectedRoot,
            ["arguments"] = new JsonObject { ["rootNodeId"] = nestedRoot }
        };

        var success = GetLiveVisualTreeTool.TryBuildVisualTreeArguments(
            arguments, VisualTreeContract.AvaloniaVisualToolId, out var remoteArguments, out var error);

        Assert.True(success, error);
        Assert.Equal(expectedRoot, remoteArguments["rootNodeId"]!.GetValue<string>());
        Assert.Equal(nestedRoot, arguments["arguments"]!["rootNodeId"]?.GetValue<string>());
    }

    [Fact]
    public void AvaloniaTreeResolverHonorsExplicitVisualCapture()
    {
        var catalog = AvaloniaSelectorBridge.CreateCatalog();
        var arguments = new JsonObject { ["toolId"] = VisualTreeContract.AvaloniaVisualToolId };

        Assert.Equal(VisualTreeContract.AvaloniaAccessibilityToolId, GetLiveVisualTreeTool.ResolveToolId(null, catalog));
        Assert.Equal(VisualTreeContract.AvaloniaVisualToolId, GetLiveVisualTreeTool.ResolveToolId(arguments, catalog));
    }

    private static JsonObject CreateAvaloniaSelectorTree(string label) => new()
    {
        ["format"] = "ansight.native.visual-tree.compact.v2",
        ["platform"] = "ios",
        ["types"] = new JsonArray("Avalonia.Controls.Panel", "Avalonia.Controls.Button"),
        ["coordinateSpace"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 390, ["height"] = 844 },
        ["root"] = new JsonObject
        {
            ["id"] = "parent",
            ["typeId"] = 0,
            ["visible"] = true,
            ["enabled"] = true,
            ["interactable"] = false,
            ["bounds"] = new JsonArray(0, 0, 390, 844),
            ["children"] = new JsonArray(new JsonObject
            {
                ["id"] = "button",
                ["typeId"] = 1,
                ["label"] = label,
                ["visible"] = true,
                ["enabled"] = true,
                ["interactable"] = true,
                ["bounds"] = new JsonArray(30, 200, 140, 40)
            })
        }
    };

    private sealed class AvaloniaSelectorBridge : IAppToolBridge
    {
        private readonly string sessionId;
        private readonly string? matchingTool;

        public AvaloniaSelectorBridge(string sessionId, string? matchingTool)
        {
            this.sessionId = sessionId;
            this.matchingTool = matchingTool;
        }

        public List<string> CapturedTools { get; } = [];
        public bool FailAccessibility { get; init; }

        public event EventHandler? ConnectionsChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public IReadOnlyList<string> GetConnectedSessionIds() => [sessionId];

        public bool IsSessionConnected(string id) => id == sessionId;

        public OperationResult ForceDisconnectSession(string id) => OperationResult.Failure("Unsupported.");

        public static JsonObject CreateCatalog() => new()
        {
            ["tools"] = new JsonArray(
                new JsonObject { ["id"] = VisualTreeContract.AvaloniaVisualToolId },
                new JsonObject { ["id"] = VisualTreeContract.AvaloniaLogicalToolId },
                new JsonObject { ["id"] = VisualTreeContract.AvaloniaAccessibilityToolId },
                new JsonObject { ["id"] = RemoteAppToolIds.UiGetVisualTree })
        };

        public Task<AppToolBridgeResponse> QueryToolsAsync(string id, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            return Task.FromResult(AppToolBridgeResponse.FromSuccess("Catalog.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.CatalogType,
                Id = "catalog",
                SessionId = id,
                Payload = CreateCatalog()
            }));
        }

        public Task<AppToolBridgeResponse> CallToolAsync(string id, string toolId, JsonObject? arguments,
            CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
        {
            CapturedTools.Add(toolId);

            if (FailAccessibility && toolId == VisualTreeContract.AvaloniaAccessibilityToolId)
            {
                return Task.FromResult(AppToolBridgeResponse.FromFailure("Accessibility unavailable."));
            }

            return Task.FromResult(AppToolBridgeResponse.FromSuccess("Tree.", new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ResultType,
                Id = "tree",
                SessionId = id,
                Payload = new JsonObject
                {
                    ["toolId"] = toolId,
                    ["success"] = true,
                    ["result"] = CreateAvaloniaSelectorTree(toolId == matchingTool ? "Standard button" : "Other content")
                }
            }));
        }
    }
}
