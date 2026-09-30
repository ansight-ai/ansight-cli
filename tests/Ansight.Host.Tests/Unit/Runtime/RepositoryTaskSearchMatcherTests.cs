using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RepositoryTaskSearchMatcherTests
{
    [Fact]
    public void Match_RanksCompleteRedpointCycleAheadOfPartialTask()
    {
        var completeTask = CreateTask(
            "open-area-3d-guide",
            "Open an area 3D guide from map search",
            "Searches an area, selects one result, opens its area page and 3D guide.",
            ["search", "area", "select", "open", "3d", "guide"],
            "Sikati Bay");
        var partialTask = CreateTask(
            "open-current-area-3d-guide",
            "Open the current area 3D guide",
            "Opens a 3D guide from the current area page.",
            ["area", "open", "3d", "guide"],
            "Seaside");
        const string query =
            "search for Siuana, go to the details area page, search for El Pati, select it and open the 3D guide";

        var completeMatch = RepositoryTaskSearchMatcher.Match(completeTask, query);
        var partialMatch = RepositoryTaskSearchMatcher.Match(partialTask, query);

        Assert.NotNull(completeMatch);
        Assert.NotNull(partialMatch);
        Assert.True(completeMatch.Score > partialMatch.Score);
        Assert.Contains(
            completeMatch.Evidence,
            evidence => evidence.QueryTerm == "go"
                        && evidence.IndexedTerm == "open"
                        && evidence.Kind == RepositoryTaskSearchMatchKind.Synonym);
        Assert.Contains(
            completeMatch.Evidence,
            evidence => evidence.QueryTerm == "select"
                        && evidence.IndexedTerm == "select"
                        && evidence.Kind == RepositoryTaskSearchMatchKind.Exact);
        Assert.Contains("siuana", completeMatch.UnmatchedQueryTerms);
        Assert.Contains("pati", completeMatch.UnmatchedQueryTerms);
    }

    [Fact]
    public void Match_UsesLevenshteinDistanceForMisspelledRuntimeValue()
    {
        var task = CreateTask(
            "open-area-3d-guide",
            "Open an area 3D guide from map search",
            "Searches an area and opens its 3D guide.",
            ["search", "area", "open", "3d", "guide"],
            "Siurana");

        var match = RepositoryTaskSearchMatcher.Match(task, "search Siuana area");

        Assert.NotNull(match);
        var fuzzyEvidence = Assert.Single(match.Evidence, evidence => evidence.QueryTerm == "siuana");
        Assert.Equal("siurana", fuzzyEvidence.IndexedTerm);
        Assert.Equal(RepositoryTaskSearchMatchKind.Fuzzy, fuzzyEvidence.Kind);
        Assert.Equal(1, fuzzyEvidence.EditDistance);
    }

    [Fact]
    public void Match_RejectsLowCoverageNoise()
    {
        var task = CreateTask(
            "open-area-3d-guide",
            "Open an area 3D guide from map search",
            "Searches an area and opens its 3D guide.",
            ["search", "area", "open", "3d", "guide"],
            "Sikati Bay");

        var match = RepositoryTaskSearchMatcher.Match(task, "delete account permanently");

        Assert.Null(match);
    }

    [Fact]
    public void Match_PenalizesTaskSpecificEntityMismatch()
    {
        var task = CreateTask(
            "save-secret-garden-offline",
            "Save Secret Garden for offline use",
            "Navigates through Sikati Bay to Secret Garden and saves the area offline.",
            ["save", "offline", "area", "secret garden", "sikati bay"],
            "Secret Garden");

        var matchingEntity = RepositoryTaskSearchMatcher.Match(
            task,
            "save Secret Garden area offline");
        var mismatchedEntity = RepositoryTaskSearchMatcher.Match(
            task,
            "save El Pati area offline");

        Assert.NotNull(matchingEntity);
        Assert.NotNull(mismatchedEntity);
        Assert.True(matchingEntity.Score >= mismatchedEntity.Score + 15);
    }

    private static RepositoryTaskDefinition CreateTask(
        string taskId,
        string title,
        string description,
        IReadOnlyList<string> keywords,
        string searchArea)
        => new(
            RepositoryRootPath: "/repository",
            ModulePath: $"/repository/ansight/tasks/{taskId}.ts",
            TaskId: taskId,
            SchemaVersion: 1,
            AppId: "com.example.app",
            Title: title,
            Description: description,
            Feature: "mapbox-to-3d-guide",
            Keywords: keywords,
            InputSchema: new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["searchArea"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["default"] = searchArea
                    }
                },
                ["additionalProperties"] = false
            },
            OutputSchema: null,
            DeclaredHostTools: new Dictionary<string, RepositoryTaskHostToolDescriptor>(StringComparer.Ordinal),
            Timeout: TimeSpan.FromSeconds(60),
            MaximumActions: 20);
}
