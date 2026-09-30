using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Tools;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class FrameworkToolIntegrationTests
{
    [Fact]
    public void LiveKeyboardState_RequiresDeviceAccessibilityEvidence()
    {
        Assert.True(LiveKeyboardState.TryRead(
            LiveUiTreeCapture.DeviceAccessibilityToolId,
            new JsonObject { ["keyboardVisible"] = true },
            out var isOpen));
        Assert.True(isOpen);

        Assert.False(LiveKeyboardState.TryRead(
            RemoteAppToolIds.MauiGetVisualTree,
            new JsonObject { ["keyboardVisible"] = true },
            out _));
        Assert.False(LiveKeyboardState.TryRead(
            LiveUiTreeCapture.DeviceAccessibilityToolId,
            new JsonObject(),
            out _));
    }

    [Theory]
    [InlineData("flutter.get_widget_tree")]
    [InlineData("dom.get_document")]
    public void LiveVisualTreeResolver_PrefersFrameworkTreeOverNativeContainer(string frameworkToolId)
    {
        var catalog = CreateCatalog(RemoteAppToolIds.UiGetVisualTree, frameworkToolId);

        var resolved = GetLiveVisualTreeTool.ResolveToolId(arguments: null, catalog);

        Assert.Equal(frameworkToolId, resolved);
    }

    [Fact]
    public void LiveVisualTreeResolver_HonorsExplicitAvailableTool()
    {
        var catalog = CreateCatalog(RemoteAppToolIds.UiGetVisualTree, RemoteAppToolIds.DomGetDocument);
        var arguments = new JsonObject
        {
            ["toolId"] = RemoteAppToolIds.UiGetVisualTree
        };

        var resolved = GetLiveVisualTreeTool.ResolveToolId(arguments, catalog);

        Assert.Equal(RemoteAppToolIds.UiGetVisualTree, resolved);
    }

    [Fact]
    public void LiveVisualTreeResolver_RejectsExplicitNonVisualTreeTool()
    {
        const string nonVisualTreeToolId = "preferences.get";
        var catalog = CreateCatalog(nonVisualTreeToolId);
        var arguments = new JsonObject
        {
            ["toolId"] = nonVisualTreeToolId
        };

        var resolved = GetLiveVisualTreeTool.ResolveToolId(arguments, catalog);

        Assert.Null(resolved);
    }

    [Fact]
    public void LiveVisualTreeArguments_UseCanonicalMauiDefaultRootScope()
    {
        var built = GetLiveVisualTreeTool.TryBuildVisualTreeArguments(
            arguments: null,
            RemoteAppToolIds.MauiGetVisualTree,
            out var remoteArguments,
            out var errorMessage);

        Assert.True(built, errorMessage);
        Assert.Equal(MauiVisualTreeRootScope.CurrentPage, remoteArguments["root"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("root", "currentPage")]
    [InlineData("currentPage", "currentPage")]
    [InlineData("CURRENT_PAGE", "currentPage")]
    [InlineData("Current page", "currentPage")]
    [InlineData("rootPage", "rootPage")]
    [InlineData("root_page", "rootPage")]
    [InlineData("Root page", "rootPage")]
    [InlineData("WINDOW", "window")]
    public void LiveVisualTreeArguments_NormalizeMauiRootScope(string supplied, string expected)
    {
        var built = GetLiveVisualTreeTool.TryBuildVisualTreeArguments(
            new JsonObject { ["root"] = supplied },
            RemoteAppToolIds.MauiGetVisualTree,
            out var remoteArguments,
            out var errorMessage);

        Assert.True(built, errorMessage);
        Assert.Equal(expected, remoteArguments["root"]?.GetValue<string>());
    }

    [Fact]
    public void LiveVisualTreeArguments_NormalizeNestedLegacyMauiRootScope()
    {
        var built = GetLiveVisualTreeTool.TryBuildVisualTreeArguments(
            new JsonObject
            {
                ["arguments"] = new JsonObject
                {
                    ["root"] = "root"
                }
            },
            RemoteAppToolIds.MauiGetVisualTree,
            out var remoteArguments,
            out var errorMessage);

        Assert.True(built, errorMessage);
        Assert.Equal(MauiVisualTreeRootScope.CurrentPage, remoteArguments["root"]?.GetValue<string>());
    }

    [Fact]
    public void LiveVisualTreeArguments_RejectInvalidMauiRootScopeBeforeCallingApp()
    {
        var built = GetLiveVisualTreeTool.TryBuildVisualTreeArguments(
            new JsonObject { ["root"] = "application" },
            RemoteAppToolIds.MauiGetVisualTree,
            out _,
            out var errorMessage);

        Assert.False(built);
        Assert.Equal(MauiVisualTreeRootScope.ValidationMessage, errorMessage);
    }

    [Fact]
    public async Task LiveUiTreeCapture_UsesWindowScopeToIncludeModalPages()
    {
        var bridge = new RecordingVisualTreeAppToolBridge();
        var session = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };

        var result = await LiveUiTreeCapture.CaptureAsync(
            session,
            bridge,
            "ansight_tap_ui",
            correlationId: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(RemoteAppToolIds.MauiGetVisualTree, bridge.LastToolId);
        Assert.Equal(
            MauiVisualTreeRootScope.Window,
            bridge.LastArguments?["root"]?.GetValue<string>());
        Assert.NotNull(result.Capture?.Viewport);
        Assert.Equal(1024, result.Capture?.Viewport?.Width);
        Assert.Equal(768, result.Capture?.Viewport?.Height);
    }

    [Fact]
    public async Task LiveUiTreeCapture_AccessibilityCaptureDoesNotInvokeNativeFallback()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge();
        var session = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };

        var result = await LiveUiTreeCapture.CaptureAccessibilityAsync(
            session,
            bridge,
            "ansight_find_ui",
            correlationId: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal([RemoteAppToolIds.MauiGetVisualTree], bridge.CalledToolIds);
        Assert.Equal(RemoteAppToolIds.MauiGetVisualTree, result.Capture?.ToolId);
    }

    [Fact]
    public async Task LiveUiTreeCapture_UsesDeviceAccessibilityBeforeAppVisualTree()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge(mauiContainsCycle: false);
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new StaticAccessibilityDriver(CreateDeviceAccessibilityTree()));
        using var target = router.BeginTargetScope("session-001", "simulator-001");
        var session = CreateLiveSession();
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Crags",
            ["role"] = "button",
            ["visible"] = true
        });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session,
            bridge,
            router,
            "ansight_tap_ui",
            correlationId: null,
            selector,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, result.Capture?.ToolId);
        Assert.Equal([LiveUiTreeCapture.DeviceAccessibilityToolId], result.AttemptedToolIds);
        Assert.Empty(bridge.CalledToolIds);
    }

    [Fact]
    public async Task LiveUiTreeCapture_FallsBackToFrameworkWhenAccessibilityMissesSelector()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge(mauiContainsCycle: false);
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new StaticAccessibilityDriver(CreateDeviceAccessibilityTree()));
        using var target = router.BeginTargetScope("session-001", "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "AccountPage",
            ["visible"] = true
        });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            CreateLiveSession(),
            bridge,
            router,
            "ansight_find_ui",
            correlationId: null,
            selector,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(RemoteAppToolIds.MauiGetVisualTree, result.Capture?.ToolId);
        Assert.Equal(
            [LiveUiTreeCapture.DeviceAccessibilityToolId, RemoteAppToolIds.MauiGetVisualTree],
            result.AttemptedToolIds);
        Assert.Equal([RemoteAppToolIds.MauiGetVisualTree], bridge.CalledToolIds);
    }

    [Fact]
    public async Task LiveUiTreeCapture_DoesNotFallThroughModalAccessibilityToUnderlyingFrameworkTarget()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge(mauiContainsCycle: false);
        var router = new UiInputRouter();
        router.ConfigureAccessibility(new StaticAccessibilityDriver(CreateModalDeviceAccessibilityTree()));
        using var target = router.BeginTargetScope("session-001", "simulator-001");
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "AccountPage",
            ["visible"] = true
        });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            CreateLiveSession(),
            bridge,
            router,
            "ansight_tap_ui",
            correlationId: null,
            selector,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(LiveUiTreeCapture.DeviceAccessibilityToolId, result.Capture?.ToolId);
        Assert.Equal([LiveUiTreeCapture.DeviceAccessibilityToolId], result.AttemptedToolIds);
        Assert.Empty(bridge.CalledToolIds);
        Assert.Empty(LiveUiNodeQuery.Find(
            Assert.IsType<LiveUiTreeCapture>(result.Capture).Root,
            selector,
            result.Capture.TypeRegistry));
    }

    [Fact]
    public async Task LiveUiTreeCapture_NativeCaptureIsExplicit()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge();
        var session = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };

        var result = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(
            session,
            bridge,
            "ansight_find_ui",
            correlationId: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal([RemoteAppToolIds.UiGetVisualTree], bridge.CalledToolIds);
        Assert.Equal(RemoteAppToolIds.UiGetVisualTree, result.Capture?.ToolId);
    }

    [Fact]
    public async Task LiveUiTreeCapture_FallsBackToNativeTreeWhenMauiTreeContainsCycle()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge();
        var session = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };

        var result = await LiveUiTreeCapture.CaptureAsync(
            session,
            bridge,
            "ansight_find_ui",
            correlationId: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var capture = Assert.IsType<LiveUiTreeCapture>(result.Capture);
        Assert.Equal(
            [RemoteAppToolIds.MauiGetVisualTree, RemoteAppToolIds.UiGetVisualTree],
            bridge.CalledToolIds);
        Assert.Equal(RemoteAppToolIds.UiGetVisualTree, capture.ToolId);
        Assert.Single(LiveUiNodeQuery.Find(
            capture.Root,
            LiveUiSelector.Parse(new JsonObject
            {
                ["automationId"] = "account-signout-button",
                ["visible"] = true
            }),
            capture.TypeRegistry));
    }

    [Theory]
    [InlineData("android")]
    [InlineData("ios")]
    public async Task LiveUiTreeCapture_FallsBackToNativeTreeWhenSelectorMatchesSystemDialog(
        string nativePlatform)
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge(
            mauiContainsCycle: false,
            nativeButtonText: "Yes",
            nativePlatform: nativePlatform);
        var session = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Yes",
            ["role"] = "button",
            ["visible"] = true
        });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session,
            bridge,
            "ansight_wait_for_ui",
            correlationId: null,
            selector,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var capture = Assert.IsType<LiveUiTreeCapture>(result.Capture);
        Assert.Equal(
            [RemoteAppToolIds.MauiGetVisualTree, RemoteAppToolIds.UiGetVisualTree],
            bridge.CalledToolIds);
        Assert.Equal(RemoteAppToolIds.UiGetVisualTree, capture.ToolId);
        Assert.Single(LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry));
    }

    [Fact]
    public async Task LiveUiTreeCapture_UsesNativeFallbackDuringSimulatorAgentRun()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge(
            mauiContainsCycle: false,
            nativeButtonText: "Yes",
            nativePlatform: "ios");
        var session = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Yes",
            ["role"] = "button",
            ["visible"] = true
        });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session,
            bridge,
            "ansight_find_ui",
            RunRequestContext.CreateCorrelationId(),
            selector,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var capture = Assert.IsType<LiveUiTreeCapture>(result.Capture);
        Assert.Equal(
            [RemoteAppToolIds.MauiGetVisualTree, RemoteAppToolIds.UiGetVisualTree],
            bridge.CalledToolIds);
        Assert.Equal(
            [RemoteAppToolIds.MauiGetVisualTree, RemoteAppToolIds.UiGetVisualTree],
            result.AttemptedToolIds);
        Assert.Equal(RemoteAppToolIds.UiGetVisualTree, capture.ToolId);
        Assert.Single(LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry));
    }

    [Fact]
    public async Task LiveUiTreeCapture_RetainsHiddenNativeSelectorEvidenceForClassification()
    {
        var bridge = new CyclicMauiVisualTreeAppToolBridge(
            mauiContainsCycle: false,
            nativeButtonText: "Secret Garden",
            nativePlatform: "ios",
            nativeButtonVisible: false);
        var session = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Secret Garden",
            ["visible"] = true
        });

        var result = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session,
            bridge,
            "ansight_find_ui",
            RunRequestContext.CreateCorrelationId(),
            selector,
            CancellationToken.None);

        var capture = Assert.IsType<LiveUiTreeCapture>(result.Capture);
        Assert.Equal(RemoteAppToolIds.UiGetVisualTree, capture.ToolId);
        Assert.Empty(LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry));
        Assert.Single(LiveUiNodeQuery.Find(
            capture.Root,
            selector.WithoutVisibility(),
            capture.TypeRegistry));
    }

    [Fact]
    public void FindLiveUiTool_SeparatesHiddenDiagnosticMatchFromStrictVisibleMatches()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("Grid", "Button"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            }
        };
        var hiddenParent = new JsonObject
        {
            ["id"] = "search-results",
            ["typeId"] = 0,
            ["flags"] = 2,
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "dismiss-search",
                    ["typeId"] = 1,
                    ["automationId"] = "map-dismiss-search-button",
                    ["flags"] = 3,
                    ["bounds"] = CompactBounds(4, 716, 382, 41),
                    ["children"] = new JsonArray()
                }
            }
        };
        var registry = VisualTreeTypeRegistry.FromPayload(payload);
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "map-dismiss-search-button",
            ["visible"] = true
        });

        var selection = FindLiveUiTool.FindSemanticMatches(hiddenParent, registry, selector);

        Assert.Empty(selection.StrictMatches);
        var diagnosticMatch = Assert.Single(selection.ResolutionMatches);
        Assert.True(selection.VisibilityRelaxed);
        Assert.False(LiveUiNodeQuery.IsEffectivelyVisible(diagnosticMatch));
    }

    [Fact]
    public void LiveUiSelector_FindDefaultsToCaseInsensitiveSubstringMatching()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("Button")
        };
        var node = new JsonObject
        {
            ["id"] = "home-tab",
            ["typeId"] = 0,
            ["automationId"] = "home-tab-crags"
        };
        var match = new LiveUiNodeMatch(
            node,
            [],
            VisualTreeTypeRegistry.FromPayload(payload));

        var findSelector = LiveUiSelector.Parse(
            new JsonObject { ["automationId"] = "HOME" },
            exactByDefault: false);
        var actionSelector = LiveUiSelector.Parse(
            new JsonObject { ["automationId"] = "home" });
        var explicitExactFindSelector = LiveUiSelector.Parse(
            new JsonObject
            {
                ["automationId"] = "home",
                ["exact"] = true
            },
            exactByDefault: false);

        Assert.True(findSelector.Matches(match));
        Assert.False(actionSelector.Matches(match));
        Assert.False(explicitExactFindSelector.Matches(match));
    }

    [Fact]
    public void LiveUiSelector_ExactMatchCanonicalizesAndroidResourceIdsForTargetAndAncestor()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("androidx.recyclerview.widget.RecyclerView", "android.widget.TextView")
        };
        var ancestor = new JsonObject
        {
            ["typeId"] = 0,
            ["automationId"] = "com.example:id/map-areas-collection-view"
        };
        var candidate = new LiveUiNodeMatch(
            new JsonObject
            {
                ["typeId"] = 1,
                ["automationId"] = "com.example:id/area-result-title",
                ["text"] = "Siurana"
            },
            [ancestor],
            VisualTreeTypeRegistry.FromPayload(payload));
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "area-result-title",
            ["text"] = "Siurana",
            ["ancestorAutomationId"] = "map-areas-collection-view",
            ["exact"] = true
        });

        Assert.True(selector.Matches(candidate));
    }

    [Fact]
    public void LiveUiSelector_ExactMatchDoesNotEquateDifferentQualifiedAndroidPackages()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("android.widget.Button")
        };
        var candidate = new LiveUiNodeMatch(
            new JsonObject
            {
                ["typeId"] = 0,
                ["automationId"] = "com.example.one:id/save"
            },
            [],
            VisualTreeTypeRegistry.FromPayload(payload));
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "com.example.two:id/save",
            ["exact"] = true
        });

        Assert.False(selector.Matches(candidate));
    }

    [Fact]
    public void LiveUiSelector_FuzzyModeMatchesTypoAndReportsScore()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("StaticText")
        };
        var match = new LiveUiNodeMatch(
            new JsonObject
            {
                ["id"] = "area-siurana",
                ["typeId"] = 0,
                ["label"] = "Siurana"
            },
            [],
            VisualTreeTypeRegistry.FromPayload(payload));
        var selector = LiveUiSelector.Parse(
            new JsonObject
            {
                ["text"] = "Siuana",
                ["matchMode"] = "fuzzy"
            },
            exactByDefault: false);

        var evaluation = selector.Evaluate(match);

        Assert.Equal(LiveUiStringMatchMode.Fuzzy, selector.MatchMode);
        Assert.True(evaluation.IsMatch);
        Assert.InRange(evaluation.Score, 0.8, 0.99);
        Assert.Contains("edit-distance-1", evaluation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveUiSelector_FuzzyModeNormalizesDiacriticsSpacingAndPunctuation()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("StaticText")
        };
        var match = new LiveUiNodeMatch(
            new JsonObject
            {
                ["id"] = "area-el-pati",
                ["typeId"] = 0,
                ["label"] = "Área — El-Pati"
            },
            [],
            VisualTreeTypeRegistry.FromPayload(payload));
        var selector = LiveUiSelector.Parse(
            new JsonObject
            {
                ["text"] = "Area El Pati",
                ["matchMode"] = "fuzzy"
            },
            exactByDefault: false);

        var evaluation = selector.Evaluate(match);

        Assert.True(evaluation.IsMatch);
        Assert.Equal(1, evaluation.Score);
        Assert.Contains("normalized-exact", evaluation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveUiSelector_FuzzyModeDoesNotRelaxNodeIdOrShortText()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("StaticText")
        };
        var match = new LiveUiNodeMatch(
            new JsonObject
            {
                ["id"] = "node-1234",
                ["typeId"] = 0,
                ["label"] = "Map"
            },
            [],
            VisualTreeTypeRegistry.FromPayload(payload));
        var fuzzyNodeIdSelector = LiveUiSelector.Parse(
            new JsonObject
            {
                ["nodeId"] = "node-123",
                ["matchMode"] = "fuzzy"
            },
            exactByDefault: false);
        var shortTextSelector = LiveUiSelector.Parse(
            new JsonObject
            {
                ["text"] = "Mop",
                ["matchMode"] = "fuzzy"
            },
            exactByDefault: false);

        Assert.False(fuzzyNodeIdSelector.Matches(match));
        Assert.False(shortTextSelector.Matches(match));
    }

    [Fact]
    public void LiveUiSelector_ExplicitMatchModeOverridesLegacyExactFlag()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("Button")
        };
        var match = new LiveUiNodeMatch(
            new JsonObject
            {
                ["id"] = "home-tab",
                ["typeId"] = 0,
                ["automationId"] = "home-tab-crags"
            },
            [],
            VisualTreeTypeRegistry.FromPayload(payload));
        var selector = LiveUiSelector.Parse(
            new JsonObject
            {
                ["automationId"] = "home",
                ["exact"] = true,
                ["matchMode"] = "contains"
            },
            exactByDefault: false);

        Assert.Equal(LiveUiStringMatchMode.Contains, selector.MatchMode);
        Assert.True(selector.Matches(match));
    }

    [Fact]
    public void LiveUiNodeQuery_DecodesCompactAbsoluteBoundsAndStateFlags()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("Button"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            }
        };
        var node = new JsonObject
        {
            ["id"] = "continue",
            ["typeId"] = 0,
            ["flags"] = 1,
            ["bounds"] = new JsonArray(10, 20, 30, 40, 110, 220, 30, 40)
        };
        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);

        var bounds = Assert.IsType<LiveUiBounds>(LiveUiNodeQuery.ReadBounds(node));

        Assert.Equal(110, bounds.X);
        Assert.Equal(220, bounds.Y);
        Assert.Equal(30, bounds.Width);
        Assert.Equal(40, bounds.Height);
        Assert.True(LiveUiNodeQuery.ReadBoolean(node, "visible", fallback: false, typeRegistry));
        Assert.False(LiveUiNodeQuery.ReadBoolean(node, "enabled", fallback: true, typeRegistry));
    }

    [Fact]
    public void LiveUiNodeQuery_ExcludesOnlySdkOwnedOverlaySurface()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray(
                "android.widget.FrameLayout",
                "ai.ansight.runtime.AndroidUiEvidence$OverlaySurface",
                "com.example.OverlaySurface")
        };
        var root = new JsonObject
        {
            ["id"] = "root",
            ["typeId"] = 0,
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "ansight-overlay",
                    ["typeId"] = 1,
                    ["automationId"] = "ansight.overlay.surface",
                    ["role"] = "button",
                    ["supportedActions"] = new JsonArray("tap"),
                    ["children"] = new JsonArray()
                },
                new JsonObject
                {
                    ["id"] = "app-overlay",
                    ["typeId"] = 2,
                    ["automationId"] = "app-overlay-action",
                    ["role"] = "button",
                    ["supportedActions"] = new JsonArray("tap"),
                    ["children"] = new JsonArray()
                }
            }
        };
        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);

        var nodes = LiveUiNodeQuery.Enumerate(root, typeRegistry).ToArray();

        Assert.Equal(2, nodes.Length);
        Assert.DoesNotContain(nodes, candidate =>
            candidate.TypeRegistry.Resolve(candidate.Node) ==
            "ai.ansight.runtime.AndroidUiEvidence$OverlaySurface");
        Assert.Contains(nodes, candidate =>
            LiveUiNodeQuery.ReadAutomationId(candidate.Node) == "app-overlay-action");
    }

    [Fact]
    public void LiveUiSelector_ExactOcrTextMatchesPhraseBoundaries()
    {
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Secret Garden",
            ["exact"] = true,
            ["visible"] = true
        });

        Assert.True(selector.CanUseOcr);
        Assert.True(selector.MatchesOcrText("Secret Garden"));
        Assert.True(selector.MatchesOcrText("Secret Garden · 18 routes"));
        Assert.False(selector.MatchesOcrText("Secret Gardenia"));
    }

    [Fact]
    public void LiveUiSelector_ExplicitExactModeRequiresOcrEquality()
    {
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Secret Garden",
            ["matchMode"] = "exact",
            ["visible"] = true
        });

        Assert.True(selector.MatchesOcrText("Secret Garden"));
        Assert.False(selector.MatchesOcrText("Secret Garden · 18 routes"));
    }

    [Fact]
    public void LiveUiSelector_AppliesOnlyAnExplicitIndexAcrossEvidenceSources()
    {
        var defaultSelector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Save" });
        var indexedSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Save",
            ["index"] = 1
        });
        string[] matches = ["first", "second", "third"];

        Assert.False(defaultSelector.IndexSpecified);
        Assert.Equal(matches, defaultSelector.ApplyIndex(matches));
        Assert.True(indexedSelector.IndexSpecified);
        Assert.Equal(["second"], indexedSelector.ApplyIndex(matches));
        Assert.Equal("second", indexedSelector.Select(matches));
        Assert.True(indexedSelector.IsAvailable(matches.Length));
        Assert.False(indexedSelector.IsAvailable(1));
    }

    [Fact]
    public void FindLiveUiTool_ClassifiesRepresentedNodeBelowViewportAsOffscreen()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("Label"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            }
        };
        var root = new JsonObject
        {
            ["id"] = "secret-garden",
            ["typeId"] = 0,
            ["flags"] = 3,
            ["text"] = "Secret Garden",
            ["bounds"] = CompactBounds(20, 900, 300, 60)
        };
        var registry = VisualTreeTypeRegistry.FromPayload(payload);
        var matches = LiveUiNodeQuery.Find(
            root,
            LiveUiSelector.Parse(new JsonObject { ["text"] = "Secret Garden" }),
            registry);

        Assert.Equal(
            "offscreen",
            FindLiveUiTool.ResolveSemanticResolution(
                matches,
                new LiveUiBounds(0, 0, 390, 844)));
        Assert.Equal(
            "below",
            FindLiveUiTool.ResolveViewportRelation(
                LiveUiNodeQuery.ReadBounds(root),
                new LiveUiBounds(0, 0, 390, 844)));
    }

    [Fact]
    public void LiveUiTapPoint_AcceptsExactPointInsideTargetAndViewport()
    {
        var arguments = new JsonObject
        {
            ["screenX"] = 728.37,
            ["screenY"] = 612.90
        };

        var resolved = LiveUiActionTool.TryResolveTapPoint(
            arguments,
            new LiveUiBounds(0, -52, 1032, 1448),
            new LiveUiBounds(0, 0, 1032, 1376),
            out var point,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(728.37, point.X);
        Assert.Equal(612.90, point.Y);
    }

    [Fact]
    public void LiveUiTapPoint_TranslatesTargetLocalPointThroughNativeSurfaceBounds()
    {
        var arguments = new JsonObject
        {
            ["targetX"] = 530.1546630859375,
            ["targetY"] = 690.96044921875
        };

        var resolved = LiveUiActionTool.TryResolveTapPoint(
            arguments,
            new LiveUiBounds(0, -74.5, 1032, 1448),
            new LiveUiBounds(0, 0, 1032, 1376),
            out var point,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(530.1546630859375, point.X);
        Assert.Equal(616.46044921875, point.Y);
    }

    [Fact]
    public void LiveUiTapPoint_TranslatesRecordedNormalizedViewportPointWithoutASelector()
    {
        var resolved = LiveUiActionTool.TryResolveTapPoint(
            new JsonObject
            {
                ["normalizedX"] = 0.25,
                ["normalizedY"] = 0.75
            },
            new LiveUiBounds(0, 0, 1032, 1376),
            new LiveUiBounds(0, 0, 1032, 1376),
            out var point,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(258, point.X);
        Assert.Equal(1032, point.Y);
    }

    [Fact]
    public void LiveUiTapTarget_RejectsFullViewportNonActionableAccessibilityContainer()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("GenericElement")
        };
        var node = new JsonObject
        {
            ["typeId"] = 0,
            ["role"] = "view",
            ["automationId"] = "home-tab-explore",
            ["bounds"] = Bounds(0, 0, 1, 1),
            ["children"] = new JsonArray()
        };
        var registry = VisualTreeTypeRegistry.FromPayload(payload);
        var target = new LiveUiNodeMatch(node, [], registry);

        Assert.True(LiveUiActionTool.IsUnsafeImplicitTapTarget(
            target,
            new LiveUiBounds(0, 0, 1, 1),
            new LiveUiBounds(0, 0, 1, 1)));
    }

    [Fact]
    public void LiveUiTapTarget_AllowsExplicitlyActionableFullViewportSurface()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("MapSurface")
        };
        var node = new JsonObject
        {
            ["typeId"] = 0,
            ["role"] = "view",
            ["supportedActions"] = new JsonArray("tap"),
            ["bounds"] = Bounds(0, 0, 1, 1),
            ["children"] = new JsonArray()
        };
        var registry = VisualTreeTypeRegistry.FromPayload(payload);
        var target = new LiveUiNodeMatch(node, [], registry);

        Assert.False(LiveUiActionTool.IsUnsafeImplicitTapTarget(
            target,
            new LiveUiBounds(0, 0, 1, 1),
            new LiveUiBounds(0, 0, 1, 1)));
    }

    [Fact]
    public void LiveUiOcrSearchResult_ReturnsOnlyOneUniqueCurrentTapPoint()
    {
        var match = new JsonObject
        {
            ["text"] = "Paranoia",
            ["tapHint"] = new JsonObject
            {
                ["selector"] = new JsonObject
                {
                    ["normalizedX"] = 0.42,
                    ["normalizedY"] = 0.73
                }
            }
        };
        var result = new LiveUiOcrSearchResult(true, "test", null, [match], null);

        var resolved = result.TryGetUniqueTapPoint(out var point, out var resolvedMatch);

        Assert.True(resolved);
        Assert.Equal(0.42, point.X);
        Assert.Equal(0.73, point.Y);
        Assert.Same(match, resolvedMatch);
        Assert.False((result with { Matches = [match, match] }).TryGetUniqueTapPoint(out _, out _));
    }

    [Fact]
    public void LiveUiOcrSearchResult_UsesAnExplicitIndexForAmbiguousText()
    {
        static JsonObject Match(double normalizedX) => new()
        {
            ["tapHint"] = new JsonObject
            {
                ["selector"] = new JsonObject
                {
                    ["normalizedX"] = normalizedX,
                    ["normalizedY"] = 0.5
                }
            }
        };

        var first = Match(0.25);
        var second = Match(0.75);
        var result = new LiveUiOcrSearchResult(true, "test", null, [first, second], null);

        Assert.True(result.TryGetTapPoint(1, indexSpecified: true, out var point, out var selected));
        Assert.Equal(0.75, point.X);
        Assert.Same(second, selected);
        Assert.False(result.TryGetTapPoint(2, indexSpecified: true, out _, out _));
    }

    [Fact]
    public void LiveUiSwipePath_PreservesExactRecordedCoordinatesAndDuration()
    {
        var resolved = LiveUiActionTool.TryResolveSwipePath(
            new JsonObject
            {
                ["startNormalizedX"] = 0.17,
                ["startNormalizedY"] = 0.81,
                ["endNormalizedX"] = 0.64,
                ["endNormalizedY"] = 0.23,
                ["durationMs"] = 437
            },
            new LiveUiBounds(100, 100, 500, 600),
            new LiveUiBounds(0, 0, 1032, 1376),
            reverseOrientation: false,
            out var path,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(0.17, path.StartX);
        Assert.Equal(0.81, path.StartY);
        Assert.Equal(0.64, path.EndX);
        Assert.Equal(0.23, path.EndY);
        Assert.Equal(437, path.DurationMilliseconds);
    }

    [Fact]
    public void LiveUiSwipePath_RejectsAPartialRecordedPath()
    {
        var resolved = LiveUiActionTool.TryResolveSwipePath(
            new JsonObject
            {
                ["startNormalizedX"] = 0.17,
                ["startNormalizedY"] = 0.81
            },
            new LiveUiBounds(0, 0, 1032, 1376),
            new LiveUiBounds(0, 0, 1032, 1376),
            reverseOrientation: false,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("must be supplied together", error, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveUiSwipePath_UsesOrientationWhenStrictSchemaCoordinatesAreNull()
    {
        var arguments = JsonNode.Parse("""
            {
              "includeScreenshot": false,
              "nodeId": null,
              "automationId": null,
              "text": null,
              "role": null,
              "type": null,
              "ancestorAutomationId": null,
              "action": null,
              "visible": null,
              "enabled": null,
              "exact": null,
              "caseSensitive": null,
              "index": null,
              "startNormalizedX": null,
              "startNormalizedY": null,
              "endNormalizedX": null,
              "endNormalizedY": null,
              "orientation": "up",
              "direction": "up",
              "length": 0.4,
              "distance": null,
              "durationMs": null
            }
            """)!.AsObject();

        var resolved = LiveUiActionTool.TryResolveSwipePath(
            arguments,
            new LiveUiBounds(0, 0, 1000, 1000),
            new LiveUiBounds(0, 0, 1000, 1000),
            reverseOrientation: false,
            out var path,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(0.5, path.StartX, precision: 6);
        Assert.Equal(0.7, path.StartY, precision: 6);
        Assert.Equal(0.5, path.EndX, precision: 6);
        Assert.Equal(0.3, path.EndY, precision: 6);
        Assert.Equal(250, path.DurationMilliseconds);
    }

    [Theory]
    [InlineData("down", 0.3, 0.7)]
    [InlineData(null, 0.7, 0.3)]
    public void LiveUiSwipePath_NullCoordinatesPreserveLegacyAndDefaultDirection(
        string? direction,
        double expectedStartY,
        double expectedEndY)
    {
        var resolved = LiveUiActionTool.TryResolveSwipePath(
            new JsonObject
            {
                ["startNormalizedX"] = null,
                ["startNormalizedY"] = null,
                ["endNormalizedX"] = null,
                ["endNormalizedY"] = null,
                ["orientation"] = null,
                ["direction"] = direction,
                ["length"] = null,
                ["distance"] = null
            },
            new LiveUiBounds(0, 0, 1000, 1000),
            new LiveUiBounds(0, 0, 1000, 1000),
            reverseOrientation: false,
            out var path,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(expectedStartY, path.StartY, precision: 6);
        Assert.Equal(expectedEndY, path.EndY, precision: 6);
    }

    [Theory]
    [InlineData(0.5, null, null, null)]
    [InlineData(null, 0.8, 0.5, null)]
    [InlineData(0.5, 0.8, null, 0.2)]
    public void LiveUiSwipePath_RejectsRecordedCoordinatesMixedWithNull(
        double? startX,
        double? startY,
        double? endX,
        double? endY)
    {
        var resolved = LiveUiActionTool.TryResolveSwipePath(
            new JsonObject
            {
                ["startNormalizedX"] = startX,
                ["startNormalizedY"] = startY,
                ["endNormalizedX"] = endX,
                ["endNormalizedY"] = endY,
                ["orientation"] = "up"
            },
            new LiveUiBounds(0, 0, 1000, 1000),
            new LiveUiBounds(0, 0, 1000, 1000),
            reverseOrientation: false,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("must be supplied together", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void LiveUiSwipePath_RejectsInvalidRecordedCoordinateWithoutDirectionFallback(double endY)
    {
        var resolved = LiveUiActionTool.TryResolveSwipePath(
            new JsonObject
            {
                ["startNormalizedX"] = 0.5,
                ["startNormalizedY"] = 0.8,
                ["endNormalizedX"] = 0.5,
                ["endNormalizedY"] = endY,
                ["orientation"] = "up"
            },
            new LiveUiBounds(0, 0, 1000, 1000),
            new LiveUiBounds(0, 0, 1000, 1000),
            reverseOrientation: false,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("finite numbers from 0 through 1", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("up", false, 0.7, 0.3)]
    [InlineData("down", false, 0.3, 0.7)]
    [InlineData("up", true, 0.3, 0.7)]
    [InlineData("down", true, 0.7, 0.3)]
    public void LiveUiSwipePath_ScrollReversesFingerTravelForContentDirection(
        string orientation,
        bool reverseOrientation,
        double expectedStartY,
        double expectedEndY)
    {
        var resolved = LiveUiActionTool.TryResolveSwipePath(
            new JsonObject { ["orientation"] = orientation },
            new LiveUiBounds(0, 0, 1000, 1000),
            new LiveUiBounds(0, 0, 1000, 1000),
            reverseOrientation,
            out var path,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(expectedStartY, path.StartY, precision: 6);
        Assert.Equal(expectedEndY, path.EndY, precision: 6);
    }

    [Fact]
    public void LiveUiPinchPath_ResolvesScaleAroundViewportCenter()
    {
        var resolved = LiveUiActionTool.TryResolvePinchPath(
            new JsonObject
            {
                ["scale"] = 0.5,
                ["centerNormalizedX"] = 0.5,
                ["centerNormalizedY"] = 0.5,
                ["startDistance"] = 0.4,
                ["durationMs"] = 450
            },
            new LiveUiBounds(0, 0, 1000, 1000),
            new LiveUiBounds(0, 0, 1000, 1000),
            out var path,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(0.3, path.PrimaryStartX, precision: 6);
        Assert.Equal(0.7, path.SecondaryStartX, precision: 6);
        Assert.Equal(0.4, path.PrimaryEndX, precision: 6);
        Assert.Equal(0.6, path.SecondaryEndX, precision: 6);
        Assert.Equal(450, path.DurationMilliseconds);
    }

    [Fact]
    public void LiveUiPinchPath_RejectsContactsOutsideGestureBounds()
    {
        var resolved = LiveUiActionTool.TryResolvePinchPath(
            new JsonObject
            {
                ["scale"] = 2,
                ["centerNormalizedX"] = 0.1,
                ["centerNormalizedY"] = 0.5,
                ["startDistance"] = 0.3
            },
            new LiveUiBounds(0, 0, 1000, 1000),
            new LiveUiBounds(0, 0, 1000, 1000),
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("outside", error, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveUiSequence_ParsesRecordedActionsBeforeDelivery()
    {
        var resolved = LiveUiSequenceTool.TryParseActions(
            new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "tap",
                    ["normalizedX"] = 0.25,
                    ["normalizedY"] = 0.75
                },
                new JsonObject
                {
                    ["kind"] = "pinch",
                    ["scale"] = 0.5
                },
                new JsonObject
                {
                    ["kind"] = "swipe",
                    ["orientation"] = "E to W"
                }
            },
            new LiveUiBounds(0, 0, 1000, 1000),
            out var actions,
            out var error);

        Assert.True(resolved, error);
        Assert.Collection(
            actions,
            action => Assert.Equal(LiveUiSequenceActionKind.Tap, action.Kind),
            action => Assert.Equal(LiveUiSequenceActionKind.Pinch, action.Kind),
            action => Assert.Equal(LiveUiSequenceActionKind.Swipe, action.Kind));
    }

    [Fact]
    public void LiveUiActionResponse_InlinesFinalScreenshotWhenRequested()
    {
        var artifactPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(artifactPath, [1, 2, 3, 4]);
            var result = LiveUiActionResponse.Build(
                new JsonObject
                {
                    ["performed"] = true,
                    ["message"] = "done"
                },
                new ActionEvidenceCapture(
                    "after",
                    VisualTreeSnapshotId: null,
                    TreeHash: null,
                    ScreenshotFrameId: "frame-001",
                    ScreenshotHash: "hash",
                    Persisted: true,
                    Message: "captured",
                    ScreenshotArtifactPath: artifactPath,
                    ScreenshotFormat: "png"),
                includeScreenshot: true,
                isError: false);

            var content = Assert.IsType<JsonArray>(result.Payload?["content"]);
            Assert.Equal(2, content.Count);
            Assert.Equal("image", content[1]?["type"]?.GetValue<string>());
            Assert.Equal("image/png", content[1]?["mimeType"]?.GetValue<string>());
            Assert.Equal("AQIDBA==", content[1]?["data"]?.GetValue<string>());
        }
        finally
        {
            File.Delete(artifactPath);
        }
    }

    [Fact]
    public void LiveUiActionTargets_PreferOnScreenCurrentMauiPageOverInactiveDuplicate()
    {
        var payload = new JsonObject
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["rootScope"] = "window",
            ["types"] = new JsonArray("HomeTabbedPage", "AreaPage", "Border"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2,
                ["currentPage"] = 4,
                ["activePage"] = 8
            },
            ["currentPage"] = new JsonObject
            {
                ["id"] = "area-page",
                ["typeId"] = 1,
                ["label"] = "Secret Garden"
            }
        };
        var inactiveButton = new JsonObject
        {
            ["id"] = "inactive-guide",
            ["typeId"] = 2,
            ["automationId"] = "area-open3d-guide-button",
            ["flags"] = 3,
            ["bounds"] = CompactBounds(0, 0, 0, 0),
            ["children"] = new JsonArray()
        };
        var activeButton = new JsonObject
        {
            ["id"] = "active-guide",
            ["typeId"] = 2,
            ["automationId"] = "area-open3d-guide-button",
            ["flags"] = 3,
            ["bounds"] = CompactBounds(104, 697, 181, 44),
            ["children"] = new JsonArray()
        };
        var root = new JsonObject
        {
            ["id"] = "tabs",
            ["typeId"] = 0,
            ["flags"] = 3,
            ["bounds"] = CompactBounds(0, 0, 390, 844),
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "inactive-area-page",
                    ["typeId"] = 1,
                    ["flags"] = 3,
                    ["bounds"] = CompactBounds(0, 0, 0, 0),
                    ["children"] = new JsonArray(inactiveButton)
                },
                new JsonObject
                {
                    ["id"] = "area-page",
                    ["typeId"] = 1,
                    ["flags"] = 15,
                    ["bounds"] = CompactBounds(0, 0, 390, 844),
                    ["children"] = new JsonArray(activeButton)
                }
            }
        };
        payload["root"] = root;
        var registry = VisualTreeTypeRegistry.FromPayload(payload);
        var matches = LiveUiNodeQuery.Find(
            root,
            LiveUiSelector.Parse(new JsonObject
            {
                ["automationId"] = "area-open3d-guide-button"
            }),
            registry);

        var ranked = LiveUiNodeQuery.PrioritizeActionTargets(
            matches,
            payload,
            new LiveUiBounds(0, 0, 390, 844));

        Assert.Equal("active-guide", LiveUiNodeQuery.ReadString(ranked[0].Node, "id"));
        Assert.True(LiveUiMauiContext.IsInActivePage(ranked[0], payload));
    }

    [Fact]
    public void WaitVisibleSelection_AppliesIndexBeforeEffectiveVisibility()
    {
        var registry = VisualTreeTypeRegistry.FromPayload(new JsonObject
        {
            ["types"] = new JsonArray("Button")
        });
        var hidden = new LiveUiNodeMatch(
            new JsonObject { ["typeId"] = 0, ["visible"] = false },
            [],
            registry);
        var visible = new LiveUiNodeMatch(
            new JsonObject { ["typeId"] = 0, ["visible"] = true },
            [],
            registry);
        var matches = new[] { hidden, visible };

        var firstSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Save",
            ["index"] = 0
        });
        var secondSelector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "Save",
            ["index"] = 1
        });

        Assert.Empty(WaitForLiveUiTool.SelectVisibleMatches(matches, firstSelector));
        Assert.Same(visible, Assert.Single(WaitForLiveUiTool.SelectVisibleMatches(matches, secondSelector)));
    }

    [Fact]
    public void LiveSnapshotBoundsOptionRemovesBoundsRecursively()
    {
        var child = new JsonObject
        {
            ["id"] = "child",
            ["bounds"] = Bounds(10, 10, 20, 20),
            ["children"] = new JsonArray()
        };
        var root = new JsonObject
        {
            ["id"] = "root",
            ["bounds"] = Bounds(0, 0, 100, 100),
            ["children"] = new JsonArray(child)
        };

        GetLiveVisualTreeTool.RemoveNodeBounds(root);

        Assert.Null(root["bounds"]);
        Assert.Null(child["bounds"]);
    }

    [Fact]
    public void LiveUiScrollPath_StaysAboveMauiTabbedNavigationChrome()
    {
        var payload = new JsonObject
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["types"] = new JsonArray("HomeTabbedPage", "ScrollView"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2,
                ["currentPage"] = 4,
                ["activePage"] = 8
            }
        };
        var root = new JsonObject
        {
            ["id"] = "tabs",
            ["typeId"] = 0,
            ["flags"] = 3,
            ["bounds"] = CompactBounds(0, 0, 390, 844),
            ["children"] = new JsonArray()
        };
        var registry = VisualTreeTypeRegistry.FromPayload(payload);
        var viewport = new LiveUiBounds(0, 0, 390, 844);
        var gestureBounds = LiveUiMauiContext.ResolveGestureBounds(
            payload,
            root,
            registry,
            target: null,
            viewport,
            viewport);

        var resolved = LiveUiActionTool.TryResolveSwipePath(
            new JsonObject
            {
                ["orientation"] = "down",
                ["length"] = 0.6
            },
            gestureBounds,
            viewport,
            reverseOrientation: true,
            out var path,
            out var error);

        Assert.True(resolved, error);
        Assert.InRange(path.StartY, 0.08, 0.80);
        Assert.InRange(path.EndY, 0.08, 0.80);
        Assert.True(path.StartY > path.EndY);
    }

    [Fact]
    public void LiveUiMauiScrollPostcondition_ReportsUnexpectedPageChange()
    {
        var beforePayload = MauiPagePayload("area-page", "Secret Garden", "AreaPage");
        var afterPayload = MauiPagePayload("world-page", "World", "WorldPage");

        var message = LiveUiMauiContext.DescribeUnexpectedPageChange(
            beforePayload,
            VisualTreeTypeRegistry.FromPayload(beforePayload),
            afterPayload,
            VisualTreeTypeRegistry.FromPayload(afterPayload));

        Assert.NotNull(message);
        Assert.Contains("Secret Garden", message, StringComparison.Ordinal);
        Assert.Contains("World", message, StringComparison.Ordinal);
        Assert.Contains("navigation chrome", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(728.37, null)]
    [InlineData(null, 612.90)]
    [InlineData(1200d, 612.90)]
    [InlineData(728.37, 1400d)]
    public void LiveUiTapPoint_RejectsIncompleteOrOutOfBoundsPoint(double? screenX, double? screenY)
    {
        var arguments = new JsonObject();
        if (screenX.HasValue)
        {
            arguments["screenX"] = screenX.Value;
        }

        if (screenY.HasValue)
        {
            arguments["screenY"] = screenY.Value;
        }

        var resolved = LiveUiActionTool.TryResolveTapPoint(
            arguments,
            new LiveUiBounds(0, -52, 1032, 1448),
            new LiveUiBounds(0, 0, 1032, 1376),
            out _,
            out var error);

        Assert.False(resolved);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void LiveUiTapPoint_RejectsMixedAbsoluteAndTargetLocalCoordinates()
    {
        var resolved = LiveUiActionTool.TryResolveTapPoint(
            new JsonObject
            {
                ["screenX"] = 530,
                ["screenY"] = 616,
                ["targetX"] = 530,
                ["targetY"] = 690
            },
            new LiveUiBounds(0, -74.5, 1032, 1448),
            new LiveUiBounds(0, 0, 1032, 1376),
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("either", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualTreeObservation_ReturnsNestedSemanticNodesWithCompactBoundsInSourceCoordinates()
    {
        var visualTree = new JsonObject
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["platform"] = "ios",
            ["capturedAtUtc"] = DateTimeOffset.UtcNow,
            ["rootScope"] = "currentPage",
            ["types"] = new JsonArray("MapPage", "ExtendedSearchBar"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            },
            ["coordinateSpace"] = Bounds(0, 0, 1024, 768),
            ["root"] = new JsonObject
            {
                ["id"] = "map-page",
                ["typeId"] = 0,
                ["automationId"] = "MapPage",
                ["label"] = "Map",
                ["flags"] = 3,
                ["bounds"] = CompactBounds(0, 0, 1024, 768),
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "search",
                        ["typeId"] = 1,
                        ["automationId"] = "map-search-text-filter",
                        ["label"] = "Search...",
                        ["flags"] = 3,
                        ["bounds"] = CompactBounds(64, 26, 896, 64),
                        ["children"] = new JsonArray()
                    }
                }
            }
        };

        var original = visualTree.ToJsonString();
        var observation = VisualTreeObservation.Build(
            visualTree,
            "session-001",
            "com.example.app",
            RemoteAppToolIds.MauiGetVisualTree);

        Assert.NotNull(observation);
        Assert.Equal(original, visualTree.ToJsonString());
        Assert.Null(observation["nodes"]);
        var nodes = Assert.IsType<JsonArray>(observation["root"]?["children"]);
        var search = Assert.Single(nodes.OfType<JsonObject>(), node =>
            node["automationId"]?.GetValue<string>() == "map-search-text-filter");
        Assert.Equal("textbox", search["role"]?.GetValue<string>());
        Assert.Equal("search", search["id"]?.GetValue<string>());
        var bounds = Assert.IsType<JsonArray>(search["bounds"]);
        Assert.Equal(4, bounds.Count);
        Assert.Equal(64, bounds[0]?.GetValue<double>());
        Assert.Equal(26, bounds[1]?.GetValue<double>());
        Assert.Equal(896, bounds[2]?.GetValue<double>());
        Assert.Equal(64, bounds[3]?.GetValue<double>());
        Assert.Equal(1024, observation["viewport"]?["width"]?.GetValue<double>());
    }

    [Fact]
    public void LiveUiSelector_FindsStableTargetThroughSemanticAncestry()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("UIWindow", "UIView", "UIButton")
        };
        var root = new JsonObject
        {
            ["id"] = "root",
            ["typeId"] = 0,
            ["bounds"] = Bounds(0, 0, 393, 852),
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "modal",
                    ["automationId"] = "location.modal",
                    ["typeId"] = 1,
                    ["children"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "ok",
                            ["typeId"] = 2,
                            ["automationId"] = "location.modal.ok",
                            ["label"] = "Ok",
                            ["role"] = "button",
                            ["supportedActions"] = new JsonArray("tap"),
                            ["visible"] = true,
                            ["enabled"] = true,
                            ["bounds"] = Bounds(150, 600, 93, 44),
                            ["children"] = new JsonArray()
                        }
                    }
                }
            }
        };
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "location.modal.ok",
            ["role"] = "button",
            ["ancestorAutomationId"] = "location.modal",
            ["action"] = "tap"
        });

        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);
        var match = Assert.Single(LiveUiNodeQuery.Find(root, selector, typeRegistry));
        var result = LiveUiNodeQuery.ToResultJson(match);

        Assert.Equal("location.modal.ok", result["automationId"]?.GetValue<string>());
        Assert.Equal("Ok", result["text"]?.GetValue<string>());
        Assert.Equal("button", result["role"]?.GetValue<string>());
        Assert.Equal(2, result["depth"]?.GetValue<int>());
        Assert.Null(result["children"]);
    }

    [Fact]
    public void LiveUiSelector_RejectsVisibleChildInsideHiddenAncestor()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("Grid", "Button"),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2
            }
        };
        var root = new JsonObject
        {
            ["id"] = "hidden-overlay",
            ["typeId"] = 0,
            ["flags"] = 2,
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "stale-done",
                    ["typeId"] = 1,
                    ["automationId"] = "map-dismiss-search-button",
                    ["label"] = "Done",
                    ["flags"] = 3,
                    ["bounds"] = CompactBounds(4, 1254, 1024, 41),
                    ["children"] = new JsonArray()
                }
            }
        };
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "map-dismiss-search-button",
            ["visible"] = true,
            ["enabled"] = true
        });

        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);
        Assert.Empty(LiveUiNodeQuery.Find(root, selector, typeRegistry));

        var child = Assert.Single(LiveUiNodeQuery.Enumerate(root, typeRegistry).Skip(1));
        var result = LiveUiNodeQuery.ToResultJson(child);
        Assert.False(result["visible"]?.GetValue<bool>());
        Assert.True(result["enabled"]?.GetValue<bool>());

        var selectorWithoutVisibility = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "map-dismiss-search-button"
        });
        var rawMatches = LiveUiNodeQuery.Find(root, selectorWithoutVisibility, typeRegistry);
        Assert.Single(rawMatches);
        Assert.Empty(WaitForLiveUiTool.FilterVisibleMatches(rawMatches));
    }

    [Fact]
    public void LiveUiSelector_DeduplicatesSameNodeAcrossWindowBranches()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("Window", "AccountPage")
        };
        var firstAccountPage = new JsonObject
        {
            ["id"] = "account-page-001",
            ["typeId"] = 1,
            ["automationId"] = "AccountPage",
            ["visible"] = true,
            ["children"] = new JsonArray()
        };
        var root = new JsonObject
        {
            ["id"] = "window",
            ["typeId"] = 0,
            ["children"] = new JsonArray(
                firstAccountPage,
                firstAccountPage.DeepClone())
        };
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "AccountPage",
            ["visible"] = true
        });

        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);
        var match = Assert.Single(LiveUiNodeQuery.Find(root, selector, typeRegistry));

        Assert.Equal("account-page-001", LiveUiNodeQuery.ReadString(match.Node, "id"));
    }

    [Fact]
    public void LiveUiSelector_InfersRoleAndActionsFromRegisteredType()
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray("UIButton")
        };
        var node = new JsonObject
        {
            ["id"] = "button",
            ["typeId"] = 0,
            ["label"] = "Continue",
            ["visible"] = true,
            ["enabled"] = true,
            ["children"] = new JsonArray()
        };
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["text"] = "continue",
            ["role"] = "button",
            ["action"] = "tap"
        });

        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);
        Assert.Single(LiveUiNodeQuery.Find(node, selector, typeRegistry));
        Assert.Equal(["tap"], LiveUiNodeQuery.ReadSupportedActions(node, typeRegistry));
    }

    [Fact]
    public void ScreenshotDifferenceCalculator_ReportsThresholdedChangedPixels()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ansight-screenshot-diff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var baselinePath = Path.Combine(directory, "baseline.png");
        var currentPath = Path.Combine(directory, "current.png");
        try
        {
            WriteBitmap(baselinePath, SKColors.Black, changedPixel: null);
            WriteBitmap(currentPath, SKColors.Black, changedPixel: SKColors.White);

            var difference = ScreenshotDifferenceCalculator.Compare(
                baselinePath,
                currentPath,
                maxWidth: 2,
                pixelThreshold: 16);

            Assert.Equal(1, difference.DifferentPixelCount);
            Assert.Equal(4, difference.TotalPixelCount);
            Assert.Equal(0.25, difference.DifferenceRatio);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SessionTimelineBuilder_CorrelatesUiActionEvidence()
    {
        var startedAtUtc = DateTimeOffset.Parse("2026-08-10T01:00:00Z");
        var session = new AppSessionSnapshot
        {
            SessionId = "action-session",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = startedAtUtc,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = startedAtUtc.AddSeconds(2),
            IsHistorical = false,
            Images =
            [
                Image("before-frame", startedAtUtc),
                Image("after-frame", startedAtUtc.AddSeconds(1))
            ],
            VisualTreeSnapshots =
            [
                ActionTree("before-tree", "before-frame", "before", startedAtUtc),
                ActionTree("after-tree", "after-frame", "after", startedAtUtc.AddSeconds(1))
            ],
            Logs =
            [
                new LogEntry(startedAtUtc, "Starting tap")
                {
                    EventId = "ui-action-001",
                    Source = "Ansight UI Automation"
                }
            ],
            MetricChannels = [],
            Metrics = []
        };

        var events = SessionTimelineBuilder.BuildEvents(session, startUtc: null, endUtc: null);
        var actionEvent = Assert.Single(
            events,
            item => item.Payload["category"]?.GetValue<string>() == "uiAction");
        var details = Assert.IsType<JsonObject>(actionEvent.Payload["details"]);

        Assert.Equal("ui-action-001", details["actionId"]?.GetValue<string>());
        Assert.Equal("before-tree", details["beforeVisualTreeSnapshotId"]?.GetValue<string>());
        Assert.Equal("after-tree", details["afterVisualTreeSnapshotId"]?.GetValue<string>());
        Assert.Equal("before-frame", details["beforeScreenshotFrameId"]?.GetValue<string>());
        Assert.Equal("after-frame", details["afterScreenshotFrameId"]?.GetValue<string>());
    }

    [Fact]
    public void SessionTimelineCursor_RoundTripsPageOffset()
    {
        var cursor = GetSessionTimelineTool.EncodeCursor(10_000);

        var valid = GetSessionTimelineTool.TryReadPageOffset(
            new JsonObject { ["cursor"] = cursor },
            out var offset,
            out var errorMessage);

        Assert.True(valid, errorMessage);
        Assert.Equal(10_000, offset);
    }

    private static JsonObject CreateCatalog(params string[] toolIds)
    {
        var tools = new JsonArray();
        foreach (var toolId in toolIds)
        {
            tools.Add(new JsonObject
            {
                ["id"] = toolId
            });
        }

        return new JsonObject
        {
            ["tools"] = tools
        };
    }

    private static JsonObject Bounds(double x, double y, double width, double height)
        => new()
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height
        };

    private static JsonArray CompactBounds(double x, double y, double width, double height)
        => new(x, y, width, height, x, y, width, height);

    private sealed class RecordingVisualTreeAppToolBridge : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public string? LastToolId { get; private set; }

        public JsonObject? LastArguments { get; private set; }

        public IReadOnlyList<string> GetConnectedSessionIds()
            => ["session-001"];

        public bool IsSessionConnected(string sessionId)
            => string.Equals(sessionId, "session-001", StringComparison.Ordinal);

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Failure("Not supported by this test bridge.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            return Task.FromResult(AppToolBridgeResponse.FromSuccess(
                "Catalog returned.",
                new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.CatalogType,
                    Id = "catalog-001",
                    SessionId = sessionId,
                    Payload = CreateCatalog(RemoteAppToolIds.MauiGetVisualTree)
                }));
        }

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            LastToolId = toolId;
            LastArguments = arguments?.DeepClone() as JsonObject;
            return Task.FromResult(AppToolBridgeResponse.FromSuccess(
                "Visual tree returned.",
                new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.ResultType,
                    Id = "result-001",
                    SessionId = sessionId,
                    Payload = new JsonObject
                    {
                        ["toolId"] = toolId,
                        ["success"] = true,
                        ["result"] = new JsonObject
                        {
                            ["format"] = "ansight.maui.visual-tree.compact.v2",
                            ["capturedAtUtc"] = DateTimeOffset.UtcNow,
                            ["types"] = new JsonArray("Microsoft.Maui.Controls.ContentPage"),
                            ["coordinateSpace"] = Bounds(0, 0, 1024, 768),
                            ["root"] = new JsonObject
                            {
                                ["id"] = "page-001",
                                ["typeId"] = 0,
                                ["bounds"] = CompactBounds(0, 0, 1024, 768),
                                ["children"] = new JsonArray()
                            }
                        }
                    }
                }));
        }
    }

    private sealed class CyclicMauiVisualTreeAppToolBridge : IAppToolBridge
    {
        private readonly bool mauiContainsCycle;
        private readonly string nativeButtonText;
        private readonly string nativePlatform;
        private readonly bool nativeButtonVisible;

        public CyclicMauiVisualTreeAppToolBridge(
            bool mauiContainsCycle = true,
            string nativeButtonText = "Sign out",
            string nativePlatform = "android",
            bool nativeButtonVisible = true)
        {
            this.mauiContainsCycle = mauiContainsCycle;
            this.nativeButtonText = nativeButtonText;
            this.nativePlatform = nativePlatform;
            this.nativeButtonVisible = nativeButtonVisible;
        }

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public List<string> CalledToolIds { get; } = [];

        public IReadOnlyList<string> GetConnectedSessionIds()
            => ["session-001"];

        public bool IsSessionConnected(string sessionId)
            => string.Equals(sessionId, "session-001", StringComparison.Ordinal);

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Failure("Not supported by this test bridge.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            return Task.FromResult(AppToolBridgeResponse.FromSuccess(
                "Catalog returned.",
                new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.CatalogType,
                    Id = "catalog-001",
                    SessionId = sessionId,
                    Payload = CreateCatalog(
                        RemoteAppToolIds.MauiGetVisualTree,
                        RemoteAppToolIds.UiGetVisualTree)
                }));
        }

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            CalledToolIds.Add(toolId);
            var result = string.Equals(toolId, RemoteAppToolIds.MauiGetVisualTree, StringComparison.Ordinal)
                ? CreateMauiTree()
                : CreateNativeTree();
            return Task.FromResult(AppToolBridgeResponse.FromSuccess(
                "Visual tree returned.",
                new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.ResultType,
                    Id = $"result-{CalledToolIds.Count}",
                    SessionId = sessionId,
                    Payload = new JsonObject
                    {
                        ["toolId"] = toolId,
                        ["success"] = true,
                        ["result"] = result
                    }
                }));
        }

        private JsonObject CreateMauiTree()
            => new()
            {
                ["format"] = "ansight.maui.visual-tree.compact.v2",
                ["capturedAtUtc"] = DateTimeOffset.UtcNow,
                ["types"] = new JsonArray("NavigationPage", "AccountPage"),
                ["flagBits"] = new JsonObject
                {
                    ["visible"] = 1,
                    ["enabled"] = 2,
                    ["cycle"] = 16
                },
                ["coordinateSpace"] = Bounds(0, 0, 1024, 768),
                ["root"] = new JsonObject
                {
                    ["id"] = "navigation-root",
                    ["typeId"] = 0,
                    ["flags"] = 3,
                    ["bounds"] = CompactBounds(0, 0, 1024, 768),
                    ["children"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "account-page",
                            ["typeId"] = 1,
                            ["automationId"] = "AccountPage",
                            ["flags"] = mauiContainsCycle ? 19 : 3,
                            ["bounds"] = CompactBounds(0, 0, 1024, 768),
                            ["children"] = new JsonArray()
                        }
                    }
                }
            };

        private JsonObject CreateNativeTree()
            => new()
            {
                ["format"] = "ansight.native.visual-tree.compact.v2",
                ["platform"] = nativePlatform,
                ["capturedAtUtc"] = DateTimeOffset.UtcNow,
                ["types"] = nativePlatform == "ios"
                    ? new JsonArray("XCUIElementTypeApplication", "XCUIElementTypeWindow", "XCUIElementTypeButton")
                    : new JsonArray("android.view.WindowRoots", "android.view.View", "android.widget.Button"),
                ["coordinateSpace"] = Bounds(0, 0, 1080, 2400),
                ["root"] = new JsonObject
                {
                    ["id"] = "windows",
                    ["typeId"] = 0,
                    ["visible"] = true,
                    ["enabled"] = true,
                    ["bounds"] = CompactBounds(0, 0, 1080, 2400),
                    ["children"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "activity-window",
                            ["typeId"] = 1,
                            ["visible"] = true,
                            ["enabled"] = true,
                            ["bounds"] = CompactBounds(0, 0, 1080, 2400),
                            ["children"] = new JsonArray()
                        },
                        new JsonObject
                        {
                            ["id"] = "dialog-window",
                            ["typeId"] = 1,
                            ["visible"] = true,
                            ["enabled"] = true,
                            ["bounds"] = CompactBounds(70, 900, 940, 500),
                            ["children"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "signout-button",
                                    ["typeId"] = 2,
                                    ["automationId"] = "account-signout-button",
                                    ["label"] = nativeButtonText,
                                    ["role"] = "button",
                                    ["visible"] = nativeButtonVisible,
                                    ["enabled"] = true,
                                    ["bounds"] = CompactBounds(720, 1240, 220, 100),
                                    ["children"] = new JsonArray()
                                }
                            }
                        }
                    }
                }
            };
    }

    private sealed class StaticAccessibilityDriver(JsonObject payload) : IUiAccessibilityDriver
    {
        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(
            UiAccessibilityRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new UiAccessibilityResult(
                true,
                "test-device-accessibility",
                "Captured.",
                payload.DeepClone().AsObject()));
    }

    private static AppSessionSnapshot CreateLiveSession()
        => new()
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            MetricChannels = [],
            Metrics = []
        };

    private static JsonObject CreateDeviceAccessibilityTree()
        => DeviceAccessibilityTreeNormalizer.NormalizeIos(
            """
            <AppiumAUT>
              <XCUIElementTypeApplication type="XCUIElementTypeApplication" name="Example" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="crags-tab" label="Crags" enabled="true" visible="true" x="100" y="720" width="100" height="50" />
              </XCUIElementTypeApplication>
            </AppiumAUT>
            """,
            viewportWidth: 400,
            viewportHeight: 800,
            maxNodes: 40,
            maxDepth: 16);

    private static JsonObject CreateModalDeviceAccessibilityTree()
        => DeviceAccessibilityTreeNormalizer.NormalizeIos(
            """
            <AppiumAUT>
              <XCUIElementTypeApplication type="XCUIElementTypeApplication" name="Example" enabled="true" visible="true" x="0" y="0" width="400" height="800">
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="pending-area-edits-close-button" enabled="true" visible="true" x="12" y="56" width="44" height="44" />
                <XCUIElementTypeStaticText type="XCUIElementTypeStaticText" label="Pending Edits &amp; Uploads" enabled="true" visible="true" x="56" y="56" width="288" height="44" />
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="pending-area-edits-refresh-button" enabled="true" visible="true" x="344" y="56" width="44" height="44" />
                <XCUIElementTypeButton type="XCUIElementTypeButton" name="header-back-button" enabled="true" visible="true" x="8" y="44" width="48" height="48" />
              </XCUIElementTypeApplication>
            </AppiumAUT>
            """,
            viewportWidth: 400,
            viewportHeight: 800,
            maxNodes: 40,
            maxDepth: 16);

    private static void WriteBitmap(string path, SKColor background, SKColor? changedPixel)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(background);
        if (changedPixel.HasValue)
        {
            bitmap.SetPixel(0, 0, changedPixel.Value);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static JsonObject MauiPagePayload(string pageId, string label, string pageType)
        => new()
        {
            ["format"] = "ansight.maui.visual-tree.compact.v2",
            ["types"] = new JsonArray(pageType),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = 1,
                ["enabled"] = 2,
                ["currentPage"] = 4,
                ["activePage"] = 8
            },
            ["currentPage"] = new JsonObject
            {
                ["id"] = pageId,
                ["typeId"] = 0,
                ["label"] = label,
                ["flags"] = 15
            },
            ["root"] = new JsonObject
            {
                ["id"] = pageId,
                ["typeId"] = 0,
                ["label"] = label,
                ["flags"] = 15,
                ["children"] = new JsonArray()
            }
        };

    private static SessionImageFrame Image(string frameId, DateTimeOffset capturedAtUtc)
        => new()
        {
            FrameId = frameId,
            CapturedAtUtc = capturedAtUtc,
            Format = "png",
            Width = 100,
            Height = 200,
            Quality = 100,
            ByteCount = 4
        };

    private static SessionVisualTreeSnapshot ActionTree(
        string snapshotId,
        string frameId,
        string phase,
        DateTimeOffset capturedAtUtc)
        => new()
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            Source = "ansight.uiAutomation",
            NodeCount = 1,
            ActionId = "ui-action-001",
            ActionCapability = "ui.tap",
            EvidencePhase = phase,
            TreeHash = $"{phase}-tree-hash",
            ScreenshotFrameId = frameId,
            ScreenshotCapturedAtUtc = capturedAtUtc,
            ScreenshotHash = $"{phase}-screenshot-hash",
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = "root",
                    ["children"] = new JsonArray()
                }
            }
        };
}
