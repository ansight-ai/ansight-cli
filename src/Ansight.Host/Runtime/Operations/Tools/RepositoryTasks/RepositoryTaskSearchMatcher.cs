using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal static class RepositoryTaskSearchMatcher
{
    private const int MaximumBehavioralSynonyms = 24;
    private const int MaximumIndexedTermsPerValue = 128;
    private const int MaximumQueryTerms = 32;
    private const int MaximumTermLength = 64;
    private const double MinimumQueryCoverage = 0.4;
    private const double EntityMismatchPenalty = 18;
    private static readonly HashSet<string> ignoredQueryWords = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "at",
        "by",
        "for",
        "from",
        "in",
        "into",
        "it",
        "its",
        "of",
        "on",
        "or",
        "that",
        "the",
        "then",
        "to",
        "use",
        "using",
        "with"
    };
    private static readonly IReadOnlyDictionary<string, HashSet<string>> behavioralSynonyms =
        CreateBehavioralSynonyms();
    private static readonly HashSet<string> genericEntityWords = CreateGenericEntityWords();
    private static readonly HashSet<string> taskContextWords = CreateTaskContextWords();

    public static RepositoryTaskSearchMatch? Match(RepositoryTaskDefinition task, string? query)
    {
        var evaluation = Evaluate(task, query);
        return evaluation.ExclusionReason is null ? evaluation : null;
    }

    internal static RepositoryTaskSearchMatch Evaluate(RepositoryTaskDefinition task, string? query)
    {
        ArgumentNullException.ThrowIfNull(task);

        var indexedTerms = BuildIndexedTerms(task);
        var taskSynonyms = BuildTaskSynonyms(indexedTerms);
        var hasParameterizedIntent = HasStringInput(task.InputSchema)
            && HasTaskIntentMatch(task.TaskId, task.Title, task.Feature, query, task.Keywords);
        var queryTerms = Tokenize(query, MaximumQueryTerms)
            .Where(term => !ignoredQueryWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (queryTerms.Length == 0)
        {
            return new RepositoryTaskSearchMatch(
                task,
                0,
                1,
                [],
                [],
                [],
                taskSynonyms);
        }

        var matchedEvidence = new List<RepositoryTaskSearchEvidence>();
        var unmatchedTerms = new List<string>();
        var totalQueryWeight = 0d;
        var matchedQueryWeight = 0d;
        var weightedMatchQuality = 0d;
        foreach (var queryTerm in queryTerms)
        {
            var queryWeight = behavioralSynonyms.ContainsKey(queryTerm) ? 1.2 : 1;
            totalQueryWeight += queryWeight;
            var evidence = FindBestEvidence(queryTerm, indexedTerms);
            if (evidence is null)
            {
                unmatchedTerms.Add(queryTerm);
                // A parameterized task describes the operation, not every possible input value.
                // Keep unknown values in the evidence, but limit their ranking cost only when
                // the query independently names the task's intent. Actions keep their full weight.
                if (hasParameterizedIntent && !behavioralSynonyms.ContainsKey(queryTerm))
                {
                    totalQueryWeight -= queryWeight * 0.8;
                }
                continue;
            }

            matchedEvidence.Add(evidence);
            matchedQueryWeight += queryWeight;
            weightedMatchQuality += queryWeight * evidence.MatchStrength * evidence.IndexedWeight;
        }

        var minimumMatchedTerms = queryTerms.Length >= 3 ? 2 : 1;
        var coverage = totalQueryWeight == 0 ? 0 : matchedQueryWeight / totalQueryWeight;
        var exclusionReason = matchedEvidence.Count < minimumMatchedTerms
            ? "insufficient-matched-terms"
            : coverage < MinimumQueryCoverage ? "below-query-coverage" : null;

        var matchQuality = matchedQueryWeight == 0
            ? 0
            : weightedMatchQuality / matchedQueryWeight;
        var entityMismatchPenalty = HasEntityMismatch(task, queryTerms)
            ? EntityMismatchPenalty
            : 0;
        var score = Math.Round(
            Math.Max(
                0,
                (((coverage * 0.75) + (matchQuality * 0.25)) * 100)
                - entityMismatchPenalty),
            1);
        return new RepositoryTaskSearchMatch(
            task,
            score,
            Math.Round(coverage, 3),
            matchedEvidence.Select(evidence => evidence.QueryTerm).ToArray(),
            unmatchedTerms,
            matchedEvidence,
            taskSynonyms)
        {
            ExclusionReason = exclusionReason
        };
    }

    internal static bool HasTaskIntentMatch(
        string taskId,
        string title,
        string? feature,
        string? query,
        IReadOnlyList<string>? keywords = null)
    {
        if (!string.IsNullOrWhiteSpace(query) && Regex.IsMatch(
                query,
                $@"(?<![\w.-]){Regex.Escape(taskId)}(?![\w.-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        var queryTerms = Tokenize(query, 4096);
        var intentTerms = new[] { feature, taskId, title }
            .Concat(keywords ?? [])
            .SelectMany(value => Tokenize(value, MaximumIndexedTermsPerValue))
            .Where(term => !taskContextWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Tasks with only generic metadata remain discoverable; there is no specific intent
        // to disprove. A domain-specific task needs more than incidental open/page overlap.
        return intentTerms.Length == 0 || intentTerms.Any(intent => queryTerms.Any(term =>
            CreateEvidence(term, new RepositoryTaskIndexedTerm(intent, 1, "feature")) is
                { Kind: not RepositoryTaskSearchMatchKind.Fuzzy }
                or { Kind: RepositoryTaskSearchMatchKind.Fuzzy, EditDistance: 1 }));
    }

    private static bool HasStringInput(JsonObject inputSchema)
        => inputSchema["properties"] is JsonObject properties
           && properties.Any(property => property.Value is JsonObject propertySchema
               && propertySchema["type"] is JsonValue type
               && type.TryGetValue<string>(out var value) && value == "string");

    private static HashSet<string> CreateTaskContextWords()
    {
        var words = new HashSet<string>(ignoredQueryWords, StringComparer.Ordinal)
        {
            "app", "area", "card", "complete", "current", "exact", "focus", "focused", "load", "map", "my",
            "named", "open", "page", "ready", "screen", "select", "selected", "selection", "stable", "state", "task",
            "ui", "validate", "verify", "workflow", "your"
        };
        words.UnionWith(behavioralSynonyms["open"]);
        words.UnionWith(behavioralSynonyms["validate"]);
        words.UnionWith(behavioralSynonyms["select"]);
        return words;
    }

    private static bool HasEntityMismatch(
        RepositoryTaskDefinition task,
        IReadOnlyList<string> queryTerms)
    {
        var taskEntities = BuildTaskEntities(task);
        return taskEntities.Count > 0
               && !taskEntities.Any(entity => entity.All(entityTerm => queryTerms.Any(
                   queryTerm => AreTermsEquivalent(entityTerm, queryTerm))));
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildTaskEntities(
        RepositoryTaskDefinition task)
    {
        var entities = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var keyword in task.Keywords)
        {
            AddTaskEntity(entities, keyword);
        }

        AddInputSchemaEntities(entities, task.InputSchema);
        return entities.Values.ToArray();
    }

    private static void AddInputSchemaEntities(
        IDictionary<string, IReadOnlyList<string>> entities,
        JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode)
            {
                if (property.Key is "default" or "const"
                    && property.Value is JsonValue value
                    && value.TryGetValue<string>(out var scalar))
                {
                    AddTaskEntity(entities, scalar, allowSingleTerm: true);
                }
                else if (property.Key == "enum" && property.Value is JsonArray enumValues)
                {
                    foreach (var enumValue in enumValues.OfType<JsonValue>())
                    {
                        if (enumValue.TryGetValue<string>(out var enumScalar))
                        {
                            AddTaskEntity(entities, enumScalar, allowSingleTerm: true);
                        }
                    }
                }

                AddInputSchemaEntities(entities, property.Value);
            }
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (var item in arrayNode)
            {
                AddInputSchemaEntities(entities, item);
            }
        }
    }

    private static void AddTaskEntity(
        IDictionary<string, IReadOnlyList<string>> entities,
        string? value,
        bool allowSingleTerm = false)
    {
        var terms = Tokenize(value, 8)
            .Where(term => !ignoredQueryWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0
            || (!allowSingleTerm && terms.Length < 2)
            || terms.All(genericEntityWords.Contains))
        {
            return;
        }

        entities.TryAdd(string.Join(' ', terms), terms);
    }

    private static bool AreTermsEquivalent(string left, string right)
    {
        var evidence = CreateEvidence(
            left,
            new RepositoryTaskIndexedTerm(right, 1, "query"));
        return evidence is not null && evidence.MatchStrength >= 0.8;
    }

    private static IReadOnlyList<RepositoryTaskIndexedTerm> BuildIndexedTerms(RepositoryTaskDefinition task)
    {
        var indexedTerms = new Dictionary<string, RepositoryTaskIndexedTerm>(StringComparer.Ordinal);
        AddIndexedTerms(indexedTerms, task.TaskId, 1, "taskId");
        AddIndexedTerms(indexedTerms, task.Title, 1, "title");
        AddIndexedTerms(indexedTerms, task.Feature, 1, "feature");
        foreach (var keyword in task.Keywords)
        {
            AddIndexedTerms(indexedTerms, keyword, 1, "keywords");
        }

        AddIndexedTerms(indexedTerms, task.Description, 0.95, "description");
        AddInputSchemaTerms(indexedTerms, task.InputSchema);
        return indexedTerms.Values.ToArray();
    }

    private static void AddInputSchemaTerms(
        IDictionary<string, RepositoryTaskIndexedTerm> indexedTerms,
        JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode)
            {
                if (property.Key is "const" or "default" or "title" or "description"
                    && property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    AddIndexedTerms(indexedTerms, text, 0.75, "inputSchema");
                }
                else if (property.Key == "enum" && property.Value is JsonArray values)
                {
                    foreach (var enumValue in values.OfType<JsonValue>())
                    {
                        if (enumValue.TryGetValue<string>(out var enumText))
                        {
                            AddIndexedTerms(indexedTerms, enumText, 0.75, "inputSchema");
                        }
                    }
                }
                AddInputSchemaTerms(indexedTerms, property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                AddInputSchemaTerms(indexedTerms, item);
            }
        }
    }

    private static void AddIndexedTerms(
        IDictionary<string, RepositoryTaskIndexedTerm> indexedTerms,
        string? value,
        double weight,
        string source)
    {
        foreach (var term in Tokenize(value, MaximumIndexedTermsPerValue))
        {
            if (!indexedTerms.TryGetValue(term, out var existing) || existing.Weight < weight)
            {
                indexedTerms[term] = new RepositoryTaskIndexedTerm(term, weight, source);
            }
        }
    }

    private static RepositoryTaskSearchEvidence? FindBestEvidence(
        string queryTerm,
        IReadOnlyList<RepositoryTaskIndexedTerm> indexedTerms)
    {
        RepositoryTaskSearchEvidence? bestEvidence = null;
        foreach (var indexedTerm in indexedTerms)
        {
            var evidence = CreateEvidence(queryTerm, indexedTerm);
            if (evidence is null)
            {
                continue;
            }

            if (bestEvidence is null
                || (evidence.MatchStrength * evidence.IndexedWeight)
                > (bestEvidence.MatchStrength * bestEvidence.IndexedWeight))
            {
                bestEvidence = evidence;
            }

            if (evidence.Kind == RepositoryTaskSearchMatchKind.Exact
                && indexedTerm.Weight >= 1)
            {
                return evidence;
            }
        }

        return bestEvidence;
    }

    private static RepositoryTaskSearchEvidence? CreateEvidence(
        string queryTerm,
        RepositoryTaskIndexedTerm indexedTerm)
    {
        if (string.Equals(queryTerm, indexedTerm.Term, StringComparison.Ordinal))
        {
            return new RepositoryTaskSearchEvidence(
                queryTerm,
                indexedTerm.Term,
                indexedTerm.Source,
                RepositoryTaskSearchMatchKind.Exact,
                1,
                indexedTerm.Weight,
                null);
        }

        var shorterLength = Math.Min(queryTerm.Length, indexedTerm.Term.Length);
        var longerLength = Math.Max(queryTerm.Length, indexedTerm.Term.Length);
        if (shorterLength >= 3
            && (queryTerm.StartsWith(indexedTerm.Term, StringComparison.Ordinal)
                || indexedTerm.Term.StartsWith(queryTerm, StringComparison.Ordinal))
            && IsInflectedForm(queryTerm, indexedTerm.Term))
        {
            return new RepositoryTaskSearchEvidence(
                queryTerm,
                indexedTerm.Term,
                indexedTerm.Source,
                RepositoryTaskSearchMatchKind.Prefix,
                0.92,
                indexedTerm.Weight,
                null);
        }

        if (behavioralSynonyms.TryGetValue(queryTerm, out var synonyms)
            && synonyms.Contains(indexedTerm.Term))
        {
            return new RepositoryTaskSearchEvidence(
                queryTerm,
                indexedTerm.Term,
                indexedTerm.Source,
                RepositoryTaskSearchMatchKind.Synonym,
                0.86,
                indexedTerm.Weight,
                null);
        }

        // Incidental prose is too large a typo dictionary (e.g. Mountains -> contains).
        // Keep typo recovery on task identity, keywords and declared input values.
        var maximumDistance = indexedTerm.Source == "description"
            ? 0
            : MaximumFuzzyDistance(queryTerm.Length, indexedTerm.Term.Length);
        if (maximumDistance == 0)
        {
            return null;
        }

        var distance = ComputeLevenshteinDistance(queryTerm, indexedTerm.Term, maximumDistance);
        if (distance > maximumDistance)
        {
            return null;
        }

        var similarity = 1 - ((double)distance / longerLength);
        return new RepositoryTaskSearchEvidence(
            queryTerm,
            indexedTerm.Term,
            indexedTerm.Source,
            RepositoryTaskSearchMatchKind.Fuzzy,
            similarity * 0.9,
            indexedTerm.Weight,
            distance);
    }

    private static bool IsInflectedForm(string left, string right)
    {
        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length > right.Length ? left : right;
        return longer[shorter.Length..] is "s" or "es" or "ed" or "ing" or "ly"
            or "ion" or "ions" or "tion" or "tions" or "ation" or "ations";
    }

    private static int MaximumFuzzyDistance(int leftLength, int rightLength)
    {
        var shorterLength = Math.Min(leftLength, rightLength);
        var longerLength = Math.Max(leftLength, rightLength);
        if (shorterLength < 4 || longerLength - shorterLength > 2)
        {
            return 0;
        }

        return longerLength <= 5 ? 1 : 2;
    }

    private static int ComputeLevenshteinDistance(string left, string right, int maximumDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maximumDistance)
        {
            return maximumDistance + 1;
        }

        var previousRow = new int[right.Length + 1];
        var currentRow = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previousRow[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            currentRow[0] = row;
            var rowMinimum = currentRow[0];
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                currentRow[column] = Math.Min(
                    Math.Min(currentRow[column - 1] + 1, previousRow[column] + 1),
                    previousRow[column - 1] + substitutionCost);
                rowMinimum = Math.Min(rowMinimum, currentRow[column]);
            }

            if (rowMinimum > maximumDistance)
            {
                return maximumDistance + 1;
            }

            var completedRow = previousRow;
            previousRow = currentRow;
            currentRow = completedRow;
        }

        return previousRow[right.Length];
    }

    private static IReadOnlyList<string> BuildTaskSynonyms(
        IReadOnlyList<RepositoryTaskIndexedTerm> indexedTerms)
    {
        var indexedWords = indexedTerms
            .Select(indexedTerm => indexedTerm.Term)
            .ToHashSet(StringComparer.Ordinal);
        return indexedWords
            .Where(behavioralSynonyms.ContainsKey)
            .SelectMany(word => behavioralSynonyms[word])
            .Where(synonym => !indexedWords.Contains(synonym))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaximumBehavioralSynonyms)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, HashSet<string>> CreateBehavioralSynonyms()
    {
        string[][] groups =
        [
            ["search", "find", "locate", "lookup", "discover", "query"],
            ["open", "launch", "enter", "show", "view", "display", "visit", "navigate", "go"],
            ["select", "choose", "pick", "tap", "press", "activate"],
            ["close", "dismiss", "exit"],
            ["create", "add", "new"],
            ["edit", "update", "change", "modify"],
            ["delete", "remove", "clear"],
            ["save", "store", "persist"],
            ["download", "cache", "offline"],
            ["upload", "publish", "sync"],
            ["login", "signin", "authenticate"],
            ["logout", "signout"],
            ["validate", "verify", "check", "assert", "confirm", "test"],
            ["run", "execute", "start", "perform"],
            ["scroll", "swipe", "pan"],
            ["zoom", "pinch", "magnify"],
            ["wait", "await", "poll"],
            ["back", "return", "previous"],
            ["reset", "restore", "restart"],
            ["guide", "walkthrough", "tour", "viewer"],
            ["detail", "details", "information", "info", "overview"]
        ];
        var synonymsByWord = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var word in group)
            {
                if (!synonymsByWord.TryGetValue(word, out var synonyms))
                {
                    synonyms = new HashSet<string>(StringComparer.Ordinal);
                    synonymsByWord[word] = synonyms;
                }

                synonyms.UnionWith(group.Where(candidate => !string.Equals(
                    candidate,
                    word,
                    StringComparison.Ordinal)));
            }
        }

        return synonymsByWord;
    }

    private static HashSet<string> CreateGenericEntityWords()
    {
        var words = new HashSet<string>(ignoredQueryWords, StringComparer.Ordinal);
        words.UnionWith(behavioralSynonyms.Keys);
        words.UnionWith(
        [
            "3d",
            "account",
            "app",
            "area",
            "card",
            "child",
            "current",
            "details",
            "exact",
            "guide",
            "log",
            "map",
            "offline",
            "out",
            "page",
            "result",
            "scene",
            "setup",
            "sign",
            "state",
            "test",
            "user"
        ]);
        return words;
    }

    private static IReadOnlyList<string> Tokenize(string? value, int maximumTerms)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var terms = new List<string>();
        var builder = new StringBuilder();
        for (var index = 0; index < normalized.Length && terms.Count < maximumTerms; index++)
        {
            var character = normalized[index];
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (!char.IsLetterOrDigit(character))
            {
                FlushToken(builder, terms);
                continue;
            }

            var previous = index > 0 ? normalized[index - 1] : '\0';
            var next = index + 1 < normalized.Length ? normalized[index + 1] : '\0';
            var startsWord = builder.Length > 0
                             && ((char.IsDigit(character) && char.IsLetter(previous))
                                 || (char.IsUpper(character)
                                     && (char.IsLower(previous)
                                         || (char.IsUpper(previous) && char.IsLower(next)))));
            if (startsWord)
            {
                FlushToken(builder, terms);
            }

            if (builder.Length < MaximumTermLength)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        if (terms.Count < maximumTerms)
        {
            FlushToken(builder, terms);
        }

        return terms;
    }

    private static void FlushToken(StringBuilder builder, ICollection<string> terms)
    {
        if (builder.Length == 0)
        {
            return;
        }

        terms.Add(builder.ToString());
        builder.Clear();
    }
}

internal sealed record RepositoryTaskSearchMatch(
    RepositoryTaskDefinition Task,
    double Score,
    double Coverage,
    IReadOnlyList<string> MatchedQueryTerms,
    IReadOnlyList<string> UnmatchedQueryTerms,
    IReadOnlyList<RepositoryTaskSearchEvidence> Evidence,
    IReadOnlyList<string> BehavioralSynonyms)
{
    public string? ExclusionReason { get; init; }
}

internal sealed record RepositoryTaskSearchEvidence(
    string QueryTerm,
    string IndexedTerm,
    string IndexedSource,
    RepositoryTaskSearchMatchKind Kind,
    double MatchStrength,
    double IndexedWeight,
    int? EditDistance);

internal sealed record RepositoryTaskIndexedTerm(
    string Term,
    double Weight,
    string Source);

internal enum RepositoryTaskSearchMatchKind
{
    Exact,
    Prefix,
    Synonym,
    Fuzzy
}
