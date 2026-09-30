using System.Text.Json.Nodes;

public sealed class AppToolCatalogCacheTests
{
    [Fact]
    public void Clear_RemovesIndexDefinitionsAndRevisions()
    {
        var cache = new AppToolCatalogCache();
        cache.Update(IndexCatalog("catalog-1", "availability-1", "map-definition-1"));
        cache.Update(DefinitionsCatalog(Definition("map.capture", "map-definition-1")));

        cache.Clear();

        var snapshot = cache.GetSnapshot();
        Assert.Null(snapshot.Revision);
        Assert.Null(snapshot.AvailabilityRevision);
        Assert.Null(snapshot.IndexCatalog);
        Assert.Empty(cache.GetSelectedToolIds(null));
        Assert.Equal(["map.capture"], cache.GetMissingDefinitionIds(["map.capture"]));
        Assert.Null(cache.BuildCatalog(null));
    }

    [Fact]
    public void Cache_FiltersCompactIndexAndFetchesOnlyMissingDefinitions()
    {
        var cache = new AppToolCatalogCache();
        cache.Update(IndexCatalog("catalog-1", "availability-1", "map-definition-1"));

        var selectedToolIds = cache.GetSelectedToolIds(new JsonObject
        {
            ["query"] = "map capture",
            ["maxResults"] = 5
        });

        Assert.Equal(["map.capture", "route.open"], selectedToolIds);
        Assert.Equal(selectedToolIds, cache.GetMissingDefinitionIds(selectedToolIds));

        cache.Update(DefinitionsCatalog(
            Definition("map.capture", "map-definition-1"),
            Definition("route.open", "route-definition-1")));
        Assert.Empty(cache.GetMissingDefinitionIds(selectedToolIds));

        var catalog = Assert.IsType<JsonObject>(cache.BuildCatalog(new JsonObject
        {
            ["query"] = "map capture",
            ["maxResults"] = 5
        }));
        var tools = Assert.IsType<JsonArray>(catalog["tools"]);
        Assert.Equal(2, tools.Count);
        Assert.All(tools.OfType<JsonObject>(), tool => Assert.NotNull(tool["argumentsSchema"]));
    }

    [Fact]
    public void Cache_ReturnsCompactSearchResultsAndFullExactDefinitions()
    {
        var cache = new AppToolCatalogCache();
        cache.Update(IndexCatalog("catalog-1", "availability-1", "map-definition-1"));
        cache.Update(DefinitionsCatalog(
            Definition("map.capture", "map-definition-1"),
            Definition("route.open", "route-definition-1")));

        var summary = Assert.IsType<JsonObject>(cache.BuildCatalog(new JsonObject
        {
            ["query"] = "map capture",
            ["detail"] = "summary",
            ["maxResults"] = 5
        }));
        var summaryTools = Assert.IsType<JsonArray>(summary["tools"]);
        Assert.Equal(2, summaryTools.Count);
        Assert.All(summaryTools.OfType<JsonObject>(), tool => Assert.Null(tool["argumentsSchema"]));

        var full = Assert.IsType<JsonObject>(cache.BuildCatalog(new JsonObject
        {
            ["toolId"] = "map.capture",
            ["detail"] = "full"
        }));
        var fullTools = Assert.IsType<JsonArray>(full["tools"]);
        Assert.Equal(2, fullTools.Count);
        Assert.All(fullTools.OfType<JsonObject>(), tool => Assert.NotNull(tool["argumentsSchema"]));
    }

