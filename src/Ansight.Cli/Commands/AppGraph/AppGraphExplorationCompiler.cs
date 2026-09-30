using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Commands.AppGraph;

internal static class AppGraphExplorationCompiler
{
    private const string ExplorationSchema = "ansight.app-graph-exploration/v1";
    private const int MaximumExistingGraphCharacters = 96_000;
    private static readonly HashSet<string> supportedBindingMechanisms =
        new(["app_link", "native_route", "app_tool", "ui_action"], StringComparer.Ordinal);
    public static string BuildInstruction(
        CloudAppGraphDetail? detail,
        string graphName,
        int maximumActions,
        string? focus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        var definitionNode = detail?.Version.Definition ?? new JsonObject
        {
            ["schema"] = "ansight.app-graph/v1",
            ["nodes"] = new JsonArray(),
            ["edges"] = new JsonArray()
        };
        var definition = definitionNode.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (definition.Length > MaximumExistingGraphCharacters)
        {
            definition = definition[..MaximumExistingGraphCharacters] + "…";
        }

        var focusText = string.IsNullOrWhiteSpace(focus)
            ? AppGraphTextResource.ReadSection(
                AppGraphTextResource.CatalogFileName,
                "exploration-focus-none")
            : AppGraphTextResource.RenderSection(
                AppGraphTextResource.CatalogFileName,
                "exploration-focus",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["FOCUS"] = focus.Trim()
                }).TrimEnd();

