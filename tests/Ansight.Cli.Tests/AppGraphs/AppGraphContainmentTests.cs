using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.AppGraphs;

namespace Ansight.Cli.Tests.AppGraphs;

public sealed class AppGraphContainmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompilerAndPlannerPreserveFlyoutOwningOneTabbedPage(bool reverseHostOrder)
    {
        var summary = CreateNestedSummary();

        if (reverseHostOrder)
        {
            var hosts = summary["navigationHosts"]!.AsArray();
            var first = hosts[0]!.DeepClone();
            hosts.RemoveAt(0);
            hosts.Add(first);
        }

        Assert.True(AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out var candidate, out var error), error);

        var definition = candidate!.Definition;
        var nodes = definition["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.Null(nodes.Single(node => node["name"]!.GetValue<string>() == "Home flyout")["parentScreen"]);
        Assert.Equal("Home flyout", nodes.Single(node => node["name"]!.GetValue<string>() == "Main tabs")["parentScreen"]!.GetValue<string>());
        Assert.Equal("Main tabs", nodes.Single(node => node["name"]!.GetValue<string>() == "Map")["parentScreen"]!.GetValue<string>());
        Assert.Equal("Main tabs", nodes.Single(node => node["name"]!.GetValue<string>() == "Crags")["parentScreen"]!.GetValue<string>());
        Assert.Empty(definition["edges"]!.AsArray());

        var detail = new CloudAppGraphDetail(new CloudAppGraphSummary(), new CloudAppGraphVersion { Definition = definition }, []);
        Assert.Empty(AppGraphPlanner.Validate(detail).Errors);
    }

    [Theory]
    [InlineData("ios-swiftui", "sheet", "other")]
    [InlineData("ios-uikit", "presented_view_controller", "other")]
    [InlineData("flutter", "navigator", "shell")]
    [InlineData("react-native", "react_navigation_stack", "shell")]
    [InlineData("android-compose", "nav_host", "shell")]
    [InlineData("android-views", "nav_controller", "shell")]
    [InlineData("capacitor", "web_router", "shell")]
    [InlineData("macos-appkit", "popover", "other")]
    [InlineData("native-unknown", "generic_host", "other")]
    public void SingleChildContainerDoesNotDependOnFramework(string framework, string technologyKind, string role)
    {
        var summary = CreateNestedSummary();
        var hosts = summary["navigationHosts"]!.AsArray();
        hosts.RemoveAt(1);

        var host = hosts[0]!.AsObject();
        host["kind"] = role;
        host["framework"] = framework;
        host["technology"] = new JsonObject
        {
            ["framework"] = framework,
            ["kind"] = technologyKind,
            ["navigationToolId"] = "",
            ["structureFingerprint"] = ""
        };

        Assert.True(AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out var candidate, out var error), error);

        var detail = new CloudAppGraphDetail(new CloudAppGraphSummary(), new CloudAppGraphVersion { Definition = candidate!.Definition }, []);
        Assert.Empty(AppGraphPlanner.Validate(detail).Errors);

        host["childDestinationIds"] = new JsonArray();
        Assert.False(AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out _, out error));
        Assert.Contains("at least one observed direct child", error);
    }

    [Theory]
    [InlineData("flutter", "tab_bar")]
    [InlineData("ios-swiftui", "tab_view")]
    [InlineData("android-compose", "tab_row")]
    public void OneObservedTabIsValidWithoutInventingASibling(string framework, string technologyKind)
    {
        var summary = CreateNestedSummary();
        summary["destinations"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "panel",
            ["kind"] = "state",
            ["name"] = "Panel",
            ["parentScreen"] = "map",
            ["purpose"] = "View content."
        });
        summary["tabGroups"] = new JsonArray(new JsonObject
        {
            ["id"] = "panels",
            ["parentDestinationId"] = "map",
            ["selectedDestinationId"] = "panel",
            ["tabDestinationIds"] = new JsonArray("panel"),
            ["technology"] = new JsonObject
            {
                ["framework"] = framework,
                ["kind"] = technologyKind,
                ["navigationToolId"] = "",
                ["structureFingerprint"] = ""
            }
        });
        Assert.True(AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out _, out var error), error);
        summary["tabGroups"]![0]!["tabDestinationIds"] = new JsonArray();
        Assert.False(AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out _, out error));
        Assert.Contains("at least one observed tabDestinationId", error);
    }

    [Theory]
    [InlineData("ios-uikit", "ui_navigation_controller", false)]
    [InlineData("react-native", "react_navigation_stack", true)]
    public void CompilerRetainsParadigmAndAppliesItsStackRule(string framework, string technologyKind, bool firstChildCanBeActive)
    {
        var summary = CreateNestedSummary();
        var hosts = summary["navigationHosts"]!.AsArray();
        hosts.RemoveAt(1);

        var host = hosts[0]!;
        host["kind"] = "shell";
        host["framework"] = framework;
        host["childDestinationIds"] = new JsonArray("tabs", "map");
        host["activeChildDestinationId"] = "tabs";
        host["technology"] = new JsonObject
        {
            ["framework"] = framework,
            ["kind"] = technologyKind,
            ["navigationToolId"] = "",
            ["structureFingerprint"] = ""
        };
        Assert.Equal(firstChildCanBeActive, AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out _, out _));
        host["activeChildDestinationId"] = "map";
        Assert.True(AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out var candidate, out var error), error);

        var compiledHost = Assert.Single(candidate!.Definition["navigationHosts"]!.AsArray())!;
        Assert.Equal("ordered_stack", compiledHost["technology"]!["paradigm"]!["topology"]!.GetValue<string>());
        compiledHost["activeChildDestinationId"] = compiledHost["childDestinationIds"]![0]!.DeepClone();

        var detail = new CloudAppGraphDetail(new CloudAppGraphSummary(), new CloudAppGraphVersion { Definition = candidate.Definition }, []);
        Assert.Equal(firstChildCanBeActive, AppGraphPlanner.Validate(detail).Errors.Count == 0);
    }

    [Fact]
    public void ContainmentValidationUsesOnlyDestinationInstanceRelations()
    {
        AppGraphContainmentRelation[] relations =
            [
                new("window", "presentation"),
            new("presentation", "content"),
            new("content", "panel")
            ];
        Assert.Empty(AppGraphContainment.FindErrors(relations));
        Assert.Contains(AppGraphContainment.FindErrors([.. relations, new("panel", "window")]), error => error.Contains("cycle"));
        Assert.Contains(AppGraphContainment.FindErrors([.. relations, new("window", "panel")]), error => error.Contains("multiple direct owners"));
    }

    [Theory]
    [InlineData("self", "cannot own itself")]
    [InlineData("cycle", "cycle")]
    [InlineData("flattened", "multiple direct owners")]
    public void CompilerRejectsInvalidOwnership(string corruption, string expectedError)
    {
        var summary = CreateNestedSummary();
        var hosts = summary["navigationHosts"]!.AsArray();

        if (corruption == "self")
        {
            hosts[0]!["childDestinationIds"]!.AsArray().Add("flyout");
        }

        if (corruption == "cycle")
        {
            hosts[1]!["childDestinationIds"]!.AsArray().Add("flyout");
        }

        if (corruption == "flattened")
        {
            hosts[0]!["childDestinationIds"]!.AsArray().Add("map");
        }

        Assert.False(AppGraphExplorationCompiler.TryCompile(summary.ToJsonString(), out _, out var error));
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    private static JsonObject CreateNestedSummary() => JsonNode.Parse("""
        {
          "schema": "ansight.app-graph-exploration/v1",
          "destinations": [
            { "id": "flyout", "kind": "screen", "name": "Home flyout", "synonyms": [], "purpose": "Own the app navigation." },
            { "id": "tabs", "kind": "screen", "name": "Main tabs", "synonyms": [], "purpose": "Switch app tabs." },
            { "id": "map", "kind": "screen", "name": "Map", "synonyms": [], "purpose": "Explore the map." },
            { "id": "crags", "kind": "screen", "name": "Crags", "synonyms": [], "purpose": "Explore crags." }
          ],
          "navigationHosts": [
            { "id": "flyout-host", "kind": "flyout", "name": "Home flyout", "destinationId": "flyout", "activeChildDestinationId": "tabs", "childDestinationIds": ["tabs"], "framework": "maui", "technology": { "framework": "maui", "kind": "flyout_page", "navigationToolId": "maui.get_navigation_state", "structureFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } },
            { "id": "tabs-host", "kind": "top_tabs", "name": "Main tabs", "destinationId": "tabs", "activeChildDestinationId": "map", "childDestinationIds": ["map", "crags"], "framework": "maui", "technology": { "framework": "maui", "kind": "tabbed_page", "navigationToolId": "maui.get_navigation_state", "structureFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } }
          ],
          "tabGroups": [],
          "transitions": []
        }
        """)!.AsObject();
}
