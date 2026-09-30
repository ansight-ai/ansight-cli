using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Cloud;

namespace Ansight.Cli.AppGraphs;

internal static class AppGraphPlanner
{
    public static AppGraphValidation Validate(CloudAppGraphDetail detail)
    {
        var errors = new List<string>();
        var definition = DeserializeDefinition(detail, errors);
        if (definition is null) return new AppGraphValidation(errors, 0, detail.Bindings.Count);

        var nodes = new Dictionary<string, AppGraphDocumentNode>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id)) errors.Add("Every node must have an ID.");
            else if (!nodes.TryAdd(node.Id, node)) errors.Add($"Node ID '{node.Id}' is duplicated.");
            if (node.Kind is not ("screen" or "dialog" or "state"))
            {
                errors.Add($"Destination '{node.Id}' must be a screen, dialog, or named state.");
            }
            if (string.IsNullOrWhiteSpace(node.Name))
            {
                errors.Add($"Destination '{node.Id}' must have a canonical name.");
            }
            if (node.Synonyms is null)
            {
                errors.Add($"Destination '{node.Id}' must provide a synonyms array, even when it is empty.");
            }
            if (string.IsNullOrWhiteSpace(node.Purpose))
            {
                errors.Add($"Destination '{node.Id}' must describe its purpose.");
            }
            if (node.Kind == "state" && string.IsNullOrWhiteSpace(node.ParentScreen))
            {
                errors.Add($"Named state '{node.Id}' must identify its containing screen.");
            }
            if (node.Kind == "dialog" && !string.IsNullOrWhiteSpace(node.ParentScreen))
            {
                errors.Add($"Dialog destination '{node.Id}' must remain top-level.");
            }
        }

        var navigationHostParentsByChildId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var host in definition.NavigationHosts)
        {
            if (string.IsNullOrWhiteSpace(host.Id)
                || string.IsNullOrWhiteSpace(host.DestinationId)
                || string.IsNullOrWhiteSpace(host.ActiveChildDestinationId)
                || host.ChildDestinationIds.Count < 2)
            {
                errors.Add("Every navigation host requires an ID, host destination, active child, and at least two child destinations.");
                continue;
            }
            if (!nodes.TryGetValue(host.DestinationId, out var hostDestination))
            {
                errors.Add($"Navigation host '{host.Id}' references missing host destination '{host.DestinationId}'.");
                continue;
            }
            if (!host.ChildDestinationIds.Contains(host.ActiveChildDestinationId, StringComparer.Ordinal))
            {
                errors.Add($"Navigation host '{host.Id}' does not include active child '{host.ActiveChildDestinationId}'.");
            }
            foreach (var childId in host.ChildDestinationIds)
            {
                if (!nodes.ContainsKey(childId))
                {
                    errors.Add($"Navigation host '{host.Id}' references missing child destination '{childId}'.");
                    continue;
                }
                if (!navigationHostParentsByChildId.TryGetValue(childId, out var parentReferences))
                {
                    parentReferences = new HashSet<string>(StringComparer.Ordinal);
                    navigationHostParentsByChildId[childId] = parentReferences;
                }
                parentReferences.Add(host.DestinationId);
                parentReferences.Add(hostDestination.Name);
            }
        }
        foreach (var node in definition.Nodes.Where(node => node.Kind == "screen" && !string.IsNullOrWhiteSpace(node.ParentScreen)))
        {
            if (!navigationHostParentsByChildId.TryGetValue(node.Id, out var parentReferences)
                || !parentReferences.Contains(node.ParentScreen!))
            {
                errors.Add($"Screen destination '{node.Id}' may only identify a parent when that navigation host declares it as a child.");
            }
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in definition.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id)) errors.Add("Every edge must have an ID.");
            else if (!edgeIds.Add(edge.Id)) errors.Add($"Edge ID '{edge.Id}' is duplicated.");
            if (!nodes.ContainsKey(edge.From)) errors.Add($"Edge '{edge.Id}' references missing source node '{edge.From}'.");
            if (!nodes.ContainsKey(edge.To)) errors.Add($"Edge '{edge.Id}' references missing target node '{edge.To}'.");
            if (string.IsNullOrWhiteSpace(edge.Action?.SemanticMeaning))
            {
                errors.Add($"Edge '{edge.Id}' must describe the element action's semantic meaning.");
            }
            if (string.IsNullOrWhiteSpace(edge.Action?.AutomationId))
            {
                errors.Add($"Edge '{edge.Id}' must identify the element automation ID.");
            }

            var bindings = detail.Bindings.Where(binding => binding.EdgeId == edge.Id).OrderBy(binding => binding.Priority).ToArray();
            if (bindings.Length == 0)
            {
                errors.Add($"Edge '{edge.Id}' has no execution binding.");
                continue;
            }
            if (bindings.All(binding => binding.Postconditions.Length == 0) && edge.Postconditions.Count == 0)
            {
                errors.Add($"Edge '{edge.Id}' has no observable postcondition.");
            }
            foreach (var binding in bindings) ValidateBinding(edge, binding, errors);
        }

        return new AppGraphValidation(errors, definition.Edges.Count, detail.Bindings.Count);
    }

    public static AppGraphPlan Build(CloudAppGraphDetail detail, string? requestedTarget)
    {
        var validation = Validate(detail);
        var errors = validation.Errors.ToList();
        var definition = DeserializeDefinition(detail, errors);
        if (definition is null || errors.Count > 0) return new AppGraphPlan(errors, []);

        var nodes = definition.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var incomingNodeIds = definition.Edges.Select(edge => edge.To).ToHashSet(StringComparer.Ordinal);
        var outgoingNodeIds = definition.Edges.Select(edge => edge.From).ToHashSet(StringComparer.Ordinal);
        var explicitStarts = definition.Nodes.Where(node => node.IsEntry).ToArray();
        var inferredStarts = definition.Nodes.Where(node => !incomingNodeIds.Contains(node.Id)).ToArray();
        var starts = explicitStarts.Length > 0
            ? explicitStarts
            : inferredStarts.Length > 0
                ? inferredStarts
                : definition.Nodes.Take(1).ToArray();
        var targets = ResolveTargets(definition, requestedTarget, outgoingNodeIds);
        if (starts.Length == 0) errors.Add("The graph has no entry destination.");
        if (targets.Count == 0) errors.Add("The graph has no matching destination.");
        if (errors.Count > 0) return new AppGraphPlan(errors, []);

        var targetIds = targets.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var outgoing = definition.Edges.GroupBy(edge => edge.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var queue = new Queue<string>();
        var previous = new Dictionary<string, AppGraphDocumentEdge>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in starts)
        {
            queue.Enqueue(start.Id);
            visited.Add(start.Id);
        }

        string? reachedTarget = null;
        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            if (targetIds.Contains(nodeId))
            {
                reachedTarget = nodeId;
                break;
            }
            if (!outgoing.TryGetValue(nodeId, out var edges)) continue;
            foreach (var edge in edges)
            {
                if (!visited.Add(edge.To)) continue;
                previous[edge.To] = edge;
                queue.Enqueue(edge.To);
            }
        }

        if (reachedTarget is null)
        {
            errors.Add("No element-action path connects an entry destination to the requested destination.");
            return new AppGraphPlan(errors, []);
        }

        var path = new List<AppGraphDocumentEdge>();
        var cursor = reachedTarget;
        while (previous.TryGetValue(cursor, out var edge))
        {
            path.Add(edge);
            cursor = edge.From;
        }
        path.Reverse();
        var steps = path.Select(edge => new AppGraphPlanStep(
            edge,
            nodes[edge.From],
            nodes[edge.To],
            detail.Bindings.Where(binding => binding.EdgeId == edge.Id).OrderBy(binding => binding.Priority).ToArray())).ToArray();
        return new AppGraphPlan(errors, steps);
    }

    private static AppGraphDocument? DeserializeDefinition(CloudAppGraphDetail detail, List<string> errors)
    {
        try
        {
            var definition = detail.Version.Definition.Deserialize<AppGraphDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (definition is null)
            {
                errors.Add("The graph version definition is empty.");
                return null;
            }
            if (!string.Equals(definition.Schema, "ansight.app-graph/v1", StringComparison.Ordinal))
            {
                errors.Add($"Unsupported App Graph schema '{definition.Schema}'.");
            }
            return definition;
        }
        catch (JsonException exception)
        {
            errors.Add($"The graph version definition is invalid: {exception.Message}");
            return null;
        }
    }

    private static IReadOnlyList<AppGraphDocumentNode> ResolveTargets(
        AppGraphDocument definition,
        string? requestedTarget,
        HashSet<string> outgoingNodeIds)
    {
        if (!string.IsNullOrWhiteSpace(requestedTarget))
        {
            return definition.Nodes.Where(node =>
                node.Id.Equals(requestedTarget.Trim(), StringComparison.OrdinalIgnoreCase)
                || node.Name.Contains(requestedTarget.Trim(), StringComparison.OrdinalIgnoreCase)
                || node.Synonyms?.Any(synonym => synonym.Contains(requestedTarget.Trim(), StringComparison.OrdinalIgnoreCase)) == true
                || node.Purpose?.Contains(requestedTarget.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToArray();
        }
        return definition.Nodes.Where(node => !outgoingNodeIds.Contains(node.Id)).ToArray();
    }

    private static void ValidateBinding(
        AppGraphDocumentEdge edge,
        CloudAppGraphBinding binding,
        List<string> errors)
    {
        if (binding.Mechanism is not ("app_link" or "native_route" or "app_tool" or "ui_action"))
        {
            errors.Add($"Binding '{binding.Id:D}' on edge '{edge.Id}' uses unsupported mechanism '{binding.Mechanism}'.");
            return;
        }
        var requiredConfigurationProperty = binding.Mechanism switch
        {
            "app_link" => "url",
            "native_route" => "route",
            "app_tool" => "toolId",
            _ => "action"
        };
        if (binding.Configuration[requiredConfigurationProperty] is null)
        {
            errors.Add($"Binding '{binding.Id:D}' on edge '{edge.Id}' requires configuration.{requiredConfigurationProperty}.");
        }
        if (binding.Mechanism == "ui_action" && !string.IsNullOrWhiteSpace(edge.Action?.AutomationId))
        {
            var selector = binding.Configuration["selector"] as JsonObject;
            var automationId = selector?["automationId"] is JsonValue value
                               && value.TryGetValue<string>(out var text)
                ? text
                : null;
            if (!string.Equals(automationId, edge.Action.AutomationId, StringComparison.Ordinal))
            {
                errors.Add(
                    $"UI action binding '{binding.Id:D}' on edge '{edge.Id}' must target automation ID '{edge.Action.AutomationId}'.");
            }
        }
    }
}
