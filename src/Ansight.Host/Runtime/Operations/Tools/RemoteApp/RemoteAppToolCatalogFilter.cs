using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.RemoteApp;

internal static class RemoteAppToolCatalogFilter
{
    private const int DefaultMaximumResults = 20;
    private const int MaximumResultsLimit = 50;

    public static JsonNode? Apply(JsonNode? catalogPayload, JsonObject? arguments)
    {
        if (catalogPayload is not JsonObject catalog
            || catalog["tools"] is not JsonArray catalogTools)
        {
            return catalogPayload?.DeepClone();
        }

        var query = ReadString(arguments, "query");
        var feature = ReadString(arguments, "feature");
        var category = ReadString(arguments, "category");
        var idPrefix = ReadString(arguments, "idPrefix");
        var toolId = ReadString(arguments, "toolId");
        var policy = ReadString(arguments, "policy");
        var executableOnly = ReadBoolean(arguments, "executableOnly", fallback: true);
        var maxResults = Math.Clamp(
            ReadInteger(arguments, "maxResults", DefaultMaximumResults),
            1,
            MaximumResultsLimit);
        var queryTerms = SplitTerms(query);
        var featureTerms = SplitTerms(feature);
        var availableTools = catalogTools.OfType<JsonObject>().ToArray();
        var matches = availableTools
            .Where(tool => Matches(
                tool,
                queryTerms,
                featureTerms,
                category,
                idPrefix,
                toolId,
                policy,
                executableOnly))
            .ToArray();
        var boundedMatches = matches.Take(maxResults).ToArray();
        var prerequisiteToolIdsByToolId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var tool in boundedMatches)
        {
            if (ReadString(tool, "id") is { } matchedToolId)
            {
                prerequisiteToolIdsByToolId[matchedToolId] = FindPrerequisiteToolIds(tool, availableTools);
            }
        }
        var matchedToolIds = boundedMatches
            .Select(tool => ReadString(tool, "id"))
            .Where(static toolId => toolId is not null)
            .ToHashSet(StringComparer.Ordinal);
        var prerequisiteToolIds = prerequisiteToolIdsByToolId.Values
            .SelectMany(static toolIds => toolIds)
            .ToHashSet(StringComparer.Ordinal);
        var prerequisiteTools = availableTools
            .Where(tool => ReadString(tool, "id") is { } toolId
                           && prerequisiteToolIds.Contains(toolId)
                           && !matchedToolIds.Contains(toolId)
                           && MatchesPolicy(tool, policy: null, executableOnly: executableOnly))
            .ToArray();
        var returnedTools = boundedMatches
            .Concat(prerequisiteTools)
            .Select(tool => (JsonNode?)CloneWithPrerequisites(tool, prerequisiteToolIdsByToolId))
            .ToArray();
        var result = catalog.DeepClone().AsObject();
        result["tools"] = new JsonArray(returnedTools);
        result["totalToolCount"] = catalogTools.Count;
        result["matchedToolCount"] = matches.Length;
        result["includedPrerequisiteToolCount"] = returnedTools
            .OfType<JsonObject>()
            .Count(tool => ReadString(tool, "id") is { } toolId && !matchedToolIds.Contains(toolId));
        result["returnedToolCount"] = returnedTools.Length;
        result["isTruncated"] = matches.Length > boundedMatches.Length;
        result["filters"] = new JsonObject
        {
            ["query"] = query,
            ["feature"] = feature,
            ["category"] = category,
            ["idPrefix"] = idPrefix,
            ["toolId"] = toolId,
            ["policy"] = policy,
            ["executableOnly"] = executableOnly,
            ["maxResults"] = maxResults
        };
        return result;
    }

    private static bool Matches(
        JsonObject tool,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> featureTerms,
        string? category,
        string? idPrefix,
        string? toolId,
        string? policy,
        bool executableOnly)
    {
        if (!MatchesPolicy(tool, policy, executableOnly))
        {
            return false;
        }

        var candidateToolId = ReadString(tool, "id");
        if (toolId is not null
            && !string.Equals(candidateToolId, toolId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (idPrefix is not null
            && (candidateToolId is null
                || !candidateToolId.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (category is not null
            && !string.Equals(ReadString(tool, "category"), category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var searchableText = NormalizeSearchText(string.Join(
            ' ',
            ReadString(tool, "id"),
            ReadString(tool, "name"),
            ReadString(tool, "description"),
            ReadString(tool, "category"),
            ReadString(tool, "keywords"),
            tool["argumentsSchema"]?.ToJsonString(),
            tool["resultSchema"]?.ToJsonString()));
        return queryTerms.All(searchableText.Contains)
               && featureTerms.All(searchableText.Contains);
    }

    private static bool MatchesPolicy(JsonObject tool, string? policy, bool executableOnly)
    {
        if (policy is not null
            && !string.Equals(ReadString(tool, "policy"), policy, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !executableOnly
               || ReadBoolean(tool, "executable", fallback: true)
               && tool["denial"] is not JsonObject;
    }

    private static IReadOnlyList<string> FindPrerequisiteToolIds(
        JsonObject tool,
        IReadOnlyList<JsonObject> availableTools)
    {
        if (tool["prerequisiteToolIds"] is JsonArray declaredPrerequisites)
        {
            return declaredPrerequisites
                .Select(ReadString)
                .Where(static toolId => toolId is not null)
                .Select(static toolId => toolId!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var argumentsSchema = tool["argumentsSchema"]?.ToJsonString();
        if (string.IsNullOrWhiteSpace(argumentsSchema))
        {
            return [];
        }

        var toolId = ReadString(tool, "id");
        return availableTools
            .Select(candidate => ReadString(candidate, "id"))
            .Where(candidateId => candidateId is not null
                                  && !string.Equals(candidateId, toolId, StringComparison.Ordinal)
                                  && argumentsSchema.Contains(candidateId, StringComparison.OrdinalIgnoreCase))
            .Select(static candidateId => candidateId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static JsonObject CloneWithPrerequisites(
        JsonObject tool,
        IReadOnlyDictionary<string, IReadOnlyList<string>> prerequisiteToolIdsByToolId)
    {
        var clone = tool.DeepClone().AsObject();
        var toolId = ReadString(tool, "id");
        if (toolId is not null
            && prerequisiteToolIdsByToolId.TryGetValue(toolId, out var prerequisiteToolIds)
            && prerequisiteToolIds.Count > 0)
        {
            clone["prerequisiteToolIds"] = new JsonArray(
                prerequisiteToolIds.Select(static prerequisiteToolId => (JsonNode?)prerequisiteToolId).ToArray());
        }

        return clone;
    }

    private static IReadOnlyList<string> SplitTerms(string? value)
        => NormalizeSearchText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
                .ToLowerInvariant()
                .Select(static character => char.IsLetterOrDigit(character) ? character : ' ')
                .ToArray())
            .Replace("  ", " ", StringComparison.Ordinal);
    }

    private static string? ReadString(JsonObject? value, string propertyName)
        => value?[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static string? ReadString(JsonNode? value)
        => value is JsonValue property
           && property.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static int ReadInteger(JsonObject? value, string propertyName, int fallback)
        => value?[propertyName] is JsonValue property
           && property.TryGetValue<int>(out var number)
            ? number
            : fallback;

    private static bool ReadBoolean(JsonObject? value, string propertyName, bool fallback)
        => value?[propertyName] is JsonValue property
           && property.TryGetValue<bool>(out var result)
            ? result
            : fallback;
}
