using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Tests.AppGraphs;

public sealed class AppGraphPlannerTests
{
    [Fact]
    public void ExplorationDefaultsUseLongRunningAgentBudgets()
    {
        Assert.Equal(1_024, AppGraphCommands.DefaultExploreMaximumActions);
        Assert.Equal(1_024, AppGraphCommands.MaximumExploreMaximumActions);
        Assert.Equal(1_023, AppGraphCommands.DefaultExploreMaximumTurns);
        Assert.Equal(1_024, AppGraphCommands.DefaultExploreMaximumRoundTrips);
        Assert.Equal(4_000, AppGraphCommands.DefaultExploreMaximumToolCalls);
        Assert.Equal(128_000, AppGraphCommands.ExploreMaximumInstructionCharacters);
        Assert.Equal(32_000, AppGraphCommands.ExploreMaximumModelOutputTokens);
    }

    [Fact]
    public void ExplorationInstructionRequiresExhaustiveBranchAndScrollCoverage()
    {
        var instruction = AppGraphExplorationCompiler.BuildInstruction(
            detail: null,
            graphName: "Default",
            maximumActions: AppGraphCommands.DefaultExploreMaximumActions,
            focus: null);

        Assert.Contains("Exhaustively map every safely reachable destination", instruction, StringComparison.Ordinal);
        Assert.Contains("Use a breadth-first traversal", instruction, StringComparison.Ordinal);
        Assert.Contains("Scroll it in bounded increments", instruction, StringComparison.Ordinal);
        Assert.Contains("App Graph exploration is semantic-only", instruction, StringComparison.Ordinal);
        Assert.Contains("never request screenshot OCR or a rendered-screen content scan", instruction, StringComparison.Ordinal);
        Assert.Contains("never call it with only", instruction, StringComparison.Ordinal);
        Assert.Contains("never exceed two scroll attempts", instruction, StringComparison.Ordinal);
        Assert.Contains("at most two successful uses of the same stable automation ID", instruction, StringComparison.Ordinal);
        Assert.Contains("selector-free bounded viewport", instruction, StringComparison.Ordinal);
        Assert.Contains("focused semantic match", instruction, StringComparison.Ordinal);
        Assert.Contains("call report_app_graph_progress", instruction, StringComparison.Ordinal);
        Assert.Contains("keep coverage counts cumulative", instruction, StringComparison.Ordinal);
        Assert.Contains("do not finish after a representative sample", instruction, StringComparison.Ordinal);
        Assert.Contains("up to 1024 safe reversible navigation actions", instruction, StringComparison.Ordinal);
        Assert.Contains("Graph 'Default' has no existing version", instruction, StringComparison.Ordinal);
        Assert.Contains("\"schema\":\"ansight.app-graph/v1\"", instruction, StringComparison.Ordinal);
        Assert.Contains("no safe unexplored branch remains", instruction, StringComparison.Ordinal);
        Assert.Contains("Emit only transitions whose interacted element has an observed stable automation ID", instruction, StringComparison.Ordinal);
        Assert.Contains("human-facing app surface", instruction, StringComparison.Ordinal);
        Assert.Contains("do not discover or call repository tasks or arbitrary app-owned tools", instruction, StringComparison.Ordinal);
        Assert.Contains("call ansight_get_live_navigation_structure once", instruction, StringComparison.Ordinal);
        Assert.Contains("record every tab destination and the currently selected tab before", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("app-published navigation tools", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Map useful navigation choices", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("$FOCUS_CONTEXT$", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("$MAXIMUM_ACTIONS$", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("$GRAPH_CONTEXT$", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("$DEFINITION$", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void AppGraphTransitionInstructionSubstitutesDynamicContent()
    {
        var instruction = AppGraphTextResource.RenderSection(
            AppGraphTextResource.CatalogFileName,
            "transition-instruction",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MECHANISM"] = "ui_action",
                ["CONFIGURATION"] = "{\"action\":\"tap\"}",
                ["FROM_STATE"] = "Map",
                ["TO_STATE"] = "Details",
                ["SEMANTIC_MEANING"] = "Open details",
                ["AUTOMATION_ID_SECTION"] = "Interacted element automation ID: open-details. ",
                ["COMPLETION_REQUIREMENT"] = "Confirm Details is visible."
            });

        Assert.Contains("Mechanism: ui_action", instruction, StringComparison.Ordinal);
        Assert.Contains("Map -> Details (Open details)", instruction, StringComparison.Ordinal);
        Assert.Contains("automation ID: open-details", instruction, StringComparison.Ordinal);
        Assert.Contains("Confirm Details is visible", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("$MECHANISM$", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("$COMPLETION_REQUIREMENT$", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorationCompilerBuildsDestinationDirectoryAndElementActionEvidence()
    {
        var summary = """
            {
              "schema": "ansight.app-graph-exploration/v1",
              "summary": "Opened area details from the map.",
              "confidence": 0.84,
              "destinations": [
                { "id": "map", "kind": "screen", "name": "Map", "synonyms": ["Map browser"], "purpose": "Browse climbing areas.", "description": "The map is visible." },
                { "id": "details", "kind": "dialog", "name": "Area details", "synonyms": ["Area card"], "purpose": "Inspect the selected area.", "description": "The area card is visible." }
              ],
              "transitions": [
                {
                  "id": "open details",
                  "from": "map",
                  "to": "details",
                  "action": { "automationId": "area-details-button", "semanticMeaning": "Open the selected area's details" },
                  "parameters": ["area"],
                  "preconditions": ["An area is selectable"],
                  "postconditions": ["Area details are visible"],
                  "binding": {
                    "mechanism": "ui_action",
                    "configuration": { "action": "tap", "selector": { "automationId": "area-details-button" } },
                    "confidence": 0.8
                  }
                }
              ]
            }
            """;

        var compiled = AppGraphExplorationCompiler.TryCompile(summary, out var candidate, out var error);

        Assert.True(compiled, error);
        Assert.NotNull(candidate);
        Assert.Equal(2, candidate.DestinationCount);
        Assert.Equal(1, candidate.ElementActionCount);
        Assert.Equal("screen_map", candidate.Definition["nodes"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("screen", candidate.Definition["nodes"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("Map browser", candidate.Definition["nodes"]![0]!["synonyms"]![0]!.GetValue<string>());
        Assert.Equal("edge_open_details", candidate.Definition["edges"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("area-details-button", candidate.Definition["edges"]![0]!["action"]!["automationId"]!.GetValue<string>());
        Assert.Single(candidate.Evidence["bindingCandidates"]!.AsArray());
    }

    [Fact]
    public void ExplorationCompilerRejectsTransitionToUnknownState()
    {
        var compiled = AppGraphExplorationCompiler.TryCompile(
            """
            {
              "schema": "ansight.app-graph-exploration/v1",
              "destinations": [{ "id": "home", "kind": "screen", "name": "Home", "synonyms": [], "purpose": "Start navigation." }],
              "transitions": [{ "id": "open", "from": "home", "to": "missing", "action": { "automationId": "open-button", "semanticMeaning": "Open" } }]
            }
            """,
            out _,
            out var error);

        Assert.False(compiled);
        Assert.Contains("unknown resulting destination", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorationCompilerPreservesHostChildrenAndInitiallySelectedTabs()
    {
        var compiled = AppGraphExplorationCompiler.TryCompile(
            """
            {
              "schema": "ansight.app-graph-exploration/v1",
              "destinations": [
                { "id": "main", "kind": "screen", "name": "Main navigation", "synonyms": [], "purpose": "Navigate the app." },
                { "id": "home", "kind": "screen", "name": "Home", "synonyms": [], "purpose": "Use Home." },
                { "id": "profile", "kind": "screen", "name": "Profile", "synonyms": [], "purpose": "Use Profile." },
                { "id": "overview", "kind": "state", "name": "Overview", "parentScreen": "Profile", "synonyms": [], "purpose": "Review profile information." },
                { "id": "activity", "kind": "state", "name": "Activity", "parentScreen": "Profile", "synonyms": [], "purpose": "Review profile activity." }
              ],
              "navigationHosts": [
                { "id": "main-host", "kind": "bottom_tabs", "name": "Main navigation", "destinationId": "main", "activeChildDestinationId": "home", "childDestinationIds": ["home", "profile"], "framework": "react-native", "technology": { "framework": "react-native", "kind": "react_navigation_bottom_tabs", "navigationToolId": "react.get_navigation_state", "structureFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, "confidence": 0.95 }
              ],
              "tabGroups": [
                { "id": "profile-tabs", "parentDestinationId": "profile", "selectedDestinationId": "overview", "tabDestinationIds": ["overview", "activity"], "technology": { "framework": "react-native", "kind": "react_navigation_material_top_tabs", "navigationToolId": "react.get_navigation_state", "structureFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, "confidence": 0.92 }
              ],
              "transitions": [
                { "id": "open-profile", "from": "home", "to": "profile", "action": { "automationId": "profile-tab", "semanticMeaning": "Open Profile" } }
              ]
            }
            """,
            out var candidate,
            out var error);

        Assert.True(compiled, error);
        var definition = Assert.IsType<JsonObject>(candidate?.Definition);
        var nodes = Assert.IsType<JsonArray>(definition["nodes"]);
        Assert.Equal(
            "Main navigation",
            nodes.OfType<JsonObject>().Single(node => node["name"]?.GetValue<string>() == "Profile")["parentScreen"]?.GetValue<string>());
        Assert.Equal(
            "Profile",
            nodes.OfType<JsonObject>().Single(node => node["name"]?.GetValue<string>() == "Overview")["parentScreen"]?.GetValue<string>());
        var navigationHost = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(definition["navigationHosts"])));
        var tabGroup = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(definition["tabGroups"])));
        Assert.Equal("react_navigation_bottom_tabs", navigationHost["technology"]?["kind"]?.GetValue<string>());
        Assert.Equal("react_navigation_material_top_tabs", tabGroup["technology"]?["kind"]?.GetValue<string>());
    }

    [Fact]
    public void ExplorationCompilerRejectsScreenParentWithoutNavigationHost()
    {
        var compiled = AppGraphExplorationCompiler.TryCompile(
            """
            {
              "schema": "ansight.app-graph-exploration/v1",
              "destinations": [
                { "id": "home", "kind": "screen", "name": "Home", "parentScreen": "Missing host", "synonyms": [], "purpose": "Use Home." }
              ],
              "transitions": []
            }
            """,
            out _,
            out var error);

        Assert.False(compiled);
        Assert.Contains("declared as a child of that navigation host", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerAcceptsScreenParentDeclaredByNavigationHost()
    {
        var graphId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var detail = new CloudAppGraphDetail(
            new CloudAppGraphSummary
            {
                Id = graphId,
                TeamId = Guid.NewGuid(),
                TeamAppId = Guid.NewGuid(),
                Name = "Main navigation"
            },
            new CloudAppGraphVersion
            {
                Id = versionId,
                AppGraphId = graphId,
                Definition = JsonNode.Parse("""
                    {
                      "schema": "ansight.app-graph/v1",
                      "nodes": [
                        { "id": "main", "kind": "screen", "name": "Main navigation", "synonyms": [], "purpose": "Host navigation." },
                        { "id": "home", "kind": "screen", "name": "Home", "parentScreen": "Main navigation", "synonyms": [], "purpose": "Use Home." },
                        { "id": "profile", "kind": "screen", "name": "Profile", "parentScreen": "Main navigation", "synonyms": [], "purpose": "Use Profile." }
                      ],
                      "navigationHosts": [
                        { "id": "main-host", "kind": "bottom_tabs", "name": "Main navigation", "destinationId": "main", "activeChildDestinationId": "home", "childDestinationIds": ["home", "profile"], "framework": "maui" }
                      ],
                      "edges": []
                    }
                    """)!.AsObject()
            },
            []);

        var validation = AppGraphPlanner.Validate(detail);

        Assert.DoesNotContain(validation.Errors, error => error.Contains("may only identify a containing screen", StringComparison.Ordinal));
        Assert.DoesNotContain(validation.Errors, error => error.Contains("declares it as a child", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplorationCompilerRejectsStatesArrayShape()
    {
        var compiled = AppGraphExplorationCompiler.TryCompile(
            """
            {
              "schema": "ansight.app-graph-exploration/v1",
              "states": [{ "id": "home", "label": "Home" }],
              "transitions": []
            }
            """,
            out _,
            out var error);

        Assert.False(compiled);
        Assert.Contains("observed destinations", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCompilesDestinationPathWithOrderedFallbacks()
    {
        var detail = CreateDetail(
        [
            CreateBinding("open", 1, "ui_action"),
            CreateBinding("open", 0, "app_tool")
        ]);

        var plan = AppGraphPlanner.Build(detail, "3D guide ready");

        Assert.Empty(plan.Errors);
        var step = Assert.Single(plan.Steps);
        Assert.Equal("open", step.Edge.Id);
        Assert.Equal(["app_tool", "ui_action"], step.Bindings.Select(binding => binding.Mechanism));
    }

    [Fact]
    public void BuildUsesExplicitEntryStateWhenNavigationCanReturnToIt()
    {
        var graphId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var detail = new CloudAppGraphDetail(
            new CloudAppGraphSummary
            {
                Id = graphId,
                TeamId = Guid.NewGuid(),
                TeamAppId = Guid.NewGuid(),
                Name = "Cyclic navigation",
                Intent = "Navigate between home and settings",
                Status = "validated",
                Version = 1,
                CurrentVersionId = versionId
            },
            new CloudAppGraphVersion
            {
                Id = versionId,
                AppGraphId = graphId,
                VersionNumber = 1,
                Status = "validated",
                Definition = JsonNode.Parse("""
                    {
                      "schema": "ansight.app-graph/v1",
                      "nodes": [
                        { "id": "home", "kind": "screen", "name": "Home Menu", "synonyms": [], "purpose": "Start navigation.", "isEntry": true },
                        { "id": "settings", "kind": "screen", "name": "Settings", "synonyms": [], "purpose": "Configure the app." }
                      ],
                      "edges": [
                        { "id": "open-settings", "from": "home", "to": "settings", "action": { "automationId": "open-settings-button", "semanticMeaning": "Open Settings" }, "postconditions": ["Settings is visible"] },
                        { "id": "open-home", "from": "settings", "to": "home", "action": { "automationId": "open-home-button", "semanticMeaning": "Open Home Menu" }, "postconditions": ["Home Menu is visible"] }
                      ]
                    }
                    """)!.AsObject()
            },
            [
                CreateBinding("open-settings", 0, "ui_action"),
                CreateBinding("open-home", 0, "ui_action")
            ]);

        var plan = AppGraphPlanner.Build(detail, "Settings");

        Assert.Empty(plan.Errors);
        Assert.Equal("open-settings", Assert.Single(plan.Steps).Edge.Id);
    }

    [Fact]
    public void BuildResolvesDestinationBySynonym()
    {
        var graphId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var detail = new CloudAppGraphDetail(
            new CloudAppGraphSummary
            {
                Id = graphId,
                TeamId = Guid.NewGuid(),
                TeamAppId = Guid.NewGuid(),
                Name = "Navigation directory",
                Status = "validated",
                Version = 1,
                CurrentVersionId = versionId
            },
            new CloudAppGraphVersion
            {
                Id = versionId,
                AppGraphId = graphId,
                VersionNumber = 1,
                Status = "validated",
                Definition = JsonNode.Parse("""
                    {
                      "schema": "ansight.app-graph/v1",
                      "nodes": [
                        { "id": "home", "kind": "screen", "name": "Home", "synonyms": ["Dashboard"], "purpose": "Start navigation.", "isEntry": true },
                        { "id": "settings", "kind": "screen", "name": "Settings", "synonyms": ["Preferences", "Options"], "purpose": "Configure the app." }
                      ],
                      "edges": [
                        {
                          "id": "show-settings",
                          "from": "home",
                          "to": "settings",
                          "action": { "automationId": "settings-button", "semanticMeaning": "Show settings" },
                          "postconditions": ["Settings is visible"]
                        }
                      ]
                    }
                    """)!.AsObject()
            },
            [
                new CloudAppGraphBinding
                {
                    Id = bindingId,
                    AppGraphVersionId = versionId,
                    EdgeId = "show-settings",
                    Mechanism = "ui_action",
                    Configuration = new JsonObject
                    {
                        ["action"] = "tap",
                        ["selector"] = new JsonObject { ["automationId"] = "settings-button" }
                    },
                    Postconditions = ["Settings is visible"]
                }
            ]);

        var plan = AppGraphPlanner.Build(detail, "Preferences");

        Assert.Empty(plan.Errors);
        Assert.Equal("show-settings", Assert.Single(plan.Steps).Edge.Id);
    }

    [Fact]
    public void ValidateRequiresBindingsAndObservablePostconditions()
    {
        var validation = AppGraphPlanner.Validate(CreateDetail([]));

        Assert.Contains(validation.Errors, error => error.Contains("no execution binding", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsWorkflowNodeAndEdgeShape()
    {
        var graphId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var detail = new CloudAppGraphDetail(
            new CloudAppGraphSummary { Id = graphId, TeamId = Guid.NewGuid(), TeamAppId = Guid.NewGuid(), Name = "Old shape" },
            new CloudAppGraphVersion
            {
                Id = versionId,
                AppGraphId = graphId,
                Definition = JsonNode.Parse("""
                    {
                      "schema": "ansight.app-graph/v1",
                      "nodes": [{ "id": "intent", "kind": "intent", "label": "Open settings" }],
                      "edges": []
                    }
                    """)!.AsObject()
            },
            []);

        var validation = AppGraphPlanner.Validate(detail);

        Assert.Contains(validation.Errors, error => error.Contains("screen, dialog, or named state", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("canonical name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentRouteDiscovery_DoesNotReadAppGraphsWithoutExplicitOptIn()
    {
        var catalog = new FakeAppGraphAgentRouteCatalog();

        var result = await AppGraphAgentRouteDiscovery.DiscoverAsync(
            catalog,
            Guid.NewGuid(),
            "com.example.app",
            ["Open the 3D guide."],
            isEnabled: false,
            requirePublished: true,
            CancellationToken.None);

        Assert.Empty(result.Plans);
        Assert.Equal(0, catalog.ListRegisteredAppsCallCount);
        Assert.Equal(0, catalog.ListGraphsCallCount);
        Assert.Equal(0, catalog.GetGraphCallCount);
    }

    [Fact]
    public async Task AgentRouteDiscovery_IncludesOnlyAuthorizedPublishedExactAppRoute()
    {
        var teamId = Guid.NewGuid();
        var teamAppId = Guid.NewGuid();
        var graphId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var graph = new CloudAppGraphSummary
        {
            Id = graphId,
            TeamId = teamId,
            TeamAppId = teamAppId,
            Name = "Open 3D guide",
            Intent = "Open the selected area's 3D guide",
            Status = "published",
            Version = 2,
            CurrentVersionId = versionId,
            PublishedVersionId = versionId,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var detail = new CloudAppGraphDetail(
            graph,
            new CloudAppGraphVersion
            {
                Id = versionId,
                AppGraphId = graphId,
                VersionNumber = 2,
                Status = "published",
                Definition = JsonNode.Parse("""
                    {
                      "schema": "ansight.app-graph/v1",
                      "nodes": [
                        { "id": "details", "kind": "screen", "name": "Area details", "synonyms": [], "purpose": "Inspect an area.", "isEntry": true },
                        { "id": "guide", "kind": "screen", "name": "3D guide visible", "synonyms": [], "purpose": "View the 3D guide." }
                      ],
                      "edges": [
                        {
                          "id": "open-guide",
                          "from": "details",
                          "to": "guide",
                          "action": { "automationId": "open-3d-guide-button", "semanticMeaning": "Open 3D guide" },
                          "postconditions": ["The 3D player is visible"]
                        }
                      ]
                    }
                    """)!.AsObject()
            },
            [
                new CloudAppGraphBinding
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    AppGraphVersionId = versionId,
                    EdgeId = "open-guide",
                    Priority = 0,
                    Mechanism = "ui_action",
                    Configuration = new JsonObject
                    {
                        ["action"] = "tap",
                        ["selector"] = new JsonObject { ["automationId"] = "open-3d-guide-button" }
                    },
                    Postconditions = ["The 3D player is visible"],
                    Confidence = 0.96m
                }
            ]);
        var catalog = new FakeAppGraphAgentRouteCatalog
        {
            Apps =
            [
                new CloudRegisteredApp(
                    teamAppId,
                    teamId,
                    "com.example.app",
                    "Example",
                    "ios",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow)
            ],
            Graphs =
            [
                graph,
                new CloudAppGraphSummary
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    TeamAppId = Guid.NewGuid(),
                    Name = "Unrelated app graph",
                    Intent = "Open settings",
                    PublishedVersionId = Guid.NewGuid()
                }
            ],
            Details = new Dictionary<Guid, CloudAppGraphDetail> { [graphId] = detail }
        };

        var result = await AppGraphAgentRouteDiscovery.DiscoverAsync(
            catalog,
            teamId,
            "com.example.app",
            ["Inside the details, open the 3D guide."],
            isEnabled: true,
            requirePublished: true,
            CancellationToken.None);

        var plan = Assert.Single(result.Plans);
        Assert.Equal(graphId, plan.GraphId);
        Assert.Equal("3D guide visible", plan.TargetState);
        var transition = Assert.Single(plan.Transitions);
        Assert.Equal("open-guide", transition.EdgeId);
        Assert.Equal("ui_action", Assert.Single(transition.Bindings).Mechanism);
        Assert.Equal(1, catalog.GetGraphCallCount);
    }

    private static CloudAppGraphDetail CreateDetail(IReadOnlyList<CloudAppGraphBinding> bindings)
    {
        var graphId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        return new CloudAppGraphDetail(
            new CloudAppGraphSummary
            {
                Id = graphId,
                TeamId = Guid.NewGuid(),
                TeamAppId = Guid.NewGuid(),
                Name = "Eagle Rock guide",
                Intent = "Open the 3D guide for Eagle Rock",
                Status = "validated",
                Version = 1,
                CurrentVersionId = versionId
            },
            new CloudAppGraphVersion
            {
                Id = versionId,
                AppGraphId = graphId,
                VersionNumber = 1,
                Status = "validated",
                Definition = JsonNode.Parse("""
                    {
                      "schema": "ansight.app-graph/v1",
                      "nodes": [
                        { "id": "details", "kind": "screen", "name": "Area details", "synonyms": [], "purpose": "Inspect an area.", "isEntry": true },
                        { "id": "ready", "kind": "screen", "name": "3D guide ready", "synonyms": [], "purpose": "View the 3D guide." }
                      ],
                      "edges": [
                        {
                          "id": "open",
                          "from": "details",
                          "to": "ready",
                          "action": { "automationId": "open-button", "semanticMeaning": "Open guide" },
                          "postconditions": ["The 3D scene is visible"]
                        }
                      ]
                    }
                    """)!.AsObject()
            },
            bindings);
    }

    private static CloudAppGraphBinding CreateBinding(string edgeId, int priority, string mechanism)
        => new()
        {
            Id = Guid.NewGuid(),
            EdgeId = edgeId,
            Priority = priority,
            Mechanism = mechanism,
            Configuration = mechanism == "app_tool"
                ? new JsonObject { ["toolId"] = "navigation.open_guide" }
                : new JsonObject
                {
                    ["action"] = "tap",
                    ["selector"] = new JsonObject { ["automationId"] = $"{edgeId}-button" }
                },
            Postconditions = ["The 3D scene is visible"]
        };

    private sealed class FakeAppGraphAgentRouteCatalog : IAppGraphAgentRouteCatalog
    {
        public IReadOnlyList<CloudRegisteredApp> Apps { get; init; } = [];

        public IReadOnlyList<CloudAppGraphSummary> Graphs { get; init; } = [];

        public IReadOnlyDictionary<Guid, CloudAppGraphDetail> Details { get; init; }
            = new Dictionary<Guid, CloudAppGraphDetail>();

        public int ListRegisteredAppsCallCount { get; private set; }

        public int ListGraphsCallCount { get; private set; }

        public int GetGraphCallCount { get; private set; }

        public Task<CloudRegisteredAppQueryResult> ListRegisteredAppsAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            ListRegisteredAppsCallCount++;
            return Task.FromResult(CloudRegisteredAppQueryResult.Success(Apps));
        }

        public Task<CloudAppGraphQueryResult> ListAppGraphsAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            ListGraphsCallCount++;
            return Task.FromResult(CloudAppGraphQueryResult.Success(Graphs));
        }

        public Task<CloudAppGraphDetailResult> GetPublishedAppGraphAsync(
            Guid graphId,
            CancellationToken cancellationToken)
        {
            GetGraphCallCount++;
            return Task.FromResult(Details.TryGetValue(graphId, out var detail)
                ? CloudAppGraphDetailResult.Success(detail)
                : CloudAppGraphDetailResult.Failure("Graph unavailable."));
        }
    }
}