        var graphContext = detail is null
            ? AppGraphTextResource.RenderSection(
                AppGraphTextResource.CatalogFileName,
                "exploration-new-graph",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GRAPH_NAME"] = graphName.Trim()
                }).TrimEnd()
            : AppGraphTextResource.RenderSection(
                AppGraphTextResource.CatalogFileName,
                "exploration-existing-graph",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GRAPH_NAME"] = graphName.Trim(),
                    ["VERSION_NUMBER"] = detail.Version.VersionNumber.ToString(
                        CultureInfo.InvariantCulture)
                }).TrimEnd();
        return AppGraphTextResource.RenderSection(
            AppGraphTextResource.CatalogFileName,
            "exploration-instruction",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FOCUS_CONTEXT"] = focusText,
                ["MAXIMUM_ACTIONS"] = maximumActions.ToString(CultureInfo.InvariantCulture),
                ["GRAPH_CONTEXT"] = graphContext,
                ["DEFINITION"] = definition
            }).TrimEnd();
    }

    public static bool TryCompile(
        string? completionSummary,
        out AppGraphExplorationCandidate? candidate,
        out string error)
    {
        candidate = null;
        error = string.Empty;
        if (!AppGraphExplorationSummaryValidator.TryValidate(completionSummary, out error)) return false;
        if (!TryParseObject(completionSummary, out var root, out error)) return false;
        if (!string.Equals(ReadString(root, "schema"), ExplorationSchema, StringComparison.Ordinal))
        {
            error = $"The exploration summary must use schema '{ExplorationSchema}'.";
            return false;
        }

        if (root["destinations"] is not JsonArray destinationValues || destinationValues.Count == 0)
        {
            error = "The exploration did not return any observed destinations.";
            return false;
        }
        if (root["transitions"] is not JsonArray transitionValues)
        {
            error = "The exploration summary must include a transitions array.";
            return false;
        }

        var nodes = new JsonArray();
        var destinationIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var compiledNodesBySourceId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var usedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < destinationValues.Count; index++)
        {
            if (destinationValues[index] is not JsonObject destination)
            {
                error = $"Exploration destination {index + 1} is not an object.";
                return false;
            }
            var sourceId = ReadString(destination, "id");
            var name = ReadString(destination, "name");
            var purpose = ReadString(destination, "purpose");
            if (string.IsNullOrWhiteSpace(sourceId)
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(purpose))
            {
                error = $"Exploration destination {index + 1} requires id, name, and purpose.";
                return false;
            }
            if (destinationIds.ContainsKey(sourceId))
            {
                error = $"Exploration destination id '{sourceId}' is duplicated.";
                return false;
            }
            var destinationKind = ReadString(destination, "kind")?.Trim().ToLowerInvariant();
            if (destinationKind is not ("screen" or "dialog" or "state"))
            {
                error = $"Exploration destination '{sourceId}' must use kind screen, dialog, or state.";
                return false;
            }
            var parentScreen = ReadString(destination, "parentScreen");
            if (destinationKind == "state" && string.IsNullOrWhiteSpace(parentScreen))
            {
                error = $"Named state '{sourceId}' requires its containing screen in parentScreen.";
                return false;
            }
            if (destinationKind == "dialog" && !string.IsNullOrWhiteSpace(parentScreen))
            {
                error = $"Dialog destination '{sourceId}' cannot belong to a navigation host or containing screen.";
                return false;
            }
            var normalizedId = MakeUniqueId(NormalizeId(sourceId, destinationKind), usedNodeIds);
            destinationIds[sourceId] = normalizedId;
            var compiledNode = new JsonObject
            {
                ["id"] = normalizedId,
                ["kind"] = destinationKind,
                ["name"] = name.Trim(),
                ["parentScreen"] = string.IsNullOrWhiteSpace(parentScreen) ? null : parentScreen.Trim(),
                ["synonyms"] = JsonSerializer.SerializeToNode(ReadStringArray(destination, "synonyms")),
                ["purpose"] = purpose.Trim(),
                ["x"] = 120 + (index % 5) * 300,
                ["y"] = 120 + (index / 5) * 220
            };
            nodes.Add(compiledNode);
            compiledNodesBySourceId[sourceId] = compiledNode;
        }

        if (!TryCompileNavigationStructure(
                root,
                destinationIds,
                compiledNodesBySourceId,
                out var navigationHosts,
                out var tabGroups,
                out error))
        {
            return false;
        }
        if (!TryValidateScreenParents(nodes, navigationHosts, out error))
        {
            return false;
        }

        var edges = new JsonArray();
        var bindingCandidates = new JsonArray();
        var usedEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < transitionValues.Count; index++)
        {
            if (transitionValues[index] is not JsonObject transition)
            {
                error = $"Exploration transition {index + 1} is not an object.";
                return false;
            }
            var sourceEdgeId = ReadString(transition, "id");
            var sourceFrom = ReadString(transition, "from");
            var sourceTo = ReadString(transition, "to");
            var action = transition["action"] as JsonObject;
            var semanticMeaning = action is null ? null : ReadString(action, "semanticMeaning");
            var automationId = action is null ? null : ReadString(action, "automationId");
            if (string.IsNullOrWhiteSpace(sourceEdgeId)
                || string.IsNullOrWhiteSpace(sourceFrom)
                || string.IsNullOrWhiteSpace(sourceTo)
                || string.IsNullOrWhiteSpace(semanticMeaning))
            {
                error = $"Exploration transition {index + 1} requires id, from, to, and semantic action values.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(automationId))
            {
                error = $"Exploration transition '{sourceEdgeId}' requires the interacted element's automation ID.";
                return false;
            }
            if (!destinationIds.TryGetValue(sourceFrom, out var from))
            {
                error = $"Exploration transition '{sourceEdgeId}' references unknown source destination '{sourceFrom}'.";
                return false;
            }
            if (!destinationIds.TryGetValue(sourceTo, out var to))
            {
                error = $"Exploration transition '{sourceEdgeId}' references unknown resulting destination '{sourceTo}'.";
                return false;
            }

            var edgeId = MakeUniqueId(NormalizeId(sourceEdgeId, "edge"), usedEdgeIds);
            var postconditions = ReadStringArray(transition, "postconditions");
            if (postconditions.Count == 0)
            {
                var targetName = (nodes.FirstOrDefault(node => node?["id"]?.GetValue<string>() == to) as JsonObject)?["name"]?.GetValue<string>();
                postconditions = [$"{targetName ?? "The resulting destination"} is visible"];
            }
            edges.Add(new JsonObject
            {
                ["id"] = edgeId,
                ["from"] = from,
                ["to"] = to,
                ["action"] = new JsonObject
                {
                    ["automationId"] = automationId.Trim(),
                    ["semanticMeaning"] = semanticMeaning.Trim()
                },
                ["parameters"] = JsonSerializer.SerializeToNode(ReadStringArray(transition, "parameters")),
                ["preconditions"] = JsonSerializer.SerializeToNode(ReadStringArray(transition, "preconditions")),
                ["postconditions"] = JsonSerializer.SerializeToNode(postconditions)
            });

            if (transition["binding"] is JsonObject binding
                && ReadString(binding, "mechanism") is { } mechanism
                && supportedBindingMechanisms.Contains(mechanism)
                && binding["configuration"] is JsonObject configuration)
            {
                var normalizedConfiguration = configuration.DeepClone().AsObject();
                if (string.Equals(mechanism, "ui_action", StringComparison.Ordinal))
                {
                    var selector = normalizedConfiguration["selector"] as JsonObject ?? new JsonObject();
                    selector["automationId"] ??= automationId.Trim();
                    normalizedConfiguration["selector"] = selector;
                }
                bindingCandidates.Add(new JsonObject
                {
                    ["edgeId"] = edgeId,
                    ["mechanism"] = mechanism,
                    ["configuration"] = normalizedConfiguration,
                    ["preconditions"] = JsonSerializer.SerializeToNode(ReadStringArray(transition, "preconditions")),
                    ["postconditions"] = JsonSerializer.SerializeToNode(postconditions),
                    ["confidence"] = ReadConfidence(binding, 0.5m)
                });
            }
        }

        var summary = ReadString(root, "summary")?.Trim();
        if (string.IsNullOrWhiteSpace(summary)) summary = $"Observed {nodes.Count} destinations and {edges.Count} element actions.";
        candidate = new AppGraphExplorationCandidate(
            new JsonObject
            {
                ["schema"] = "ansight.app-graph/v1",
                ["nodes"] = nodes,
                ["edges"] = edges,
                ["navigationHosts"] = navigationHosts,
                ["tabGroups"] = tabGroups
            },
            new JsonObject
            {
                ["schema"] = "ansight.app-graph-exploration-evidence/v1",
                ["summary"] = summary,
                ["bindingCandidates"] = bindingCandidates
            },
            ReadConfidence(root, 0.65m),
            summary,
            nodes.Count,
            edges.Count);
        return true;
    }

    private static bool TryValidateScreenParents(
        JsonArray nodes,
        JsonArray navigationHosts,
        out string error)
    {
        error = string.Empty;
        foreach (var node in nodes.OfType<JsonObject>())
        {
            if (!string.Equals(ReadString(node, "kind"), "screen", StringComparison.Ordinal)
                || ReadString(node, "parentScreen") is not { } parentScreen
                || string.IsNullOrWhiteSpace(parentScreen))
            {
                continue;
            }

            var nodeId = ReadString(node, "id");
            var hasMatchingHost = navigationHosts
                .OfType<JsonObject>()
                .Any(host =>
                    host["childDestinationIds"] is JsonArray childIds
                    && childIds.Any(child => string.Equals(
                        child?.GetValue<string>(),
                        nodeId,
                        StringComparison.Ordinal))
                    && nodes.OfType<JsonObject>().Any(hostNode =>
                        string.Equals(
                            ReadString(hostNode, "id"),
                            ReadString(host, "destinationId"),
                            StringComparison.Ordinal)
                        && string.Equals(
                            ReadString(hostNode, "name"),
                            parentScreen,
                            StringComparison.Ordinal)));
            if (hasMatchingHost)
            {
                continue;
            }

            error = $"Screen destination '{nodeId}' may only use parentScreen when it is declared as a child of that navigation host.";
            return false;
        }

        return true;
    }

    private static bool TryCompileNavigationStructure(
        JsonObject root,
        IReadOnlyDictionary<string, string> destinationIds,
        IReadOnlyDictionary<string, JsonObject> compiledNodesBySourceId,
        out JsonArray navigationHosts,
        out JsonArray tabGroups,
        out string error)
    {
        navigationHosts = new JsonArray();
        tabGroups = new JsonArray();
        error = string.Empty;
        var sourceNavigationHosts = root["navigationHosts"] as JsonArray ?? [];
        var sourceTabGroups = root["tabGroups"] as JsonArray ?? [];
        var usedHostIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in sourceNavigationHosts)
        {
            if (value is not JsonObject source)
            {
                error = "Every navigation host must be an object.";
                return false;
            }

            var id = ReadString(source, "id");
            var kind = ReadString(source, "kind")?.Trim().ToLowerInvariant();
            var name = ReadString(source, "name");
            var destinationId = ReadString(source, "destinationId");
            var activeChildDestinationId = ReadString(source, "activeChildDestinationId");
            var childDestinationIds = ReadStringArray(source, "childDestinationIds");
            if (string.IsNullOrWhiteSpace(id)
                || kind is not ("flyout" or "drawer" or "bottom_tabs" or "top_tabs" or "navigation_rail" or "shell" or "other")
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(destinationId)
                || string.IsNullOrWhiteSpace(activeChildDestinationId)
                || childDestinationIds.Count < 2)
            {
                error = "Every navigation host requires id, supported kind, name, destinationId, activeChildDestinationId, and at least two childDestinationIds.";
                return false;
            }
            if (!destinationIds.TryGetValue(destinationId, out var normalizedDestinationId)
                || !compiledNodesBySourceId.TryGetValue(destinationId, out var hostNode))
            {
                error = $"Navigation host '{id}' references unknown host destination '{destinationId}'.";
                return false;
            }
            if (!childDestinationIds.Contains(activeChildDestinationId, StringComparer.Ordinal))
            {
                error = $"Navigation host '{id}' does not include active child '{activeChildDestinationId}'.";
                return false;
            }

            var normalizedChildren = new JsonArray();
            foreach (var childDestinationId in childDestinationIds)
            {
                if (!destinationIds.TryGetValue(childDestinationId, out var normalizedChildId)
                    || !compiledNodesBySourceId.TryGetValue(childDestinationId, out var childNode))
                {
                    error = $"Navigation host '{id}' references unknown child destination '{childDestinationId}'.";
                    return false;
                }
                childNode["parentScreen"] = hostNode["name"]?.GetValue<string>() ?? destinationId;
                normalizedChildren.Add(normalizedChildId);
            }

            navigationHosts.Add(new JsonObject
            {
                ["id"] = MakeUniqueId(NormalizeId(id, "host"), usedHostIds),
                ["kind"] = kind,
                ["name"] = name.Trim(),
                ["destinationId"] = normalizedDestinationId,
                ["activeChildDestinationId"] = destinationIds[activeChildDestinationId],
                ["childDestinationIds"] = normalizedChildren,
                ["framework"] = ReadString(source, "framework")?.Trim() ?? "unknown",
                ["technology"] = source["technology"]?.DeepClone(),
                ["confidence"] = ReadConfidence(source, 0.8m)
            });
        }

        var usedTabGroupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in sourceTabGroups)
        {
            if (value is not JsonObject source)
            {
                error = "Every tab group must be an object.";
                return false;
            }

            var id = ReadString(source, "id");
            var parentDestinationId = ReadString(source, "parentDestinationId");
            var selectedDestinationId = ReadString(source, "selectedDestinationId");
            var tabDestinationIds = ReadStringArray(source, "tabDestinationIds");
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(parentDestinationId)
                || string.IsNullOrWhiteSpace(selectedDestinationId)
                || tabDestinationIds.Count < 2)
            {
                error = "Every tab group requires id, parentDestinationId, selectedDestinationId, and at least two tabDestinationIds.";
                return false;
            }
            if (!destinationIds.TryGetValue(parentDestinationId, out var normalizedParentId)
                || !compiledNodesBySourceId.TryGetValue(parentDestinationId, out var parentNode))
            {
                error = $"Tab group '{id}' references unknown parent destination '{parentDestinationId}'.";
                return false;
            }
            if (!tabDestinationIds.Contains(selectedDestinationId, StringComparer.Ordinal))
            {
                error = $"Tab group '{id}' does not include selected tab '{selectedDestinationId}'.";
                return false;
            }

            var normalizedTabs = new JsonArray();
            foreach (var tabDestinationId in tabDestinationIds)
            {
                if (!destinationIds.TryGetValue(tabDestinationId, out var normalizedTabId)
                    || !compiledNodesBySourceId.TryGetValue(tabDestinationId, out var tabNode))
                {
                    error = $"Tab group '{id}' references unknown tab destination '{tabDestinationId}'.";
                    return false;
                }
                if (!string.Equals(tabNode["kind"]?.GetValue<string>(), "state", StringComparison.Ordinal))
                {
                    error = $"Internal tab destination '{tabDestinationId}' must use kind state.";
                    return false;
                }
                tabNode["parentScreen"] = parentNode["name"]?.GetValue<string>() ?? parentDestinationId;
                normalizedTabs.Add(normalizedTabId);
            }

            tabGroups.Add(new JsonObject
            {
                ["id"] = MakeUniqueId(NormalizeId(id, "tabs"), usedTabGroupIds),
                ["parentDestinationId"] = normalizedParentId,
                ["selectedDestinationId"] = destinationIds[selectedDestinationId],
                ["tabDestinationIds"] = normalizedTabs,
                ["technology"] = source["technology"]?.DeepClone(),
                ["confidence"] = ReadConfidence(source, 0.8m)
            });
        }

        return true;
    }

    public static JsonObject AttachAudit(JsonObject evidence, SimulatorAgentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(result);
        var toolCalls = new JsonArray();
        foreach (var call in result.Audit.ToolCalls.Where(static call => call.IsAnsightTool).Take(96))
        {
            toolCalls.Add(new JsonObject
            {
                ["sequence"] = call.Sequence,
                ["turn"] = call.InstructionTurn,
                ["tool"] = call.ToolName,
                ["isError"] = call.IsError,
                ["message"] = call.Message,
                ["arguments"] = Truncate(call.Arguments.Content, 4_000),
                ["argumentsSha256"] = call.Arguments.Sha256,
                ["result"] = Truncate(call.Result.Content, 8_000),
                ["resultSha256"] = call.Result.Sha256
            });
        }

        var enriched = evidence.DeepClone().AsObject();
        enriched["audit"] = new JsonObject
        {
            ["runId"] = result.Audit.RunId,
            ["status"] = result.Status.ToString().ToLowerInvariant(),
            ["model"] = result.Audit.Model,
            ["turns"] = result.TotalTurns,
            ["toolCallCount"] = result.TotalToolCalls,
            ["successfulAnsightToolCallCount"] = result.Audit.SuccessfulAnsightToolCallCount,
            ["failedAnsightToolCallCount"] = result.Audit.FailedAnsightToolCallCount,
            ["durationMilliseconds"] = (long)result.Duration.TotalMilliseconds,
            ["localAuditFilePath"] = result.AuditFilePath,
            ["toolCalls"] = toolCalls
        };
        return enriched;
    }

    private static bool TryParseObject(string? value, out JsonObject result, out string error)
    {
        result = new JsonObject();
        error = string.Empty;
        var source = value?.Trim() ?? string.Empty;
        var firstBrace = source.IndexOf('{');
        var lastBrace = source.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            error = "The exploration agent did not return a JSON object in its completion summary.";
            return false;
        }
        try
        {
            result = JsonNode.Parse(source[firstBrace..(lastBrace + 1)]) as JsonObject ?? new JsonObject();
            return result.Count > 0;
        }
        catch (JsonException exception)
        {
            error = $"The exploration completion summary is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static string? ReadString(JsonObject source, string propertyName)
        => source[propertyName] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static IReadOnlyList<string> ReadStringArray(JsonObject source, string propertyName)
        => source[propertyName] is not JsonArray values
            ? []
            : values.Select(static value => value is JsonValue item && item.TryGetValue<string>(out var text) ? text.Trim() : string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static decimal ReadConfidence(JsonObject source, decimal fallback)
    {
        if (source["confidence"] is not JsonValue value) return fallback;
        if (value.TryGetValue<decimal>(out var decimalValue)) return Math.Clamp(decimalValue, 0m, 1m);
        return value.TryGetValue<double>(out var doubleValue)
            ? Math.Clamp((decimal)doubleValue, 0m, 1m)
            : fallback;
    }

    private static string NormalizeId(string source, string prefix)
    {
        var builder = new StringBuilder(source.Length + prefix.Length + 1);
        foreach (var character in source.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        var normalized = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(normalized)) normalized = prefix;
        return normalized.StartsWith(prefix + "_", StringComparison.Ordinal) ? normalized : $"{prefix}_{normalized}";
    }

    private static string MakeUniqueId(string source, ISet<string> used)
    {
        var candidate = source;
        var suffix = 2;
        while (!used.Add(candidate)) candidate = $"{source}_{suffix++}";
        return candidate;
    }

    private static string Truncate(string source, int maximumCharacters)
        => source.Length <= maximumCharacters ? source : source[..maximumCharacters] + "…";
}

internal sealed record AppGraphExplorationCandidate(
    JsonObject Definition,
    JsonObject Evidence,
    decimal Confidence,
    string Summary,
    int DestinationCount,
    int ElementActionCount);
