using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RemoteAppToolCatalogFilterTests
{
    [Fact]
    public void Apply_NarrowsByFeatureAndExecutablePolicyWithBoundedResults()
    {
        var catalog = new JsonObject
        {
            ["protocolVersion"] = "1",
            ["tools"] = new JsonArray
            {
                Tool("redpoint.area.query", "Area Query", "redpoint.area", "area cache query", "read", true),
                Tool("redpoint.area.version", "Area Version", "redpoint.area", "area cache version", "read", true),
                Tool("redpoint.area.clear", "Clear Area", "redpoint.area", "area cache clear", "write", true),
                Tool("redpoint.area.offline", "Offline Area", "redpoint.area", "area cache", "read", false),
                Tool("redpoint.routing.state", "Routing State", "redpoint.routing", "routing navigation", "read", true)
            }
        };

        var result = Assert.IsType<JsonObject>(RemoteAppToolCatalogFilter.Apply(
            catalog,
            new JsonObject
            {
                ["feature"] = "area cache",
                ["policy"] = "read",
                ["executableOnly"] = true,
                ["maxResults"] = 1
            }));

        var tools = Assert.IsType<JsonArray>(result["tools"]);
        Assert.Single(tools);
        Assert.Equal("redpoint.area.query", tools[0]?["id"]?.GetValue<string>());
        Assert.Equal(5, result["totalToolCount"]?.GetValue<int>());
        Assert.Equal(2, result["matchedToolCount"]?.GetValue<int>());
        Assert.Equal(1, result["returnedToolCount"]?.GetValue<int>());
        Assert.True(result["isTruncated"]?.GetValue<bool>());
        Assert.Equal("1", result["protocolVersion"]?.GetValue<string>());
    }

    [Fact]
    public void Apply_MatchesAllFocusedQueryTermsAcrossMetadataAndSchemas()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                Tool(
                    "redpoint.scene.query",
                    "Query Scene",
                    "redpoint.3d_player",
                    "camera overlay",
                    "read",
                    true,
                    argumentProperty: "viewId"),
                Tool(
                    "redpoint.map.query",
                    "Query Map",
                    "redpoint.mapbox",
                    "camera feature",
                    "read",
                    true,
                    argumentProperty: "surfaceId")
            }
        };

        var result = Assert.IsType<JsonObject>(RemoteAppToolCatalogFilter.Apply(
            catalog,
            new JsonObject { ["query"] = "3d camera viewId" }));

        var tool = Assert.Single(Assert.IsType<JsonArray>(result["tools"]));
        Assert.Equal("redpoint.scene.query", tool?["id"]?.GetValue<string>());
    }

    [Fact]
    public void Apply_CombinesPartialMetadataAndDeterministicIdentityFilters()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                Tool(
                    "redpoint.mapbox.query_surface_contents",
                    "Query Mapbox Surface Contents",
                    "redpoint.mapbox",
                    "annotation manager contents",
                    "read",
                    true),
                Tool(
                    "redpoint.mapbox.reset_surface_state",
                    "Reset Mapbox Surface State",
                    "redpoint.mapbox",
                    "mapbox reset",
                    "write",
                    true),
                Tool(
                    "redpoint.scene.query_surface_contents",
                    "Query Scene Surface Contents",
                    "redpoint.3d_player",
                    "scene contents",
                    "read",
                    true)
            }
        };

        var result = Assert.IsType<JsonObject>(RemoteAppToolCatalogFilter.Apply(
            catalog,
            new JsonObject
            {
                ["query"] = "surface cont",
                ["category"] = "REDPOINT.MAPBOX",
                ["idPrefix"] = "REDPOINT.MAPBOX.",
                ["toolId"] = "REDPOINT.MAPBOX.QUERY_SURFACE_CONTENTS",
                ["policy"] = "read"
            }));

        var tool = Assert.Single(Assert.IsType<JsonArray>(result["tools"]));
        Assert.Equal("redpoint.mapbox.query_surface_contents", tool?["id"]?.GetValue<string>());
    }

    [Fact]
    public void Apply_IncludesExecutableReadOnlyToolReferencedByMatchedArgumentSchema()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                Tool(
                    "redpoint.mapbox.query_surface_contents",
                    "Query Surface Contents",
                    "redpoint.mapbox",
                    "annotation manager contents",
                    "read",
                    true,
                    argumentProperty: "surfaceId",
                    argumentDescription: "Surface id returned by redpoint.mapbox.list_surfaces."),
                Tool(
                    "redpoint.mapbox.list_surfaces",
                    "List Surfaces",
                    "redpoint.mapbox",
                    "registered map surfaces",
                    "read",
                    true),
                Tool(
                    "redpoint.mapbox.reset_surfaces",
                    "Reset Surfaces",
                    "redpoint.mapbox",
                    "reset map surfaces",
                    "write",
                    true)
            }
        };

        var result = Assert.IsType<JsonObject>(RemoteAppToolCatalogFilter.Apply(
            catalog,
            new JsonObject
            {
                ["query"] = "annotation manager",
                ["policy"] = "read",
                ["executableOnly"] = true,
                ["maxResults"] = 1
            }));

        var tools = Assert.IsType<JsonArray>(result["tools"]);
        Assert.Equal(2, tools.Count);
        Assert.Equal("redpoint.mapbox.query_surface_contents", tools[0]?["id"]?.GetValue<string>());
        Assert.Equal("redpoint.mapbox.list_surfaces", tools[1]?["id"]?.GetValue<string>());
        var prerequisiteToolIds = Assert.IsType<JsonArray>(tools[0]?["prerequisiteToolIds"]);
        Assert.Equal("redpoint.mapbox.list_surfaces", Assert.Single(prerequisiteToolIds)?.GetValue<string>());
        Assert.Equal(1, result["matchedToolCount"]?.GetValue<int>());
        Assert.Equal(1, result["includedPrerequisiteToolCount"]?.GetValue<int>());
        Assert.Equal(2, result["returnedToolCount"]?.GetValue<int>());
        Assert.False(result["isTruncated"]?.GetValue<bool>());
    }

    [Fact]
    public void Apply_DoesNotApplyTheRequestedPolicyToSupplementalPrerequisites()
    {
        var catalog = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                Tool(
                    "redpoint.cache.clear",
                    "Clear Cache",
                    "redpoint.cache",
                    "clear cache",
                    "write",
                    true,
                    argumentDescription: "Cache id returned by redpoint.cache.inspect."),
                Tool(
                    "redpoint.cache.inspect",
                    "Inspect Cache",
                    "redpoint.cache",
                    "inspect cache",
                    "read",
                    true)
            }
        };

        var result = Assert.IsType<JsonObject>(RemoteAppToolCatalogFilter.Apply(
            catalog,
            new JsonObject
            {
                ["query"] = "clear cache",
                ["policy"] = "write",
                ["maxResults"] = 1
            }));

        var tools = Assert.IsType<JsonArray>(result["tools"]);
        Assert.Equal(2, tools.Count);
        Assert.Equal("redpoint.cache.clear", tools[0]?["id"]?.GetValue<string>());
        Assert.Equal("redpoint.cache.inspect", tools[1]?["id"]?.GetValue<string>());
    }

    private static JsonObject Tool(
        string id,
        string name,
        string category,
        string keywords,
        string policy,
        bool executable,
        string argumentProperty = "query",
        string? argumentDescription = null)
        => new()
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = $"Description for {name}.",
            ["category"] = category,
            ["keywords"] = keywords,
            ["policy"] = policy,
            ["executable"] = executable,
            ["argumentsSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [argumentProperty] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = argumentDescription
                    }
                }
            }
        };
}
