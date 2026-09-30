using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.AppGraphs;

public static class AppGraphExplorationSummaryValidator
{
    public const string ExplorationSchema = "ansight.app-graph-exploration/v1";

    private static readonly HashSet<string> SupportedNavigationHostKinds = new(
        ["flyout", "drawer", "bottom_tabs", "top_tabs", "navigation_rail", "shell", "other"],
        StringComparer.Ordinal);

    public static bool TryValidate(string? completionSummary, out string error)
    {
        if (!TryParseObject(completionSummary, out var root, out error))
        {
            return false;
        }

        if (!string.Equals(ReadString(root, "schema"), ExplorationSchema, StringComparison.Ordinal))
        {
            error = $"The exploration summary must use schema '{ExplorationSchema}'.";
            return false;
        }

        if (root["destinations"] is not JsonArray destinations || destinations.Count == 0)
        {
            error = "The exploration did not return any observed destinations.";
            return false;
        }
        if (root["transitions"] is not JsonArray transitions)
        {
            error = "The exploration summary must include a transitions array.";
            return false;
        }

        var destinationsById = new Dictionary<string, DestinationContract>(StringComparer.Ordinal);
        for (var index = 0; index < destinations.Count; index++)
        {
            if (destinations[index] is not JsonObject destination)
            {
                error = $"Exploration destination {index + 1} is not an object.";
                return false;
            }

            var id = ReadString(destination, "id");
            var name = ReadString(destination, "name");
            var purpose = ReadString(destination, "purpose");
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(purpose))
            {
                error = $"Exploration destination {index + 1} requires id, name, and purpose.";
                return false;
            }
            if (destinationsById.ContainsKey(id))
            {
                error = $"Exploration destination id '{id}' is duplicated.";
                return false;
            }

            var kind = ReadString(destination, "kind")?.Trim().ToLowerInvariant();
            if (kind is not ("screen" or "dialog" or "state"))
            {
                error = $"Exploration destination '{id}' must use kind screen, dialog, or state.";
                return false;
            }

            var parentScreen = ReadString(destination, "parentScreen");
            if (kind == "state" && string.IsNullOrWhiteSpace(parentScreen))
            {
                error = $"Named state '{id}' requires its containing screen in parentScreen.";
                return false;
            }
            if (kind == "dialog" && !string.IsNullOrWhiteSpace(parentScreen))
            {
                error = $"Dialog destination '{id}' cannot belong to a navigation host or containing screen.";
                return false;
            }

            destinationsById[id] = new DestinationContract(kind, parentScreen);
        }

        var navigationHostChildren = new HashSet<string>(StringComparer.Ordinal);
        if (!TryValidateNavigationHosts(
                root["navigationHosts"] as JsonArray ?? [],
                destinationsById,
                navigationHostChildren,
                out error))
        {
            return false;
        }

        foreach (var (destinationId, destination) in destinationsById)
        {
            if (destination.Kind == "screen"
                && !string.IsNullOrWhiteSpace(destination.ParentScreen)
                && !navigationHostChildren.Contains(destinationId))
            {
                error = $"Screen destination '{destinationId}' may only use parentScreen when it is declared as a child of that navigation host.";
                return false;
            }
        }

        if (!TryValidateTabGroups(
                root["tabGroups"] as JsonArray ?? [],
                destinationsById,
                out error))
        {
            return false;
        }

