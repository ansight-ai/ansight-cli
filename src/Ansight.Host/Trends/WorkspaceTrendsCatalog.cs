using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ansight.Host.Trends;

public static class WorkspaceTrendsCatalog
{
    private const int MaximumDefinitionCount = 512;
    private const long MaximumDefinitionBytes = 1_048_576;
    private static readonly TimeSpan MaximumSpanDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumObservationTail = TimeSpan.FromSeconds(60);

    public static WorkspaceTrendsCatalogResult Load(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath.Trim());
        var warnings = new List<string>();
        var definitions = LoadDefinitions(
            fullWorkspacePath,
            "trends",
            ParseTrends,
            warnings,
            cancellationToken);
        var historyDefinitions = CreateHistoryDefinitions(definitions);

        AddDuplicateWarnings(
            fullWorkspacePath,
            definitions.Select(static definition => new WorkspaceDefinitionIdentity(
                definition.TrendsId,
                definition.FilePath)),
            "trends",
            warnings);
        AddDuplicateWarnings(
            fullWorkspacePath,
            historyDefinitions.Select(static definition => new WorkspaceDefinitionIdentity(
                definition.DecisionId,
                definition.FilePath)),
            "trends history",
            warnings);

        return new WorkspaceTrendsCatalogResult(
            fullWorkspacePath,
            definitions,
            historyDefinitions,
            warnings);
    }

    public static WorkspaceTrendsDefinition ParseTrends(
        string definitionsDirectoryPath,
        string filePath,
        string source)
    {
        using var document = ParseDocument(source, "trends");
        var root = document.RootElement;
        ValidateSchemaVersion(root, "trends");
        var trendsId = ReadDefinitionId(root, definitionsDirectoryPath, filePath);
        var appId = ReadRequiredString(root, "appId");
        var spanElement = ReadRequiredObject(root, "span");
        var maximumDuration = TimeSpan.FromMilliseconds(
            ReadOptionalInt(spanElement, "maximumDurationMs") ?? 120_000);
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > MaximumSpanDuration)
        {
            throw new InvalidDataException("span.maximumDurationMs must be between 1 and 1,800,000.");
        }
        var span = new WorkspaceTrendsSpanDefinition(
            ReadAnchor(spanElement, "start"),
            ReadAnchor(spanElement, "end"),
            ParseSpanSelection(ReadOptionalString(spanElement, "selection") ?? "lastCompleted"),
            maximumDuration);
        var required = ReadOptionalBool(root, "required") ?? true;
        var missingDataOutcome = (ReadOptionalString(root, "missingDataOutcome") ?? "inconclusive").ToLowerInvariant();
        if (missingDataOutcome is not ("inconclusive" or "fail" or "warn"))
        {
            throw new InvalidDataException("missingDataOutcome must be inconclusive, fail, or warn.");
        }

        if (!root.TryGetProperty("metrics", out var metricsElement)
            || metricsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("metrics must be a non-empty array.");
        }

        var metrics = metricsElement.EnumerateArray()
            .Select(ReadMetric)
            .ToArray();
        if (metrics.Length == 0)
        {
            throw new InvalidDataException("metrics must be a non-empty array.");
        }

        if (metrics.Select(static metric => metric.MetricId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != metrics.Length)
        {
            throw new InvalidDataException("metric IDs must be unique within a trends definition.");
        }

        return new WorkspaceTrendsDefinition(
            trendsId,
            appId,
            span,
            required,
            missingDataOutcome,
            metrics,
            ComputeHash(source),
            Path.GetFullPath(filePath))
        {
            Enabled = ReadEnabled(root)
        };
    }

    private static bool ReadEnabled(JsonElement root)
    {
        if (!root.TryGetProperty("enabled", out var enabledElement))
        {
            return true;
        }

        if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("enabled must be a boolean.");
        }

        return enabledElement.GetBoolean();
    }

    private static IReadOnlyList<WorkspaceTrendsHistoryDefinition> CreateHistoryDefinitions(
        IReadOnlyList<WorkspaceTrendsDefinition> definitions)
        => definitions
            .Where(static definition => definition.Enabled)
            .SelectMany(definition => definition.Metrics
                .Where(static metric => metric.Regression is not null)
                .Select(metric => CreateHistoryDefinition(definition, metric)))
            .ToArray();

    private static WorkspaceTrendsHistoryDefinition CreateHistoryDefinition(
        WorkspaceTrendsDefinition trends,
        WorkspaceTrendsMetricDefinition metric)
    {
        var policy = metric.Regression!;
        var metricKey = $"{trends.TrendsId}.{metric.MetricId}";
        var regression = policy.Direction == WorkspaceTrendsRegressionDirection.Higher
            ? new WorkspaceTrendsHistoryRegressionDefinition(
                policy.Percent,
                null,
                policy.Absolute,
                null,
                policy.ConfirmRuns)
            : new WorkspaceTrendsHistoryRegressionDefinition(
                null,
                policy.Percent,
                null,
                policy.Absolute,
                policy.ConfirmRuns);
        var display = metric.Display;
        return new WorkspaceTrendsHistoryDefinition(
            metricKey,
            metricKey,
            new WorkspaceTrendsHistoryBaselineDefinition(
                "referenceMedian",
                policy.BaselineRuns,
                policy.MinimumBaselineRuns),
            regression,
            policy.SeriesBy,
            ComputeHistoryHash(trends, metric),
            trends.FilePath)
        {
            Platform = metric.Platform,
            Blocking = policy.Blocking,
            Chart = display is null
                ? null
                : new WorkspaceTrendsChartDefinition(
                    display.Title,
                    new WorkspaceTrendsChartAxisDefinition(
                        display.Unit,
                        display.Scale,
                        display.FractionDigits,
                        display.Minimum,
                        display.Maximum,
                        display.IncludeZero))
        };
    }

    private static string ComputeHistoryHash(
        WorkspaceTrendsDefinition trends,
        WorkspaceTrendsMetricDefinition metric)
        => ComputeHash(JsonSerializer.Serialize(new
        {
            trends.TrendsId,
            trends.Span,
            metric.MetricId,
            metric.Platform,
            metric.Channel,
            metric.Statistic,
            metric.StatisticThreshold,
            metric.MinimumSamples,
            metric.MaximumSampleGap,
            metric.BaselineBeforeStart,
            metric.TailAfterEnd,
            metric.Budget,
            metric.Regression
        }));

    private static IReadOnlyList<TDefinition> LoadDefinitions<TDefinition>(
        string workspacePath,
        string directoryName,
        Func<string, string, string, TDefinition> parser,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.Combine(workspacePath, "ansight", directoryName);
        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        var definitions = new List<TDefinition>();
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.AllDirectories)
                .OrderBy(static path => path, GetPathComparer())
                .Take(MaximumDefinitionCount + 1)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Definitions could not be read from '{directoryPath}': {exception.Message}");
            return definitions;
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definitions.Count >= MaximumDefinitionCount)
            {
                warnings.Add($"Only the first {MaximumDefinitionCount:N0} {directoryName} definitions were loaded.");
                break;
            }

            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaximumDefinitionBytes)
                {
                    throw new InvalidDataException("The definition is missing or larger than 1 MB.");
                }

                definitions.Add(parser(directoryPath, path, File.ReadAllText(path)));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or JsonException)
            {
                warnings.Add($"{Path.GetRelativePath(workspacePath, path)}: {exception.Message}");
            }
        }

        return definitions;
    }

    private static WorkspaceTrendsMetricDefinition ReadMetric(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("metrics must contain JSON objects.");
        }

        var metricId = ReadRequiredString(element, "id");
        var platform = ReadOptionalString(element, "platform")?.ToLowerInvariant();
        if (platform is not null
            && platform is not ("android" or "ios" or "macos" or "windows" or "other"))
        {
            throw new InvalidDataException(
                "metric.platform must be android, ios, macos, windows, or other.");
        }
        var channelElement = ReadRequiredObject(element, "channel");
        var channelMatch = ReadOptionalString(channelElement, "match") ?? "exactlyOne";
        if (!channelMatch.Equals("exactlyOne", StringComparison.OrdinalIgnoreCase)
            && !channelMatch.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("metric.channel.match must be exactlyOne or any.");
        }
        var selector = new WorkspaceTrendsMetricSelector(
            ReadOptionalString(channelElement, "type"),
            ReadOptionalString(channelElement, "name"),
            ReadOptionalString(channelElement, "source"),
            ReadOptionalString(channelElement, "kind"),
            !string.Equals(channelMatch, "any", StringComparison.OrdinalIgnoreCase));
        if (selector is { Type: null, Name: null, Source: null, Kind: null })
        {
            throw new InvalidDataException("metric.channel must select type, name, source, or kind.");
        }

        if (!element.TryGetProperty("statistic", out var statisticElement))
        {
            throw new InvalidDataException("metric.statistic is required.");
        }
        string statistic;
        double? statisticThreshold = null;
        var statisticWasObject = false;
        if (statisticElement.ValueKind == JsonValueKind.String)
        {
            statistic = statisticElement.GetString()?.Trim() ?? string.Empty;
        }
        else if (statisticElement.ValueKind == JsonValueKind.Object)
        {
            statisticWasObject = true;
            statistic = ReadRequiredString(statisticElement, "type");
            statisticThreshold = ReadOptionalDouble(statisticElement, "threshold");
        }
        else
        {
            throw new InvalidDataException("metric.statistic must be a string or JSON object.");
        }

        var supportedStatistics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "min", "max", "average", "p05", "p10", "p50", "p95", "timeBelowRatio",
            "memoryPeakIncreaseMiB", "memoryRetainedIncreaseMiB", "tailSlopeMiBPerSecond", "tailSpreadMiB"
        };
        if (!supportedStatistics.Contains(statistic))
        {
            throw new InvalidDataException($"Unsupported metric statistic '{statistic}'.");
        }
        if (string.Equals(statistic, "timeBelowRatio", StringComparison.OrdinalIgnoreCase)
            && !statisticThreshold.HasValue)
        {
            throw new InvalidDataException("timeBelowRatio requires statistic.threshold.");
        }
        if (statisticWasObject
            && !string.Equals(statistic, "timeBelowRatio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only timeBelowRatio uses an object statistic with a threshold.");
        }

        var budgetElement = ReadRequiredObject(element, "budget");
        var budget = new WorkspaceTrendsBudget(
            ReadOptionalDouble(budgetElement, "gte"),
            ReadOptionalDouble(budgetElement, "lte"),
            ReadOptionalDouble(budgetElement, "absoluteLte"));
        if (budget is { GreaterThanOrEqual: null, LessThanOrEqual: null, AbsoluteLessThanOrEqual: null })
        {
            throw new InvalidDataException("metric.budget must contain gte, lte, or absoluteLte.");
        }

        var minimumSamples = ReadOptionalInt(element, "minimumSamples") ?? 2;
        var maximumGap = TimeSpan.FromMilliseconds(ReadOptionalInt(element, "maximumSampleGapMs") ?? 5_000);
        var baseline = TimeSpan.FromMilliseconds(ReadOptionalInt(element, "baselineBeforeStartMs") ?? 2_000);
        var tail = TimeSpan.FromMilliseconds(ReadOptionalInt(element, "tailAfterEndMs") ?? 0);
        if (minimumSamples is < 1 or > 100_000)
        {
            throw new InvalidDataException("minimumSamples must be between 1 and 100,000.");
        }
        if (maximumGap <= TimeSpan.Zero || maximumGap > TimeSpan.FromMinutes(5))
        {
            throw new InvalidDataException("maximumSampleGapMs must be between 1 and 300,000.");
        }
        if (baseline < TimeSpan.Zero || baseline > MaximumObservationTail
            || tail < TimeSpan.Zero || tail > MaximumObservationTail)
        {
            throw new InvalidDataException("baselineBeforeStartMs and tailAfterEndMs must be between 0 and 60,000.");
        }

        return new WorkspaceTrendsMetricDefinition(
            metricId,
            selector,
            statistic,
            budget,
            statisticThreshold,
            minimumSamples,
            maximumGap,
            baseline,
            tail)
        {
            Platform = platform,
            Regression = ReadMetricRegression(element, budget),
            Display = ReadMetricDisplay(element)
        };
    }

    private static WorkspaceTrendsRegressionPolicy? ReadMetricRegression(
        JsonElement metric,
        WorkspaceTrendsBudget budget)
    {
        if (!metric.TryGetProperty("regression", out var element))
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("metric.regression must be a JSON object.");
        }

        var regressionPercent = ReadOptionalDouble(element, "percent");
        var regressionAbsolute = ReadOptionalDouble(element, "absolute");
        if (!regressionPercent.HasValue && !regressionAbsolute.HasValue)
        {
            throw new InvalidDataException(
                "metric.regression must configure percent or absolute.");
        }
        if (regressionPercent is < 0 || regressionAbsolute is < 0)
        {
            throw new InvalidDataException("metric.regression regression thresholds must be zero or greater.");
        }

        var confirmRuns = ReadOptionalInt(element, "confirmRuns") ?? 2;
        var baselineRuns = ReadOptionalInt(element, "baselineRuns") ?? 10;
        var minimumBaselineRuns = ReadOptionalInt(element, "minimumBaselineRuns") ?? 5;
        if (confirmRuns is < 1 or > 20)
        {
            throw new InvalidDataException("metric.regression.confirmRuns must be between 1 and 20.");
        }
        if (baselineRuns is < 1 or > 200
            || minimumBaselineRuns is < 1 or > 200
            || minimumBaselineRuns > baselineRuns)
        {
            throw new InvalidDataException(
                "metric.regression baseline runs must be between 1 and 200, with minimumBaselineRuns no greater than baselineRuns.");
        }

        var direction = ResolveRegressionDirection(ReadOptionalString(element, "worseWhen"), budget);
        var seriesBy = element.TryGetProperty("seriesBy", out _)
            ? ReadStringArray(element, "seriesBy")
            : ["platform", "deviceModel", "operatingSystemMajor"];
        var supportedSeriesDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "buildNumber", "platform", "deviceModel",
            "operatingSystemMajor", "buildConfiguration"
        };
        var unsupportedDimension = seriesBy.FirstOrDefault(
            dimension => !supportedSeriesDimensions.Contains(dimension));
        if (unsupportedDimension is not null)
        {
            throw new InvalidDataException(
                $"Unsupported metric.regression series dimension '{unsupportedDimension}'.");
        }
        seriesBy = seriesBy
            .OrderBy(static dimension => dimension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WorkspaceTrendsRegressionPolicy(
            direction,
            regressionPercent,
            regressionAbsolute,
            confirmRuns,
            baselineRuns,
            minimumBaselineRuns,
            seriesBy,
            ReadOptionalBool(element, "blocking") ?? false);
    }

    private static WorkspaceTrendsRegressionDirection ResolveRegressionDirection(
        string? configuredDirection,
        WorkspaceTrendsBudget budget)
    {
        if (configuredDirection is not null)
        {
            return configuredDirection.ToLowerInvariant() switch
            {
                "higher" => WorkspaceTrendsRegressionDirection.Higher,
                "lower" => WorkspaceTrendsRegressionDirection.Lower,
                _ => throw new InvalidDataException(
                    "metric.regression.worseWhen must be higher or lower.")
            };
        }

        if (budget.GreaterThanOrEqual.HasValue
            && !budget.LessThanOrEqual.HasValue
            && !budget.AbsoluteLessThanOrEqual.HasValue)
        {
            return WorkspaceTrendsRegressionDirection.Lower;
        }
        if (budget.LessThanOrEqual.HasValue
            && !budget.GreaterThanOrEqual.HasValue
            && !budget.AbsoluteLessThanOrEqual.HasValue)
        {
            return WorkspaceTrendsRegressionDirection.Higher;
        }

        throw new InvalidDataException(
            "metric.regression.worseWhen is required when the budget does not imply one regression direction.");
    }

    private static WorkspaceTrendsDisplayDefinition? ReadMetricDisplay(JsonElement metric)
    {
        if (!metric.TryGetProperty("display", out var element))
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("metric.display must be a JSON object.");
        }

        var title = ReadOptionalString(element, "title");
        var unit = ReadOptionalString(element, "unit");
        var scale = ReadOptionalDouble(element, "scale");
        var fractionDigits = ReadOptionalInt(element, "fractionDigits");
        var minimum = ReadOptionalDouble(element, "minimum");
        var maximum = ReadOptionalDouble(element, "maximum");
        var includeZero = ReadOptionalBool(element, "includeZero");
        if (title is null
            && unit is null
            && scale is null
            && fractionDigits is null
            && minimum is null
            && maximum is null
            && includeZero is null)
        {
            throw new InvalidDataException("metric.display must configure at least one property.");
        }
        if (scale is <= 0)
        {
            throw new InvalidDataException("metric.display.scale must be greater than zero.");
        }
        if (fractionDigits is < 0 or > 6)
        {
            throw new InvalidDataException("metric.display.fractionDigits must be between 0 and 6.");
        }
        if (minimum.HasValue && maximum.HasValue && minimum.Value >= maximum.Value)
        {
            throw new InvalidDataException("metric.display.minimum must be less than metric.display.maximum.");
        }

        return new WorkspaceTrendsDisplayDefinition(
            title,
            unit,
            scale,
            fractionDigits,
            minimum,
            maximum,
            includeZero);
    }

    private static WorkspaceEventAnchor ReadAnchor(JsonElement root, string propertyName)
    {
        var anchorElement = ReadRequiredObject(root, propertyName);
        var hasEvent = anchorElement.TryGetProperty("event", out _);
        var hasLog = anchorElement.TryGetProperty("log", out _);
        if (hasEvent == hasLog)
            throw new InvalidDataException($"span.{propertyName} must select exactly one of event or log.");
        if (hasLog)
        {
            var log = ReadRequiredObject(anchorElement, "log");
            string[] allowed = ["streamId", "message", "match", "priority", "source", "tag", "processId"];
            if (log.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
                throw new InvalidDataException($"span.{propertyName}.log contains an unsupported selector field.");
            var streamId = ReadRequiredString(log, "streamId");
            var message = ReadRequiredString(log, "message");
            var match = ReadOptionalString(log, "match") ?? "exact";
            if (match is not ("exact" or "contains"))
                throw new InvalidDataException($"span.{propertyName}.log.match must be exact or contains.");
            var priority = ReadOptionalString(log, "priority");
            if (priority is not null && !Enum.GetNames<LogPriority>().Contains(priority))
                throw new InvalidDataException($"span.{propertyName}.log.priority must be a log priority name.");
            var processId = ReadOptionalInt(log, "processId");
            if (processId < 1)
                throw new InvalidDataException($"span.{propertyName}.log.processId must be positive.");
            return new WorkspaceEventAnchor(message)
            {
                Log = new WorkspaceTrendsLogSelector(streamId, message, match, priority,
                    ReadOptionalString(log, "source"), ReadOptionalString(log, "tag"), processId)
            };
        }
        var eventElement = ReadRequiredObject(anchorElement, "event");
        var channelValue = ReadOptionalInt(eventElement, "channelId");
        if (channelValue is < byte.MinValue or > byte.MaxValue)
        {
            throw new InvalidDataException($"{propertyName}.event.channelId must be between 0 and 255.");
        }
        return new WorkspaceEventAnchor(
            ReadRequiredString(eventElement, "label"),
            ReadOptionalString(eventElement, "eventType"),
            channelValue.HasValue ? (byte)channelValue.Value : null);
    }

    private static WorkspaceTrendsSpanSelection ParseSpanSelection(string value)
        => value.Trim().ToLowerInvariant() switch
    {
        "firstcompleted" => WorkspaceTrendsSpanSelection.FirstCompleted,
        "lastcompleted" => WorkspaceTrendsSpanSelection.LastCompleted,
        "exactlyone" => WorkspaceTrendsSpanSelection.ExactlyOne,
        "all" => WorkspaceTrendsSpanSelection.All,
        _ => throw new InvalidDataException("selection must be firstCompleted, lastCompleted, exactlyOne, or all.")
    };

    private static JsonDocument ParseDocument(string source, string definitionName)
    {
        var document = JsonDocument.Parse(source, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException($"The {definitionName} definition must be a JSON object.");
        }
        return document;
    }

    private static void ValidateSchemaVersion(JsonElement root, string definitionName)
    {
        var schemaVersion = ReadOptionalInt(root, "schemaVersion") ?? 1;
        if (schemaVersion != 1)
        {
            throw new InvalidDataException($"{definitionName} schemaVersion must be 1.");
        }
    }

    private static string ReadDefinitionId(JsonElement root, string definitionsDirectoryPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(definitionsDirectoryPath, filePath).Replace('\\', '/');
        var inferred = Path.ChangeExtension(relativePath, null)?.Replace('/', '.') ?? relativePath;
        return ReadOptionalString(root, "id") ?? inferred;
    }

    private static JsonElement ReadRequiredObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{propertyName} must be a JSON object.");
        }
        return value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
        => ReadOptionalString(element, propertyName)
           ?? throw new InvalidDataException($"{propertyName} is required.");

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? ReadOptionalInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result)
            ? result
            : null;

    private static double? ReadOptionalDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var result)
            ? result
            : null;

    private static bool? ReadOptionalBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{propertyName} must be an array of strings.");
        }
        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException($"{propertyName} must contain only non-empty strings.");
            }
            values.Add(item.GetString()!.Trim());
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ComputeHash(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void AddDuplicateWarnings(
        string workspacePath,
        IEnumerable<WorkspaceDefinitionIdentity> definitions,
        string definitionKind,
        ICollection<string> warnings)
    {
        foreach (var group in definitions.GroupBy(
                     static definition => definition.DefinitionId,
                     StringComparer.OrdinalIgnoreCase).Where(static group => group.Count() > 1))
        {
            warnings.Add(
                $"Duplicate {definitionKind} ID '{group.Key}' was found in "
                + string.Join(
                    ", ",
                    group.Select(definition => Path.GetRelativePath(workspacePath, definition.FilePath)))
                + ".");
        }
    }

    private sealed record WorkspaceDefinitionIdentity(string DefinitionId, string FilePath);
}
