using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RepositoryTaskSearchIntentTests
{
    [Fact]
    public void Match_DoesNotTreatMountainsAsTheDescriptionWordContains()
    {
        var task = CreateDetailsTask();

        var match = RepositoryTaskSearchMatcher.Match(task, "Open map details for Blue Mountains");

        Assert.NotNull(match);
        Assert.Contains("mountains", match.UnmatchedQueryTerms);
        Assert.DoesNotContain(match.Evidence, evidence => evidence.QueryTerm == "mountains");
    }

    [Theory]
    [InlineData("Blue Mountains", "mountains")]
    [InlineData("Mount Arapiles", "arapiles")]
    [InlineData("Siurana", "siurana")]
    [InlineData("Zzyzx Granite Reserve", "zzyzx")]
    public void Match_KeepsParameterizedMapSearchDiscoverableForUnknownAreaNames(
        string searchArea,
        string unmatchedAreaTerm)
    {
        var task = CreateMapSearchTask();

        var match = RepositoryTaskSearchMatcher.Match(task, $"On the map page, search for {searchArea}");

        Assert.NotNull(match);
        Assert.True(match.Score >= 65, $"Expected the preload score threshold; actual score was {match.Score}.");
        Assert.True(match.Coverage >= 0.5, $"Expected the preload coverage threshold; actual coverage was {match.Coverage}.");
        Assert.Contains(unmatchedAreaTerm, match.UnmatchedQueryTerms);
        Assert.Contains(match.Evidence, evidence => evidence.QueryTerm == "search"
            && evidence.Kind == RepositoryTaskSearchMatchKind.Exact);
    }

    [Theory]
    [InlineData("open-selected-map-area-details", "Open the weather card")]
    [InlineData("open-current-area-3d-guide", "Open the weather card")]
    [InlineData("focus-map-search-area", "Select the weather card")]
    public void Match_DoesNotDiscountWeatherAsAnInputValueForUnrelatedParameterizedTasks(
        string taskId,
        string query)
    {
        var task = taskId switch
        {
            "open-selected-map-area-details" => CreateDetailsTask(),
            "open-current-area-3d-guide" => CreateGuideTask(),
            _ => CreateMapSearchTask()
        };

        var parameterizedMatch = RepositoryTaskSearchMatcher.Match(task, query);
        var noInputMatch = RepositoryTaskSearchMatcher.Match(task with { InputSchema = EmptyInputSchema() }, query);

        Assert.False(RepositoryTaskSearchMatcher.HasTaskIntentMatch(task.TaskId, task.Title, task.Feature, query, task.Keywords));
        Assert.Equal(noInputMatch?.Score, parameterizedMatch?.Score);
        Assert.Equal(noInputMatch?.Coverage, parameterizedMatch?.Coverage);
    }

    [Fact]
    public void Match_DoesNotIndexJsonSchemaStructuralWordsOrPropertyNames()
    {
        var task = CreateMapSearchTask();
        task.InputSchema["properties"]!["opaquePropertyName"] = new JsonObject { ["type"] = "string" };

        var match = RepositoryTaskSearchMatcher.Match(
            task,
            "required string object properties additionalProperties minLength");

        Assert.Null(match);
        Assert.Null(RepositoryTaskSearchMatcher.Match(task, "opaquePropertyName"));
    }

    [Fact]
    public void Match_PreservesBehavioralSynonyms()
    {
        var task = CreateMapSearchTask();

        var match = RepositoryTaskSearchMatcher.Match(task, "find an area on the map");

        Assert.NotNull(match);
        Assert.Contains(match.Evidence, evidence => evidence.QueryTerm == "find"
            && evidence.IndexedTerm == "search"
            && evidence.Kind == RepositoryTaskSearchMatchKind.Synonym);
        Assert.True(RepositoryTaskSearchMatcher.HasTaskIntentMatch(task.TaskId, task.Title, task.Feature, "find an area"));
    }

    [Fact]
    public void Match_PreservesCommonInflectedForms()
    {
        var task = CreateMapSearchTask();

        var match = RepositoryTaskSearchMatcher.Match(task, "searching maps");

        Assert.NotNull(match);
        Assert.Contains(match.Evidence, evidence => evidence.QueryTerm == "searching"
            && evidence.IndexedTerm == "search"
            && evidence.Kind == RepositoryTaskSearchMatchKind.Prefix);
        Assert.Contains(match.Evidence, evidence => evidence.QueryTerm == "maps"
            && evidence.IndexedTerm == "map"
            && evidence.Kind == RepositoryTaskSearchMatchKind.Prefix);
    }

    [Fact]
    public void Match_PreservesFuzzyRecoveryForDeclaredInputValues()
    {
        var task = CreateMapSearchTask();
        task.InputSchema["properties"]!["searchArea"]!["default"] = "Siurana";

        var match = RepositoryTaskSearchMatcher.Match(task, "search Siuana area");

        Assert.NotNull(match);
        var evidence = Assert.Single(match.Evidence, candidate => candidate.QueryTerm == "siuana");
        Assert.Equal("siurana", evidence.IndexedTerm);
        Assert.Equal("inputSchema", evidence.IndexedSource);
        Assert.Equal(RepositoryTaskSearchMatchKind.Fuzzy, evidence.Kind);
        Assert.Equal(1, evidence.EditDistance);
    }

    [Theory]
    [InlineData("const")]
    [InlineData("default")]
    [InlineData("enum")]
    [InlineData("title")]
    [InlineData("description")]
    public void Match_PreservesSemanticSchemaMetadata(string metadataKey)
    {
        var task = CreateMapSearchTask();
        task.InputSchema["properties"]!["searchArea"]![metadataKey] = metadataKey == "enum"
            ? new JsonArray("Kalymnos")
            : JsonValue.Create("Kalymnos");

        var match = RepositoryTaskSearchMatcher.Match(task, "search Kalymnos on map");

        Assert.NotNull(match);
        Assert.Contains(match.Evidence, evidence => evidence.QueryTerm == "kalymnos"
            && evidence.IndexedSource == "inputSchema"
            && evidence.Kind == RepositoryTaskSearchMatchKind.Exact);
    }

    [Fact]
    public void HasTaskIntentMatch_RecognizesTheNamedTaskDespiteDifferentFeatureTaxonomy()
    {
        Assert.True(RepositoryTaskSearchMatcher.HasTaskIntentMatch(
            "load-area-weather",
            "Load the area weather",
            "meteorology",
            "Run load-area-weather"));
    }

    [Fact]
    public void HasTaskIntentMatch_PreservesWeatherForecastIntent()
    {
        Assert.True(RepositoryTaskSearchMatcher.HasTaskIntentMatch(
            "load-area-weather",
            "load-area-weather",
            "area-weather",
            "Open the area forecast",
            ["area", "about", "weather", "forecast"]));
    }

    [Theory]
    [InlineData("Open the map page")]
    [InlineData("Select the current area card")]
    [InlineData("Validate the current area")]
    [InlineData("Open the area details")]
    public void HasTaskIntentMatch_DoesNotUseGenericGuideKeywordsAsIntent(string query)
    {
        var task = CreateGuideTask();

        Assert.False(RepositoryTaskSearchMatcher.HasTaskIntentMatch(
            task.TaskId, task.Title, task.Feature, query, task.Keywords));
    }

    [Theory]
    [InlineData("Check the current area 3D guide", "check", "Validate the current area")]
    [InlineData("Show the current area 3D guide", "show", "Open the current area")]
    public void HasTaskIntentMatch_DoesNotUseSynonymsOfGenericContextActionsAsIntent(
        string title,
        string genericKeyword,
        string query)
    {
        Assert.False(RepositoryTaskSearchMatcher.HasTaskIntentMatch(
            "open-current-area-3d-guide",
            title,
            "3d-guide",
            query,
            ["area", "guide", genericKeyword]));
    }

    [Fact]
    public void HasTaskIntentMatch_DoesNotTreatPossessivePronounsAsAccountIntent()
    {
        Assert.False(RepositoryTaskSearchMatcher.HasTaskIntentMatch(
            "open-my-account-from-map-menu",
            "Open My Account from the map menu",
            "account",
            "Open my weather card",
            ["map", "home menu", "account", "my account"]));
    }

    private static RepositoryTaskDefinition CreateMapSearchTask()
        => CreateTask(
            "focus-map-search-area",
            "Focus an exact map search area",
            "Selects a visible exact-text Redpoint map search result and waits until the map selection and card both represent that area.",
            "map-search",
            ["map", "search", "area", "exact", "focus", "selection", "card"],
            "searchArea");

    private static RepositoryTaskDefinition CreateDetailsTask()
        => CreateTask(
            "open-selected-map-area-details",
            "Open selected map area and child details",
            "Opens Details when the current map card visibly contains the requested area name, then searches the AreaPage top search bar and opens a visible exact-text child result.",
            "area-details",
            ["map", "area", "card", "details", "area page", "open", "exact"],
            "targetArea");

    private static RepositoryTaskDefinition CreateGuideTask()
        => CreateTask(
            "open-current-area-3d-guide",
            "Open and validate the current area 3D guide",
            "Opens the 3D guide from the current named AreaPage, derives the authoritative area ID from the loaded guide, and waits until the matching Evergine scene is fully ready.",
            "3d-guide",
            ["area", "3d", "guide", "evergine", "open", "ready", "scene", "validate", "exact"],
            "targetArea");

    private static RepositoryTaskDefinition CreateTask(
        string taskId,
        string title,
        string description,
        string feature,
        IReadOnlyList<string> keywords,
        string inputProperty)
        => new(
            RepositoryRootPath: "/fixtures/redpoint",
            ModulePath: $"/fixtures/redpoint/ansight/tasks/{taskId}.ts",
            TaskId: taskId,
            SchemaVersion: 1,
            AppId: "com.alphaoutdoors.redpoint",
            Title: title,
            Description: description,
            Feature: feature,
            Keywords: keywords,
            InputSchema: new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [inputProperty] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
                },
                ["required"] = new JsonArray(inputProperty),
                ["additionalProperties"] = false
            },
            OutputSchema: null,
            DeclaredHostTools: new Dictionary<string, RepositoryTaskHostToolDescriptor>(StringComparer.Ordinal),
            Timeout: TimeSpan.FromSeconds(60),
            MaximumActions: 20);

    private static JsonObject EmptyInputSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false
        };
}
