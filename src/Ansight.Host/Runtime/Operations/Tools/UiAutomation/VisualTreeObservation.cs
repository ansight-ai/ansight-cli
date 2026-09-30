using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class VisualTreeObservation
{
    private const int MaximumStructureNodes = 60;

    public static JsonObject? Build(JsonObject structuredContent, UiProjectionOptions? options = null)
    {
        if (structuredContent["payload"]?["result"] is JsonObject inlineTree)
        {
            var inlineObservation = Build(
                inlineTree,
                structuredContent["sessionId"]?.GetValue<string>(),
                structuredContent["appId"]?.GetValue<string>(),
                structuredContent["toolId"]?.GetValue<string>(), options);
            if (inlineObservation is not null)
            {
                return WithEvidence(inlineObservation, structuredContent);
            }
        }

        var artifactPath = structuredContent["payload"]?["result"]?["artifactPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
        {
            return null;
        }

        try
        {
            var visualTree = JsonNode.Parse(File.ReadAllText(artifactPath)) as JsonObject;
            var observation = visualTree is null
                ? null
                : Build(
                    visualTree,
                    structuredContent["sessionId"]?.GetValue<string>(),
                    structuredContent["appId"]?.GetValue<string>(),
                    structuredContent["toolId"]?.GetValue<string>(), options);
            return observation is null ? null : WithEvidence(observation, structuredContent);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static JsonObject WithEvidence(JsonObject observation, JsonObject source)
    {
        foreach (var key in new[] { "persistedVisualTree", "responseType", "evidence", "message", "isError", "error", "errorCode" })
        {
            if (source[key] is { } value) observation[key] = value.DeepClone();
        }
        return observation;
    }

    internal static JsonObject? Build(
        JsonObject visualTree,
        string? sessionId,
        string? appId,
        string? toolId,
        UiProjectionOptions? options = null)
    {
        options ??= UiProjectionOptions.Model;
        visualTree = VisualTreePresentationNormalizer.Create(visualTree);
        if (!VisualTreeTypeRegistry.TryCreateCompactV2(
                visualTree,
                out var typeRegistry,
                out var root)
            || typeRegistry is null
            || root is null)
        {
            return null;
        }

        var meaningfulNodes = LiveUiNodeQuery.Enumerate(root, typeRegistry)
            .Where(IsMeaningful)
            .ToArray();
        var candidateNodes = meaningfulNodes
            .Where(match => !ReferenceEquals(match.Node, root))
            .ToArray();
        var viewport = LiveUiNodeQuery.ReadCoordinateSpace(visualTree)
                       ?? LiveUiNodeQuery.ReadBounds(root);
        var selectedMatches = new Dictionary<JsonObject, LiveUiNodeMatch>(ReferenceEqualityComparer.Instance);
        var returnedNodes = BuildBoundedNodeList(candidateNodes, visualTree, viewport, selectedMatches, options);
        var observation = new JsonObject
        {
            ["capability"] = "ui.observe",
            ["sessionId"] = sessionId,
            ["appId"] = appId,
            ["toolId"] = toolId,
            ["format"] = visualTree["format"]?.DeepClone(),
            ["platform"] = visualTree["platform"]?.DeepClone(),
            ["capturedAtUtc"] = visualTree["capturedAtUtc"]?.DeepClone(),
            ["rootScope"] = visualTree["rootScope"]?.DeepClone(),
            ["viewport"] = viewport?.ToJson(),
            ["root"] = UiNodeProjection.FromMatch(
                new LiveUiNodeMatch(root, [], typeRegistry), options, includeAncestors: false),
            ["nodes"] = new JsonArray(returnedNodes),
            ["nodeCount"] = LiveUiNodeQuery.Enumerate(root, typeRegistry).Count(),
            ["meaningfulNodeCount"] = meaningfulNodes.Length,
            ["returnedNodeCount"] = returnedNodes.Length,
            ["truncated"] = candidateNodes.Length > returnedNodes.Length
                            || visualTree["truncated"]?.GetValue<bool>() == true
                            || returnedNodes.Any(node => node["projectionTruncated"]?.GetValue<bool>() == true)
        };
        var controllers = NavigationControllerCatalog.ResolveVisualTree(
            visualTree,
            toolId);
        if (controllers.Count > 0)
        {
            observation["framework"] = controllers[0].Framework;
            observation["frameworks"] = new JsonArray(
                controllers.Select(static controller => (JsonNode?)controller.Framework).ToArray());
            observation["graphStructureControllers"] = new JsonArray(
                controllers.Select(static controller => (JsonNode?)new JsonObject
                {
                    ["framework"] = controller.Framework
                }).ToArray());
            observation["structureNodes"] = new JsonArray(
                BuildStructureNodeList(root, typeRegistry, options));
        }
        NestNodes(observation, root, typeRegistry, selectedMatches, options);
        return observation;
    }

    private static void NestNodes(
        JsonObject observation,
        JsonObject root,
        VisualTreeTypeRegistry typeRegistry,
        IReadOnlyDictionary<JsonObject, LiveUiNodeMatch> selectedMatches,
        UiProjectionOptions options)
    {
        var rootResult = observation["root"]!.AsObject();
        observation.Remove("nodes");
        var projected = new Dictionary<JsonObject, JsonObject>(ReferenceEqualityComparer.Instance)
        {
            [root] = rootResult
        };
        foreach (var entry in selectedMatches)
        {
            // Detach from the old ranked list before attaching to its actual ancestor.
            projected[entry.Value.Node] = entry.Key.DeepClone().AsObject();
        }
        foreach (var match in selectedMatches.Values)
        {
            foreach (var ancestor in match.Ancestors.Where(ancestor =>
                         LiveUiNodeQuery.ReadAutomationId(ancestor) is not null
                         || LiveUiNodeQuery.ReadText(ancestor) is not null).TakeLast(options.MaximumAncestors))
            {
                if (!projected.ContainsKey(ancestor))
                {
                    var context = UiNodeProjection.Ancestor(ancestor, typeRegistry, options);
                    context["contextOnly"] = true;
                    projected[ancestor] = context;
                }
            }
        }
        // Use source identity, not automation IDs or text: these can legitimately repeat.
        foreach (var match in LiveUiNodeQuery.Enumerate(root, typeRegistry))
        {
            if (ReferenceEquals(match.Node, root) || !projected.TryGetValue(match.Node, out var node))
            {
                continue;
            }
            node.Remove("ancestorPath");
            var parent = match.Ancestors.LastOrDefault(projected.ContainsKey) ?? root;
            var parentResult = projected[parent];
            var children = parentResult["children"] as JsonArray;
            if (children is null)
            {
                children = new JsonArray();
                parentResult["children"] = children;
            }
            children.Add(node);
        }
        if (observation["structureNodes"] is JsonArray structureNodes)
        {
            var structureMatches = LiveUiNodeQuery.Enumerate(root, typeRegistry)
                .Where(IsNavigationStructureNode).Take(MaximumStructureNodes).ToArray();
            var retainedSources = new HashSet<JsonObject>(projected.Keys, ReferenceEqualityComparer.Instance);
            retainedSources.UnionWith(structureMatches.Select(match => match.Node));
            for (var index = 0; index < structureMatches.Length; index++)
            {
                var node = structureNodes[index]!.AsObject();
                node.Remove("ancestorPath");
                node.Remove("ancestorTypes");
                var parent = structureMatches[index].Ancestors.LastOrDefault(retainedSources.Contains);
                if (parent is not null)
                {
                    node["parentId"] = LiveUiNodeQuery.ReadString(parent, "id");
                }
            }
        }
    }

    private static bool IsMeaningful(LiveUiNodeMatch match)
    {
        if (!LiveUiNodeQuery.IsEffectivelyVisible(match))
        {
            return false;
        }

        return LiveUiNodeQuery.ReadAutomationId(match.Node) is not null
               || LiveUiNodeQuery.ReadText(match.Node) is not null
               || LiveUiNodeQuery.ReadSupportedActions(match.Node, match.TypeRegistry).Count > 0;
    }

    private static JsonObject[] BuildBoundedNodeList(
        IReadOnlyList<LiveUiNodeMatch> candidates,
        JsonObject visualTree,
        LiveUiBounds? viewport,
        IDictionary<JsonObject, LiveUiNodeMatch> selectedMatches,
        UiProjectionOptions options)
    {
        var results = new List<JsonObject>(Math.Min(candidates.Count, options.MaximumNodes));
        var resultCharacters = 0;
        var hasActivePageContext = LiveUiMauiContext.HasActivePageContext(visualTree);
        var rankedCandidates = candidates
            .Select((match, index) => new LiveUiRankedObservationNode(match, index))
            .OrderByDescending(candidate => viewport is not null
                                            && LiveUiNodeQuery.HasUsableBoundsInViewport(
                                                candidate.Match,
                                                viewport))
            .ThenByDescending(candidate => hasActivePageContext
                                           && LiveUiMauiContext.IsInActivePage(candidate.Match, visualTree))
            .ThenByDescending(candidate =>
                LiveUiNodeQuery.ReadSupportedActions(
                    candidate.Match.Node,
                    candidate.Match.TypeRegistry).Count > 0)
            .ThenByDescending(candidate => LiveUiNodeQuery.ReadAutomationId(candidate.Match.Node) is not null)
            .ThenBy(candidate => candidate.Index);
        foreach (var candidate in rankedCandidates.Take(options.MaximumNodes))
        {
            // NestNodes retains shared ancestry once. Do not spend each node's budget on
            // repeated ancestor paths that will be removed, or mark that removal as evidence loss.
            var result = UiNodeProjection.FromMatch(candidate.Match, options, includeAncestors: false);
            var resultLength = result.ToJsonString().Length;
            if (results.Count > 0
                && resultCharacters + resultLength > options.MaximumCharacters)
            {
                break;
            }

            results.Add(result);
            selectedMatches[result] = candidate.Match;
            resultCharacters += resultLength;
        }

        return results.ToArray();
    }

    private static JsonObject[] BuildStructureNodeList(
        JsonObject root,
        VisualTreeTypeRegistry typeRegistry,
        UiProjectionOptions options)
        => LiveUiNodeQuery.Enumerate(root, typeRegistry)
            .Where(IsNavigationStructureNode)
            .Take(MaximumStructureNodes)
            .Select(match => ToCompactStructureNode(match, options))
            .ToArray();

    private static bool IsNavigationStructureNode(LiveUiNodeMatch match)
    {
        var type = match.TypeRegistry.Resolve(match.Node);
        var role = LiveUiNodeQuery.ReadRole(match.Node, match.TypeRegistry);
        return ContainsNavigationTerm(type) || ContainsNavigationTerm(role);
    }

    private static bool ContainsNavigationTerm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("shell", StringComparison.OrdinalIgnoreCase)
               || value.Contains("flyout", StringComparison.OrdinalIgnoreCase)
               || value.Contains("drawer", StringComparison.OrdinalIgnoreCase)
               || value.Contains("uinavigationcontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("uitabbarcontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("uisplitviewcontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("uipageviewcontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("navigationstack", StringComparison.OrdinalIgnoreCase)
               || value.Contains("navigationsplitview", StringComparison.OrdinalIgnoreCase)
               || value.Contains("nstabviewcontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("nssplitviewcontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("nspagecontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("navhost", StringComparison.OrdinalIgnoreCase)
               || value.Contains("drawerlayout", StringComparison.OrdinalIgnoreCase)
               || value.Contains("bottomnavigationview", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tablayout", StringComparison.OrdinalIgnoreCase)
               || value.Contains("viewpager", StringComparison.OrdinalIgnoreCase)
               || value.Contains("modalnavigationdrawer", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tabrow", StringComparison.OrdinalIgnoreCase)
               || value.Contains("pagerstate", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tabbar", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tabview", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tabcontroller", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tabnavigator", StringComparison.OrdinalIgnoreCase)
               || value.Contains("bottomnavigation", StringComparison.OrdinalIgnoreCase)
               || value.Contains("navigationrail", StringComparison.OrdinalIgnoreCase)
               || value.Contains("navigationcontainer", StringComparison.OrdinalIgnoreCase)
               || value.Contains("navigationpage", StringComparison.OrdinalIgnoreCase)
               || value.Contains("navigator", StringComparison.OrdinalIgnoreCase)
               || value.Equals("tab", StringComparison.OrdinalIgnoreCase)
               || value.Equals("tablist", StringComparison.OrdinalIgnoreCase)
               || value.Equals("tabpanel", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject ToCompactStructureNode(LiveUiNodeMatch match, UiProjectionOptions options)
    {
        var result = UiNodeProjection.FromMatch(match, options);
        result["ancestorTypes"] = new JsonArray(match.Ancestors
            .TakeLast(options.MaximumAncestors + 2)
            .Select(ancestor => (JsonNode?)match.TypeRegistry.Resolve(ancestor))
            .ToArray());
        CopyBoolean(match.Node, result, match.TypeRegistry, "selected");
        CopyBoolean(match.Node, result, match.TypeRegistry, "active");
        CopyBoolean(match.Node, result, match.TypeRegistry, "currentPage");
        return result;
    }

    private static void CopyBoolean(
        JsonObject source,
        JsonObject target,
        VisualTreeTypeRegistry typeRegistry,
        string propertyName)
    {
        if (source[propertyName] is null)
        {
            return;
        }

        target[propertyName] = LiveUiNodeQuery.ReadBoolean(
            source,
            propertyName,
            fallback: false,
            typeRegistry);
    }


}