        for (var index = 0; index < transitions.Count; index++)
        {
            if (transitions[index] is not JsonObject transition)
            {
                error = $"Exploration transition {index + 1} is not an object.";
                return false;
            }

            var id = ReadString(transition, "id");
            var from = ReadString(transition, "from");
            var to = ReadString(transition, "to");
            var action = transition["action"] as JsonObject;
            var automationId = action is null ? null : ReadString(action, "automationId");
            var semanticMeaning = action is null ? null : ReadString(action, "semanticMeaning");
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(from)
                || string.IsNullOrWhiteSpace(to)
                || string.IsNullOrWhiteSpace(semanticMeaning))
            {
                error = $"Exploration transition {index + 1} requires id, from, to, and semantic action values.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(automationId))
            {
                error = $"Exploration transition '{id}' requires the interacted element's automation ID.";
                return false;
            }
            if (!destinationsById.ContainsKey(from))
            {
                error = $"Exploration transition '{id}' references unknown source destination '{from}'.";
                return false;
            }
            if (!destinationsById.ContainsKey(to))
            {
                error = $"Exploration transition '{id}' references unknown resulting destination '{to}'.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateNavigationHosts(
        JsonArray hosts,
        IReadOnlyDictionary<string, DestinationContract> destinationsById,
        ISet<string> navigationHostChildren,
        out string error)
    {
        foreach (var value in hosts)
        {
            if (value is not JsonObject host)
            {
                error = "Every navigation host must be an object.";
                return false;
            }

            var id = ReadString(host, "id");
            var kind = ReadString(host, "kind")?.Trim().ToLowerInvariant();
            var name = ReadString(host, "name");
            var destinationId = ReadString(host, "destinationId");
            var activeChildDestinationId = ReadString(host, "activeChildDestinationId");
            var childDestinationIds = ReadStringArray(host, "childDestinationIds");
            if (string.IsNullOrWhiteSpace(id)
                || kind is null
                || !SupportedNavigationHostKinds.Contains(kind)
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(destinationId)
                || string.IsNullOrWhiteSpace(activeChildDestinationId)
                || childDestinationIds.Count < 2)
            {
                error = "Every navigation host requires id, supported kind, name, destinationId, activeChildDestinationId, and at least two childDestinationIds.";
                return false;
            }
            if (!AppGraphNavigationTechnologyCatalog.TryReadAndValidate(
                    host["technology"] as JsonObject,
                    AppGraphNavigationTechnologyCatalog.NavigationHostScope,
                    out var technology,
                    out var normalizedRole,
                    out error))
            {
                error = $"Navigation host '{id}': {error}";
                return false;
            }
            if (!string.Equals(kind, normalizedRole, StringComparison.Ordinal)
                || !string.Equals(
                    ReadString(host, "framework")?.Trim(),
                    technology!.Framework.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = $"Navigation host '{id}' compatibility kind/framework must match its technology descriptor.";
                return false;
            }
            if (!destinationsById.ContainsKey(destinationId))
            {
                error = $"Navigation host '{id}' references unknown host destination '{destinationId}'.";
                return false;
            }
            if (!childDestinationIds.Contains(activeChildDestinationId, StringComparer.Ordinal))
            {
                error = $"Navigation host '{id}' does not include active child '{activeChildDestinationId}'.";
                return false;
            }

            foreach (var childDestinationId in childDestinationIds)
            {
                if (!destinationsById.ContainsKey(childDestinationId))
                {
                    error = $"Navigation host '{id}' references unknown child destination '{childDestinationId}'.";
                    return false;
                }
                navigationHostChildren.Add(childDestinationId);
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateTabGroups(
        JsonArray tabGroups,
        IReadOnlyDictionary<string, DestinationContract> destinationsById,
        out string error)
    {
        foreach (var value in tabGroups)
        {
            if (value is not JsonObject tabGroup)
            {
                error = "Every tab group must be an object.";
                return false;
            }

            var id = ReadString(tabGroup, "id");
            var parentDestinationId = ReadString(tabGroup, "parentDestinationId");
            var selectedDestinationId = ReadString(tabGroup, "selectedDestinationId");
            var tabDestinationIds = ReadStringArray(tabGroup, "tabDestinationIds");
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(parentDestinationId)
                || string.IsNullOrWhiteSpace(selectedDestinationId)
                || tabDestinationIds.Count < 2)
            {
                error = "Every tab group requires id, parentDestinationId, selectedDestinationId, and at least two tabDestinationIds.";
                return false;
            }
            if (!AppGraphNavigationTechnologyCatalog.TryReadAndValidate(
                    tabGroup["technology"] as JsonObject,
                    AppGraphNavigationTechnologyCatalog.TabGroupScope,
                    out _,
                    out _,
                    out error))
            {
                error = $"Tab group '{id}': {error}";
                return false;
            }
            if (!destinationsById.ContainsKey(parentDestinationId))
            {
                error = $"Tab group '{id}' references unknown parent destination '{parentDestinationId}'.";
                return false;
            }
            if (!tabDestinationIds.Contains(selectedDestinationId, StringComparer.Ordinal))
            {
                error = $"Tab group '{id}' does not include selected tab '{selectedDestinationId}'.";
                return false;
            }

            foreach (var tabDestinationId in tabDestinationIds)
            {
                if (!destinationsById.TryGetValue(tabDestinationId, out var destination))
                {
                    error = $"Tab group '{id}' references unknown tab destination '{tabDestinationId}'.";
                    return false;
                }
                if (destination.Kind != "state")
                {
                    error = $"Internal tab destination '{tabDestinationId}' must use kind state.";
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseObject(string? value, out JsonObject result, out string error)
    {
        result = new JsonObject();
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
            if (result.Count > 0)
            {
                error = string.Empty;
                return true;
            }

            error = "The exploration agent did not return a JSON object in its completion summary.";
            return false;
        }
        catch (JsonException exception)
        {
            error = $"The exploration completion summary is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static string? ReadString(JsonObject source, string propertyName)
        => source[propertyName] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonObject source, string propertyName)
        => source[propertyName] is not JsonArray values
            ? []
            : values
                .Select(static value => value is JsonValue item && item.TryGetValue<string>(out var text)
                    ? text.Trim()
                    : string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private sealed record DestinationContract(string Kind, string? ParentScreen);
}