    [Fact]
    public void Cache_AppliesAvailabilitySnapshotAndInvalidatesOneChangedDefinition()
    {
        var cache = new AppToolCatalogCache();
        cache.Update(IndexCatalog("catalog-1", "availability-1", "map-definition-1"));
        cache.Update(DefinitionsCatalog(
            Definition("map.capture", "map-definition-1"),
            Definition("route.open", "route-definition-1")));

        cache.Update(new JsonObject
        {
            ["schema"] = "ansight.tool-catalog.v3",
            ["revision"] = "catalog-1",
            ["unchanged"] = true,
            ["availabilityRevision"] = "availability-2",
            ["evaluatedAtUtc"] = "2026-08-25T00:00:00Z",
            ["changes"] = new JsonObject
            {
                ["map.capture"] = new JsonObject
                {
                    ["available"] = false,
                    ["code"] = "map_not_loaded",
                    ["retryable"] = true
                }
            }
        });

        var catalog = Assert.IsType<JsonObject>(cache.BuildCatalog(null));
        var tools = Assert.IsType<JsonArray>(catalog["tools"]);
        var mapTool = tools.OfType<JsonObject>().Single(tool => tool["id"]?.GetValue<string>() == "map.capture");
        var routeTool = tools.OfType<JsonObject>().Single(tool => tool["id"]?.GetValue<string>() == "route.open");
        Assert.False(mapTool["executable"]?.GetValue<bool>());
        Assert.Equal("map_not_loaded", mapTool["runtime"]?["code"]?.GetValue<string>());
        Assert.Null(routeTool["runtime"]);

        cache.Update(IndexCatalog("catalog-2", "availability-2", "map-definition-2"));
        Assert.Equal(
            ["map.capture"],
            cache.GetMissingDefinitionIds(["map.capture", "route.open"]));
    }

    [Fact]
    public void Cache_TreatsLegacySchemaLessEntriesAsCompleteDefinitions()
    {
        var cache = new AppToolCatalogCache();
        cache.Update(new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "legacy.ping",
                    ["policy"] = "read"
                }
            }
        });

        Assert.Equal(["legacy.ping"], cache.GetSelectedToolIds(null));
        Assert.Empty(cache.GetMissingDefinitionIds(["legacy.ping"]));

        var catalog = Assert.IsType<JsonObject>(cache.BuildCatalog(null));
        var tool = Assert.Single(Assert.IsType<JsonArray>(catalog["tools"]));
        Assert.Equal("legacy.ping", tool?["id"]?.GetValue<string>());
    }

    private static JsonObject IndexCatalog(
        string revision,
        string availabilityRevision,
        string mapDefinitionRevision)
        => new()
        {
            ["schema"] = "ansight.tool-catalog.v3",
            ["revision"] = revision,
            ["availabilityRevision"] = availabilityRevision,
            ["unchanged"] = false,
            ["detail"] = "index",
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "map.capture",
                    ["name"] = "Capture Map",
                    ["description"] = "Capture the current map.",
                    ["category"] = "map",
                    ["policy"] = "read",
                    ["definitionRevision"] = mapDefinitionRevision,
                    ["prerequisiteToolIds"] = new JsonArray("route.open")
                },
                new JsonObject
                {
                    ["id"] = "route.open",
                    ["name"] = "Open Route",
                    ["description"] = "Open a route before map capture.",
                    ["category"] = "routing",
                    ["policy"] = "read",
                    ["definitionRevision"] = "route-definition-1"
                }
            },
            ["count"] = 2,
            ["totalCount"] = 2
        };

    private static JsonObject DefinitionsCatalog(params JsonObject[] definitions)
        => new()
        {
            ["schema"] = "ansight.tool-catalog.v3",
            ["revision"] = "catalog-1",
            ["unchanged"] = false,
            ["detail"] = "definitions",
            ["tools"] = new JsonArray(definitions.Select(definition => (JsonNode?)definition).ToArray())
        };

    private static JsonObject Definition(string toolId, string definitionRevision)
        => new()
        {
            ["id"] = toolId,
            ["name"] = toolId,
            ["description"] = $"Definition for {toolId}.",
            ["category"] = "test",
            ["policy"] = "read",
            ["definitionRevision"] = definitionRevision,
            ["argumentsSchema"] = new JsonObject { ["type"] = "object" },
            ["resultSchema"] = new JsonObject { ["type"] = "object" }
        };
}
