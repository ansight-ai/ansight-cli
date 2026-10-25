using System.Text.Json.Nodes;
using Ansight.Cli.Commands.App.Learning;

namespace Ansight.Cli.Tests.Commands.App.Learning;

internal static class AppLearnTestData
{
    public static JsonObject Definition() => JsonNode.Parse("""
        { "schema": "ansight.app-graph/v1", "nodes": [
          { "id": "home", "name": "Home", "kind": "screen", "purpose": "Start", "synonyms": [] },
          { "id": "catalog", "name": "Catalog", "kind": "screen", "purpose": "Browse", "synonyms": [] },
          { "id": "details", "name": "Item details", "kind": "screen", "purpose": "Read item", "synonyms": [] },
          { "id": "filters", "name": "Filters", "kind": "dialog", "purpose": "Choose filters", "synonyms": [] },
          { "id": "map", "name": "Map", "kind": "screen", "purpose": "Browse map", "synonyms": [] }
        ], "edges": [
          { "id": "open-catalog", "from": "home", "to": "catalog", "action": {"automationId": "nav.catalog", "semanticMeaning": "Open catalog"} },
          { "id": "open-details", "from": "catalog", "to": "details", "action": {"automationId": "item.open", "semanticMeaning": "Open item"} },
          { "id": "open-filters", "from": "catalog", "to": "filters", "action": {"automationId": "filters.open", "semanticMeaning": "Open filters"} },
          { "id": "back", "from": "details", "to": "catalog", "action": {"automationId": "details.back", "semanticMeaning": "Back"} },
          { "id": "open-map", "from": "home", "to": "map", "action": {"automationId": "nav.map", "semanticMeaning": "Open map"} }
        ] }
        """)!.AsObject();

    public static JsonObject Evidence(JsonObject definition) => new()
    {
        ["bindingCandidates"] = new JsonArray(definition["edges"]!.AsArray().OfType<JsonObject>().Select(edge =>
            (JsonNode)new JsonObject
            {
                ["edgeId"] = edge["id"]!.DeepClone(), ["mechanism"] = "ui_action",
                ["configuration"] = new JsonObject
                {
                    ["action"] = "tap", ["selector"] = new JsonObject { ["automationId"] = edge["action"]!["automationId"]!.DeepClone() }
                }
            }).ToArray())
    };

    public static AppLearnCandidate Candidate()
    {
        var graph = Definition();
        return AppLearnPlanner.Build(graph, Evidence(graph), new HashSet<string>(), 1).Candidates.Single();
    }

    public static LocalTaskExtractionSnapshot Snapshot(AppLearnCandidate candidate) => new(
        Schema: "ansight.local-task-extraction/v1", ExtractionId: "extraction-1", Status: "ready", Message: "Ready",
        SessionId: "session-1", AppId: "com.example.app", WorkspacePath: "/workspace", StartUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
        EndUtc: DateTimeOffset.UtcNow, TaskName: candidate.TaskName, Description: "Learn navigation", Mode: "hosted", Model: "",
        ValidateSelectors: true, CreatedAtUtc: DateTimeOffset.UtcNow, UpdatedAtUtc: DateTimeOffset.UtcNow, Progress: [],
        Draft: new(candidate.TaskId, "Open catalog", "export const task = {};", candidate.TaskId, "/draft", "/draft/ansight/tasks/task.ts", []),
        TestStatus: "idle", TestMessage: null, TestResult: null, CommittedPath: null, Trace: null);

    public static LocalTaskExtractionTestResult PassedResult() => new(
        "passed", "Passed", "test-run", "session-1", 10,
        [new("starting-state", true, "Home visible", null, null), new("destination-reached", true, "Catalog visible", null, null)], [], "");
}
