using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Trends;

public sealed class WorkspaceTrendsService
{
    private const int ReportSchemaVersion = 1;
    private static readonly Ansight.Infrastructure.Logging.ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private static readonly JsonSerializerOptions reportJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly RuntimeCoordinator runtime;
    private readonly WorkspaceTrendsEvaluator evaluator = new();
    private readonly WorkspaceTrendsHistoryStore historyStore;
    private readonly IApplicationPaths applicationPaths;
    private readonly Lock automaticEvaluationGate = new();
    private readonly Dictionary<string, Task<WorkspaceTrendsReport?>> automaticEvaluationTasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> deletedSessionIds = new(StringComparer.Ordinal);
    private readonly Lock historyEvaluationGate = new();

    internal WorkspaceTrendsService(RuntimeCoordinator runtime, IApplicationPaths applicationPaths)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        historyStore = new WorkspaceTrendsHistoryStore(applicationPaths);
    }

    public WorkspaceTrendsCatalogResult List(
        string workspacePath,
        CancellationToken cancellationToken = default)
        => WorkspaceTrendsCatalog.Load(workspacePath, cancellationToken);

    public WorkspaceTrendsHistoryResult ListHistory(
        string? appId = null,
        string? metricKey = null,
        string? decisionId = null,
        int limit = 100,
        string? spanGroup = null,
        bool limitPerSeries = false)
    {
        var history = CreateHistoryResult(
            historyStore,
            appId,
            metricKey,
            decisionId,
            limit,
            spanGroup,
            limitPerSeries);
        if (string.IsNullOrWhiteSpace(appId))
        {
            return history;
        }

        var app = runtime.Apps.Get(appId);
        if (string.IsNullOrWhiteSpace(app?.CodebasePath) || !Directory.Exists(app.CodebasePath))
        {
            return history;
        }

        var catalog = WorkspaceTrendsCatalog.Load(app.CodebasePath);
        var charts = catalog.HistoryDefinitions
            .Where(static definition => definition.Chart is not null)
            .Select(static definition => new WorkspaceTrendsChartMetadata(
                definition.DecisionId,
                definition.MetricKey,
                definition.Chart!.Title,
                definition.Chart.YAxis)
            {
                DefinitionHash = definition.DefinitionHash
            })
            .ToArray();
        return history with
        {
            Charts = charts,
            Series = CreateSeries(history.History, charts)
        };
    }

    public static WorkspaceTrendsHistoryResult LoadHistory(
        IApplicationPaths applicationPaths,
        string? appId = null,
        string? metricKey = null,
        string? decisionId = null,
        int limit = 100,
        string? spanGroup = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return CreateHistoryResult(
            new WorkspaceTrendsHistoryStore(applicationPaths),
            appId,
            metricKey,
            decisionId,
            limit,
            spanGroup,
            limitPerSeries: false);
    }

    public WorkspaceTrendsHistoryRebuildResult RebuildHistory(
        WorkspaceTrendsHistoryRebuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appId = NormalizeOptional(request.AppId);
        var appVersion = NormalizeOptional(request.AppVersion);
        var metricKey = NormalizeOptional(request.MetricKey);
        var workspaceOverride = NormalizeOptional(request.WorkspacePath);
        if (appVersion is not null && appId is null)
        {
            throw new ArgumentException("An app ID is required when rebuilding one app version.", nameof(request));
        }
        if (workspaceOverride is not null && appId is null)
        {
            throw new ArgumentException("A workspace override can only be used when rebuilding one app.", nameof(request));
        }

        lock (historyEvaluationGate)
        {
            var appIds = appId is null
                ? historyStore.LoadMetricAppIds()
                : [appId];
            var scopes = new List<WorkspaceTrendsHistoryRebuildScope>();
            var rows = new List<WorkspaceTrendsHistoryRebuildRow>();
            var apps = new List<WorkspaceTrendsHistoryRebuildAppResult>();
            var skippedApps = new List<WorkspaceTrendsHistoryRebuildSkippedApp>();
            foreach (var candidateAppId in appIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workspacePath = ResolveHistoryRebuildWorkspace(candidateAppId, workspaceOverride);
                if (workspacePath is null)
                {
                    skippedApps.Add(new WorkspaceTrendsHistoryRebuildSkippedApp(
                        candidateAppId,
                        "No existing registered codebase is available for the app's current rules."));
                    continue;
                }

                WorkspaceTrendsCatalogResult catalog;
                try
                {
                    catalog = WorkspaceTrendsCatalog.Load(workspacePath, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException)
                {
                    skippedApps.Add(new WorkspaceTrendsHistoryRebuildSkippedApp(
                        candidateAppId,
                        $"Current trends rules could not be loaded: {exception.Message}"));
                    continue;
                }

                var appMetricKeys = catalog.Definitions
                    .Where(static definition => definition.Enabled)
                    .Where(definition => string.Equals(
                        definition.AppId,
                        candidateAppId,
                        StringComparison.Ordinal))
                    .SelectMany(static definition => definition.Metrics.Select(metric =>
                        $"{definition.TrendsId}.{metric.MetricId}"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var definitions = catalog.HistoryDefinitions
                    .Where(definition => appMetricKeys.Contains(definition.MetricKey))
                    .Where(definition => metricKey is null || string.Equals(
                        definition.MetricKey,
                        metricKey,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (definitions.Length == 0)
                {
                    if (metricKey is not null)
                    {
                        skippedApps.Add(new WorkspaceTrendsHistoryRebuildSkippedApp(
                            candidateAppId,
                            $"The current workspace has no regression policy for metric '{metricKey}' in this app."));
                        continue;
                    }
                }

                var metrics = historyStore.LoadMetricsForHistoryRebuild(candidateAppId);
                var rebuiltRows = RebuildAppHistory(
                    metrics,
                    definitions,
                    appVersion,
                    cancellationToken);
                scopes.Add(new WorkspaceTrendsHistoryRebuildScope(
                    candidateAppId,
                    appVersion,
                    HasAppVersionFilter: appVersion is not null,
                    metricKey));
                rows.AddRange(rebuiltRows);
                apps.Add(new WorkspaceTrendsHistoryRebuildAppResult(
                    candidateAppId,
                    workspacePath,
                    metrics.Count,
                    definitions.Length,
                    rebuiltRows.Count)
                {
                    StatusCounts = rebuiltRows
                        .GroupBy(static row => row.Result.Status)
                        .ToDictionary(static group => group.Key, static group => group.Count())
                });
            }

            var removedCount = request.DryRun
                ? historyStore.CountHistory(scopes)
                : historyStore.ReplaceHistory(scopes, rows);
            return new WorkspaceTrendsHistoryRebuildResult(
                historyStore.DatabasePath,
                request.DryRun,
                appId,
                appVersion,
                metricKey,
                removedCount,
                rows.Count,
                apps,
                skippedApps);
        }
    }

    private string? ResolveHistoryRebuildWorkspace(string appId, string? workspaceOverride)
    {
        var candidate = workspaceOverride ?? runtime.Apps.Get(appId)?.CodebasePath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }
        var fullPath = Path.GetFullPath(candidate);
        return Directory.Exists(fullPath) ? fullPath : null;
    }

    private static IReadOnlyList<WorkspaceTrendsHistoryRebuildRow> RebuildAppHistory(
        IReadOnlyList<WorkspaceTrendsMetricHistoryEntry> metrics,
        IReadOnlyList<WorkspaceTrendsHistoryDefinition> definitions,
        string? appVersion,
        CancellationToken cancellationToken)
    {
        var priorMetrics = new List<WorkspaceTrendsMetricHistoryEntry>();
        var rebuiltRows = new List<WorkspaceTrendsHistoryRebuildRow>();
        var evaluations = metrics
            .GroupBy(static metric => metric.EvaluationId, StringComparer.Ordinal)
            .OrderBy(static group => group.Min(static metric => metric.EvaluatedAtUtc))
            .ThenBy(static group => group.Key, StringComparer.Ordinal);
        foreach (var evaluation in evaluations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluationMetrics = evaluation.ToArray();
            var first = evaluationMetrics[0];
            var context = CreateHistoricalRunContext(first);
            var isSelectedVersion = appVersion is null || string.Equals(
                context.AppVersion,
                appVersion,
                StringComparison.Ordinal);
            if (isSelectedVersion)
            {
                var currentMetrics = evaluationMetrics
                    .GroupBy(static metric => metric.MetricKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group
                            .GroupBy(static metric => metric.SpanGroup ?? string.Empty, StringComparer.Ordinal)
                            .Select(static spanGroup => spanGroup
                                .OrderByDescending(static metric => metric.InstanceIndex)
                                .First())
                            .ToArray(),
                        StringComparer.OrdinalIgnoreCase);
                foreach (var definition in definitions)
                {
                    if (!HistoryDefinitionAppliesToPlatform(definition, context.Platform))
                    {
                        continue;
                    }
                    if (!currentMetrics.TryGetValue(definition.MetricKey, out var current))
                    {
                        continue;
                    }
                    foreach (var metric in current)
                    {
                        var comparison = string.IsNullOrWhiteSpace(context.AppVersion)
                            ? WorkspaceTrendsHistoryComparison.UnversionedReference
                            : WorkspaceTrendsHistoryComparison.PreviousVersion;
                        var seriesKey = CreateSeriesKey(context, definition.Cohort);
                        var missingSeriesDimension = FindMissingSeriesDimension(context, definition.Cohort);
                        WorkspaceTrendsHistoryDecision result;
                        var isBreach = false;
                        if (missingSeriesDimension is not null)
                        {
                            result = new WorkspaceTrendsHistoryDecision(
                                definition.DecisionId,
                                definition.MetricKey,
                                comparison,
                                WorkspaceTrendsHistoryStatus.Error,
                                $"Cannot evaluate this signal because series dimension '{missingSeriesDimension}' is missing from the session profile.",
                                metric.Value,
                                null,
                                null,
                                null,
                                0,
                                definition.Baseline.MinimumRuns,
                                0,
                                definition.DefinitionHash);
                        }
                        else
                        {
                            var baselineData = CreateHistoricalBaseline(
                                context,
                                metric,
                                definition,
                                comparison,
                                priorMetrics,
                                rebuiltRows);
                            result = EvaluateHistory(
                                definition,
                                comparison,
                                metric.Value,
                                metric.SpanGroup,
                                context.AppVersion,
                                baselineData);
                            isBreach = IsRegression(
                                definition.Regression,
                                result.AbsoluteDelta,
                                result.RelativeDeltaPercent);
                        }

                        result = result with
                        {
                            SpanGroup = metric.SpanGroup,
                            MetricDefinitionHash = metric.MetricDefinitionHash,
                            Unit = metric.Unit,
                            Blocking = definition.Blocking,
                            SeriesKey = seriesKey
                        };
                        rebuiltRows.Add(new WorkspaceTrendsHistoryRebuildRow(context, result, isBreach));
                    }
                }
            }
            priorMetrics.AddRange(evaluationMetrics);
        }
        return rebuiltRows;
    }

    private static bool HistoryDefinitionAppliesToPlatform(
        WorkspaceTrendsHistoryDefinition definition,
        string? platform)
        => string.IsNullOrWhiteSpace(definition.Platform)
           || string.Equals(
               definition.Platform,
               NormalizeHistoricalPlatform(platform),
               StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHistoricalPlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return "other";
        }
        var normalized = platform.Trim();
        if (normalized.Contains("android", StringComparison.OrdinalIgnoreCase))
        {
            return "android";
        }
        if (normalized.Contains("ios", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("iphone", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ipad", StringComparison.OrdinalIgnoreCase))
        {
            return "ios";
        }
        if (normalized.Contains("windows", StringComparison.OrdinalIgnoreCase))
        {
            return "windows";
        }
        if (normalized.Contains("mac", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("catalyst", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("os x", StringComparison.OrdinalIgnoreCase))
        {
            return "macos";
        }
        return "other";
    }

    private static WorkspaceTrendsHistoryBaselineData CreateHistoricalBaseline(
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsMetricHistoryEntry metric,
        WorkspaceTrendsHistoryDefinition history,
        WorkspaceTrendsHistoryComparison comparison,
        IReadOnlyList<WorkspaceTrendsMetricHistoryEntry> priorMetrics,
        IReadOnlyList<WorkspaceTrendsHistoryRebuildRow> priorResults)
    {
        var baselineAppVersion = comparison switch
        {
            WorkspaceTrendsHistoryComparison.UnversionedReference => context.AppVersion,
            WorkspaceTrendsHistoryComparison.PreviousVersion when context.AppVersion is not null
                => FindHistoricalPinnedBaselineVersion(context, metric, history, priorResults)
                   ?? FindHistoricalBaselineVersion(context, metric, history, priorMetrics),
            _ => null
        };
        var comparableMetrics = priorMetrics
            .Where(candidate => string.Equals(candidate.AppId, context.AppId, StringComparison.Ordinal)
                                && string.Equals(candidate.MetricKey, metric.MetricKey, StringComparison.Ordinal)
                                && string.Equals(candidate.MetricDefinitionHash, metric.MetricDefinitionHash, StringComparison.Ordinal)
                                && SpanGroupsEqual(candidate.SpanGroup, metric.SpanGroup)
                                && CohortMatches(candidate, context, history.Cohort)
                                && VersionMatches(candidate.AppVersion, baselineAppVersion, comparison))
            .GroupBy(static candidate => candidate.EvaluationId, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static candidate => candidate.InstanceIndex).First());
        comparableMetrics = comparison == WorkspaceTrendsHistoryComparison.UnversionedReference
            ? comparableMetrics.OrderBy(static candidate => candidate.EvaluatedAtUtc)
                .ThenBy(static candidate => candidate.EvaluationId, StringComparer.Ordinal)
            : comparableMetrics.OrderByDescending(static candidate => candidate.EvaluatedAtUtc)
                .ThenByDescending(static candidate => candidate.EvaluationId, StringComparer.Ordinal);
        var values = comparableMetrics
            .Take(history.Baseline.Runs)
            .Select(static candidate => candidate.Value)
            .ToArray();
        var previous = priorResults
            .LastOrDefault(candidate => HistoricalSeriesMatches(
                candidate,
                context,
                metric,
                history,
                comparison,
                baselineAppVersion));
        return new WorkspaceTrendsHistoryBaselineData(
            values,
            previous?.Result.Status,
            previous?.Result.ConsecutiveBreachCount ?? 0,
            baselineAppVersion);
    }

    private static string? FindHistoricalPinnedBaselineVersion(
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsMetricHistoryEntry metric,
        WorkspaceTrendsHistoryDefinition history,
        IReadOnlyList<WorkspaceTrendsHistoryRebuildRow> priorResults)
        => priorResults
            .FirstOrDefault(candidate => candidate.Result.BaselineAppVersion is not null
                                         && HistoricalSeriesMatches(
                                             candidate,
                                             context,
                                             metric,
                                             history,
                                             WorkspaceTrendsHistoryComparison.PreviousVersion,
                                             candidate.Result.BaselineAppVersion))
            ?.Result.BaselineAppVersion;

    private static string? FindHistoricalBaselineVersion(
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsMetricHistoryEntry metric,
        WorkspaceTrendsHistoryDefinition history,
        IReadOnlyList<WorkspaceTrendsMetricHistoryEntry> priorMetrics)
        => priorMetrics
            .Where(candidate => candidate.AppVersion is not null
                                && !string.Equals(candidate.AppVersion, context.AppVersion, StringComparison.Ordinal)
                                && string.Equals(candidate.AppId, context.AppId, StringComparison.Ordinal)
                                && string.Equals(candidate.MetricKey, metric.MetricKey, StringComparison.Ordinal)
                                && string.Equals(candidate.MetricDefinitionHash, metric.MetricDefinitionHash, StringComparison.Ordinal)
                                && SpanGroupsEqual(candidate.SpanGroup, metric.SpanGroup)
                                && CohortMatches(candidate, context, history.Cohort))
            .OrderByDescending(static candidate => candidate.EvaluatedAtUtc)
            .ThenByDescending(static candidate => candidate.EvaluationId, StringComparer.Ordinal)
            .Select(static candidate => candidate.AppVersion)
            .FirstOrDefault();

    private static bool HistoricalSeriesMatches(
        WorkspaceTrendsHistoryRebuildRow candidate,
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsMetricHistoryEntry metric,
        WorkspaceTrendsHistoryDefinition history,
        WorkspaceTrendsHistoryComparison comparison,
        string? baselineAppVersion)
        => string.Equals(candidate.Context.AppId, context.AppId, StringComparison.Ordinal)
           && string.Equals(candidate.Context.AppVersion, context.AppVersion, StringComparison.Ordinal)
           && string.Equals(candidate.Result.DecisionId, history.DecisionId, StringComparison.Ordinal)
           && string.Equals(candidate.Result.DefinitionHash, history.DefinitionHash, StringComparison.Ordinal)
           && string.Equals(candidate.Result.MetricDefinitionHash, metric.MetricDefinitionHash, StringComparison.Ordinal)
           && candidate.Result.Comparison == comparison
           && SpanGroupsEqual(candidate.Result.SpanGroup, metric.SpanGroup)
           && string.Equals(candidate.Result.BaselineAppVersion, baselineAppVersion, StringComparison.Ordinal)
           && CohortMatches(candidate.Context, context, history.Cohort);

    private static bool VersionMatches(
        string? candidateAppVersion,
        string? baselineAppVersion,
        WorkspaceTrendsHistoryComparison comparison)
        => comparison switch
        {
            WorkspaceTrendsHistoryComparison.UnversionedReference when baselineAppVersion is null
                => candidateAppVersion is null,
            WorkspaceTrendsHistoryComparison.UnversionedReference
                => string.Equals(candidateAppVersion, baselineAppVersion, StringComparison.Ordinal),
            WorkspaceTrendsHistoryComparison.PreviousVersion when baselineAppVersion is not null
                => string.Equals(candidateAppVersion, baselineAppVersion, StringComparison.Ordinal),
            _ => false
        };

    private static bool CohortMatches(
        WorkspaceTrendsMetricHistoryEntry candidate,
        WorkspaceTrendsRunContext context,
        IReadOnlyList<string> cohort)
        => CohortMatches(CreateHistoricalRunContext(candidate), context, cohort);

    private static bool CohortMatches(
        WorkspaceTrendsRunContext candidate,
        WorkspaceTrendsRunContext context,
        IReadOnlyList<string> cohort)
        => cohort.Distinct(StringComparer.OrdinalIgnoreCase).All(dimension => string.Equals(
            ResolveSeriesDimension(candidate, dimension),
            ResolveSeriesDimension(context, dimension),
            StringComparison.Ordinal));

    private static WorkspaceTrendsRunContext CreateHistoricalRunContext(
        WorkspaceTrendsMetricHistoryEntry metric)
        => new(
            metric.EvaluationId,
            metric.RunId,
            metric.SessionId,
            metric.AppId,
            metric.AppVersion,
            metric.BuildNumber,
            metric.Platform,
            metric.DeviceModel,
            metric.OperatingSystemMajor,
            metric.BuildConfiguration,
            metric.EvaluatedAtUtc);

    private static bool SpanGroupsEqual(string? left, string? right)
        => string.Equals(
            string.IsNullOrWhiteSpace(left) ? string.Empty : left.Trim(),
            string.IsNullOrWhiteSpace(right) ? string.Empty : right.Trim(),
            StringComparison.Ordinal);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal int DeleteSessionHistory(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        lock (automaticEvaluationGate)
        {
            deletedSessionIds.Add(normalizedSessionId);
        }

        lock (historyEvaluationGate)
        {
            return historyStore.DeleteSession(normalizedSessionId);
        }
    }

    private static WorkspaceTrendsHistoryResult CreateHistoryResult(
        WorkspaceTrendsHistoryStore store,
        string? appId,
        string? metricKey,
        string? decisionId,
        int limit,
        string? spanGroup,
        bool limitPerSeries)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "History limit must be between 1 and 10,000.");
        }
        var history = store.LoadHistory(appId, decisionId, limit, spanGroup, limitPerSeries);
        return new WorkspaceTrendsHistoryResult(
            store.DatabasePath,
            store.LoadMetricHistory(appId, metricKey, limit, spanGroup),
            history)
        {
            Series = CreateSeries(history, [])
        };
    }

    private static IReadOnlyList<WorkspaceTrendsSeries> CreateSeries(
        IReadOnlyList<WorkspaceTrendsHistoryEntry> entries,
        IReadOnlyList<WorkspaceTrendsChartMetadata> charts)
        => entries
            .GroupBy(static entry => new WorkspaceTrendsSeriesKey(
                entry.AppId,
                entry.DecisionId,
                entry.MetricKey,
                entry.Comparison,
                entry.AppVersion,
                entry.BaselineAppVersion,
                entry.SpanGroup,
                entry.SeriesKey,
                entry.DefinitionHash,
                entry.MetricDefinitionHash))
            .Select(group =>
            {
                var points = group.OrderBy(static entry => entry.EvaluatedAtUtc).ToArray();
                var latest = points[^1];
                var chart = charts.FirstOrDefault(candidate =>
                    string.Equals(candidate.DecisionId, group.Key.DecisionId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.DefinitionHash, group.Key.DefinitionHash, StringComparison.Ordinal));
                return new WorkspaceTrendsSeries(
                    CreateSeriesId(group.Key),
                    group.Key.AppId,
                    group.Key.DecisionId,
                    group.Key.MetricKey,
                    group.Key.Comparison,
                    group.Key.AppVersion,
                    CommonValue(points, static point => point.BuildNumber),
                    group.Key.BaselineAppVersion,
                    group.Key.SpanGroup,
                    CommonValue(points, static point => point.Platform),
                    CommonValue(points, static point => point.DeviceModel),
                    CommonValue(points, static point => point.OperatingSystemMajor),
                    CommonValue(points, static point => point.BuildConfiguration),
                    group.Key.SeriesKey,
                    group.Key.DefinitionHash,
                    group.Key.MetricDefinitionHash,
                    latest.Unit,
                    latest.Status,
                    latest.Blocking,
                    points)
                {
                    Chart = chart
                };
            })
            .OrderByDescending(static series => series.Points[^1].EvaluatedAtUtc)
            .ToArray();

    private static string CreateSeriesId(WorkspaceTrendsSeriesKey key)
    {
        var identity = string.Join(
            "\u001f",
            key.AppId,
            key.DecisionId,
            key.MetricKey,
            key.Comparison,
            key.AppVersion,
            key.BaselineAppVersion,
            key.SpanGroup,
            key.SeriesKey,
            key.DefinitionHash,
            key.MetricDefinitionHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24]
            .ToLowerInvariant();
    }

    private static string? CommonValue(
        IReadOnlyList<WorkspaceTrendsHistoryEntry> points,
        Func<WorkspaceTrendsHistoryEntry, string?> select)
    {
        var values = points
            .Select(select)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    internal void QueueRegisteredSessionEvaluation(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        Task<WorkspaceTrendsReport?> evaluationTask;
        lock (automaticEvaluationGate)
        {
            if (automaticEvaluationTasks.ContainsKey(normalizedSessionId))
            {
                return;
            }
            evaluationTask = Task.Run(() => EvaluateRegisteredSessionAsync(normalizedSessionId));
            automaticEvaluationTasks.Add(normalizedSessionId, evaluationTask);
        }

        _ = ObserveRegisteredSessionEvaluationAsync(normalizedSessionId, evaluationTask);
    }

    public async Task AwaitRegisteredSessionEvaluationAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        Task<WorkspaceTrendsReport?>? evaluationTask;
        lock (automaticEvaluationGate)
        {
            automaticEvaluationTasks.TryGetValue(normalizedSessionId, out evaluationTask);
        }
        evaluationTask ??= EvaluateRegisteredSessionAsync(normalizedSessionId, cancellationToken);
        await evaluationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceTrendsReport?> EvaluateRegisteredSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        var snapshot = runtime.Sessions.TryGetSnapshot(normalizedSessionId, out var currentSnapshot)
            ? currentSnapshot
            : await runtime.Sessions.LoadSnapshotAsync(
                normalizedSessionId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var registeredApp = runtime.Apps.Get(snapshot.AppId);
        if (string.IsNullOrWhiteSpace(registeredApp?.CodebasePath)
            || !Directory.Exists(registeredApp.CodebasePath))
        {
            return null;
        }

        if (File.Exists(ResolveSidecarPath(snapshot)))
        {
            return null;
        }

        var catalog = WorkspaceTrendsCatalog.Load(registeredApp.CodebasePath, cancellationToken);
        foreach (var warning in catalog.Warnings)
        {
            log.Warning(
                $"automatic_trends_configuration_warning sessionId={normalizedSessionId} appId={snapshot.AppId} reason=\"{warning}\"");
        }
        var trendsIds = catalog.Definitions
            .Where(static definition => definition.Enabled)
            .Where(definition => string.Equals(
                definition.AppId,
                snapshot.AppId,
                StringComparison.Ordinal))
            .Select(static definition => definition.TrendsId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (trendsIds.Length == 0)
        {
            return null;
        }

        return await EvaluateSessionCoreAsync(
            registeredApp.CodebasePath,
            snapshot.AppId,
            normalizedSessionId,
            trendsIds,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveRegisteredSessionEvaluationAsync(
        string sessionId,
        Task<WorkspaceTrendsReport?> evaluationTask)
    {
        try
        {
            var report = await evaluationTask.ConfigureAwait(false);
            if (report is not null)
            {
                log.Info(
                    $"automatic_trends_evaluation_completed sessionId={sessionId} appId={report.Context.AppId} status={report.Status} checkCount={report.Checks.Count} historyCount={report.History.Count}");
            }
        }
        catch (Exception exception)
        {
            log.Warning(
                $"automatic_trends_evaluation_failed sessionId={sessionId} reason=\"{exception.Message}\"");
        }
        finally
        {
            lock (automaticEvaluationGate)
            {
                if (automaticEvaluationTasks.TryGetValue(sessionId, out var currentTask)
                    && ReferenceEquals(currentTask, evaluationTask))
                {
                    automaticEvaluationTasks.Remove(sessionId);
                }
            }
        }
    }

    private async Task<WorkspaceTrendsReport?> EvaluateSessionCoreAsync(
        string workspacePath,
        string appId,
        string sessionId,
        IReadOnlyList<string> trendsIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(trendsIds);
        if (trendsIds.Count == 0)
        {
            return null;
        }

        var catalog = WorkspaceTrendsCatalog.Load(workspacePath, cancellationToken);
        var evaluatableTrendsIds = trendsIds
            .Where(trendsId =>
            {
                var matches = catalog.Definitions.Count(candidate => string.Equals(
                    candidate.TrendsId,
                    trendsId,
                    StringComparison.OrdinalIgnoreCase));
                return matches != 1 || catalog.FindDefinition(trendsId)?.Enabled != false;
            })
            .ToArray();
        if (evaluatableTrendsIds.Length == 0)
        {
            return null;
        }

        // The lightweight telemetry snapshot deliberately omits logs. Log anchors need
        // the complete retained streams, including entries older than the live tail.
        var needsLogs = catalog.Definitions.Any(definition =>
            evaluatableTrendsIds.Contains(definition.TrendsId, StringComparer.OrdinalIgnoreCase)
            && (definition.Span.Start.Log is not null || definition.Span.End.Log is not null));
        var snapshot = !needsLogs && runtime.Sessions.TryGetLiveContentSnapshot(sessionId, out var liveSnapshot)
            ? liveSnapshot
            : await runtime.Sessions.LoadSnapshotAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return CreateUnavailableReport(
                appId,
                sessionId,
                "The session capture could not be loaded.");
        }

        var checks = new List<WorkspaceTrendsCheckResult>();
        foreach (var trendsId in evaluatableTrendsIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trendsMatches = catalog.Definitions.Where(candidate => string.Equals(
                candidate.TrendsId,
                trendsId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (trendsMatches.Length != 1)
            {
                checks.Add(new WorkspaceTrendsCheckResult(
                    trendsId,
                    true,
                    WorkspaceTrendsStatus.Error,
                    trendsMatches.Length == 0
                        ? $"Trends definition '{trendsId}' was not found."
                        : $"Trends definition '{trendsId}' is ambiguous because {trendsMatches.Length:N0} files use that ID.",
                    [],
                    [],
                    string.Empty));
                continue;
            }
            var trendsCheck = trendsMatches[0];
            if (!trendsCheck.Enabled)
            {
                continue;
            }

            var spanResolution = WorkspaceTrendsSpanResolver.Resolve(
                snapshot,
                trendsCheck.Span);
            if (spanResolution.Status != WorkspaceTrendsStatus.Passed)
            {
                checks.Add(new WorkspaceTrendsCheckResult(
                    trendsCheck.TrendsId,
                    Required: false,
                    Status: WorkspaceTrendsStatus.Inconclusive,
                    Message: $"Automatic monitoring did not observe this span. {spanResolution.Message}",
                    SpanInstances: [],
                    Metrics: [],
                    DefinitionHash: trendsCheck.DefinitionHash));
                continue;
            }
            checks.Add(evaluator.Evaluate(snapshot, trendsCheck));
        }

        var context = CreateRunContext(snapshot);
        IReadOnlyList<WorkspaceTrendsHistoryDecision> history;
        string? persistenceError = null;
        try
        {
            lock (historyEvaluationGate)
            {
                lock (automaticEvaluationGate)
                {
                    if (deletedSessionIds.Contains(context.SessionId))
                    {
                        history = [];
                        return null;
                    }
                }

                historyStore.SaveMetrics(context, checks);
                history = EvaluateHistory(context, checks, catalog.HistoryDefinitions);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or Microsoft.Data.Sqlite.SqliteException
                                           or InvalidDataException)
        {
            persistenceError = exception.Message;
            history = [];
        }

        var status = persistenceError is null
            ? ResolveReportStatus(checks, history)
            : WorkspaceTrendsStatus.Error;
        var message = BuildReportMessage(checks, history, status, persistenceError);
        var report = new WorkspaceTrendsReport(
            ReportSchemaVersion,
            context,
            status,
            message,
            checks,
            history,
            null,
            persistenceError is null ? historyStore.DatabasePath : null);
        var sidecarPath = SaveSidecar(snapshot, report);
        report = report with { SidecarFilePath = sidecarPath };
        PublishEvents(report);
        return report;
    }

    private IReadOnlyList<WorkspaceTrendsHistoryDecision> EvaluateHistory(
        WorkspaceTrendsRunContext context,
        IReadOnlyList<WorkspaceTrendsCheckResult> checks,
        IReadOnlyList<WorkspaceTrendsHistoryDefinition> definitions)
    {
        var currentMetrics = checks
            .SelectMany(static check => check.Metrics)
            .Where(static result => result.Value.HasValue)
            .GroupBy(static result => result.MetricKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .GroupBy(static result => result.SpanGroup ?? string.Empty, StringComparer.Ordinal)
                    .Select(static spanGroup => spanGroup
                        .OrderByDescending(static result => result.InstanceIndex)
                        .First())
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var results = new List<WorkspaceTrendsHistoryDecision>();
        foreach (var definition in definitions)
        {
            if (!currentMetrics.TryGetValue(definition.MetricKey, out var metrics))
            {
                continue;
            }
            foreach (var metric in metrics)
            {
                var comparison = string.IsNullOrWhiteSpace(context.AppVersion)
                    ? WorkspaceTrendsHistoryComparison.UnversionedReference
                    : WorkspaceTrendsHistoryComparison.PreviousVersion;
                var seriesKey = CreateSeriesKey(context, definition.Cohort);
                var missingSeriesDimension = FindMissingSeriesDimension(context, definition.Cohort);
                if (missingSeriesDimension is not null)
                {
                    var error = new WorkspaceTrendsHistoryDecision(
                        definition.DecisionId,
                        definition.MetricKey,
                        comparison,
                        WorkspaceTrendsHistoryStatus.Error,
                        $"Cannot evaluate this signal because series dimension '{missingSeriesDimension}' is missing from the session profile.",
                        metric.Value!.Value,
                        null,
                        null,
                        null,
                        0,
                        definition.Baseline.MinimumRuns,
                        0,
                        definition.DefinitionHash)
                    {
                        SpanGroup = metric.SpanGroup,
                        MetricDefinitionHash = metric.MetricDefinitionHash,
                        Unit = metric.Unit,
                        Blocking = definition.Blocking,
                        SeriesKey = seriesKey
                    };
                    historyStore.SaveHistoryDecision(context, error, isBreach: false);
                    results.Add(error);
                    continue;
                }
                var baselineData = historyStore.LoadBaseline(
                    context,
                    metric,
                    definition,
                    comparison);
                var result = EvaluateHistory(
                    definition,
                    comparison,
                    metric.Value!.Value,
                    metric.SpanGroup,
                    context.AppVersion,
                    baselineData) with
                {
                    MetricDefinitionHash = metric.MetricDefinitionHash,
                    Unit = metric.Unit,
                    Blocking = definition.Blocking,
                    SeriesKey = seriesKey
                };
                var isBreach = IsRegression(
                    definition.Regression,
                    result.AbsoluteDelta,
                    result.RelativeDeltaPercent);
                historyStore.SaveHistoryDecision(context, result, isBreach);
                results.Add(result);
            }
        }
        return results;
    }

    internal static string? FindMissingSeriesDimension(
        WorkspaceTrendsRunContext context,
        IReadOnlyList<string> seriesBy)
    {
        foreach (var dimension in seriesBy)
        {
            var value = ResolveSeriesDimension(context, dimension);
            if (string.IsNullOrWhiteSpace(value))
            {
                return dimension;
            }
        }
        return null;
    }

    internal static string CreateSeriesKey(
        WorkspaceTrendsRunContext context,
        IReadOnlyList<string> seriesBy)
    {
        var identity = string.Join(
            "\u001f",
            seriesBy
                .OrderBy(static dimension => dimension, StringComparer.OrdinalIgnoreCase)
                .Select(dimension => $"{dimension.ToLowerInvariant()}={ResolveSeriesDimension(context, dimension)?.Trim()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24]
            .ToLowerInvariant();
    }

    private static string? ResolveSeriesDimension(
        WorkspaceTrendsRunContext context,
        string dimension)
        => dimension.ToLowerInvariant() switch
        {
            "buildnumber" => context.BuildNumber,
            "platform" => context.Platform,
            "devicemodel" => context.DeviceModel,
            "operatingsystemmajor" => context.OperatingSystemMajor,
            "buildconfiguration" => context.BuildConfiguration,
            _ => null
        };

    internal static WorkspaceTrendsHistoryDecision EvaluateHistory(
        WorkspaceTrendsHistoryDefinition definition,
        WorkspaceTrendsHistoryComparison comparison,
        double currentValue,
        string? spanGroup,
        string? currentAppVersion,
        WorkspaceTrendsHistoryBaselineData baselineData)
    {
        if (baselineData.Values.Count < definition.Baseline.MinimumRuns)
        {
            return new WorkspaceTrendsHistoryDecision(
                definition.DecisionId,
                definition.MetricKey,
                comparison,
                WorkspaceTrendsHistoryStatus.Warmup,
                BuildWarmupMessage(
                    comparison,
                    currentAppVersion,
                    baselineData.BaselineAppVersion,
                    baselineData.Values.Count,
                    definition.Baseline.MinimumRuns),
                currentValue,
                null,
                null,
                null,
                baselineData.Values.Count,
                definition.Baseline.MinimumRuns,
                0,
                definition.DefinitionHash)
            {
                BaselineAppVersion = baselineData.BaselineAppVersion,
                SpanGroup = spanGroup
            };
        }

        var baseline = Median(baselineData.Values);
        var absoluteDelta = currentValue - baseline;
        double? relativeDelta = Math.Abs(baseline) <= double.Epsilon
            ? null
            : absoluteDelta / Math.Abs(baseline) * 100d;
        var isBreach = IsRegression(definition.Regression, absoluteDelta, relativeDelta);
        var consecutive = isBreach ? baselineData.ConsecutiveBreachCount + 1 : 0;
        var status = isBreach && consecutive >= definition.Regression.ConsecutiveRuns
            ? WorkspaceTrendsHistoryStatus.Regressed
            : isBreach
                ? WorkspaceTrendsHistoryStatus.Suspect
            : !isBreach && baselineData.PreviousStatus == WorkspaceTrendsHistoryStatus.Regressed
                ? WorkspaceTrendsHistoryStatus.Recovered
                : WorkspaceTrendsHistoryStatus.Healthy;
        var message = status switch
        {
            WorkspaceTrendsHistoryStatus.Regressed => $"{DescribeComparison(comparison, baselineData.BaselineAppVersion)} regression detected after {consecutive:N0} consecutive comparable run(s).",
            WorkspaceTrendsHistoryStatus.Recovered => $"The {DescribeComparison(comparison, baselineData.BaselineAppVersion).ToLowerInvariant()} metric returned within its configured regression bounds.",
            WorkspaceTrendsHistoryStatus.Suspect => $"{DescribeComparison(comparison, baselineData.BaselineAppVersion)} regression threshold crossed for {consecutive:N0} of {definition.Regression.ConsecutiveRuns:N0} required comparable run(s).",
            _ => $"The metric is within its configured {DescribeComparison(comparison, baselineData.BaselineAppVersion).ToLowerInvariant()} regression bounds."
        };
        return new WorkspaceTrendsHistoryDecision(
            definition.DecisionId,
            definition.MetricKey,
            comparison,
            status,
            message,
            currentValue,
            baseline,
            absoluteDelta,
            relativeDelta,
            baselineData.Values.Count,
            definition.Baseline.MinimumRuns,
            consecutive,
            definition.DefinitionHash)
        {
            BaselineAppVersion = baselineData.BaselineAppVersion,
            SpanGroup = spanGroup
        };
    }

    private static string BuildWarmupMessage(
        WorkspaceTrendsHistoryComparison comparison,
        string? currentAppVersion,
        string? baselineAppVersion,
        int baselineRunCount,
        int minimumBaselineRunCount)
        => comparison switch
        {
            WorkspaceTrendsHistoryComparison.UnversionedReference =>
                $"Establishing the {currentAppVersion ?? "unversioned"} baseline: {baselineRunCount:N0} of {minimumBaselineRunCount:N0} comparable run(s) are available.",
            WorkspaceTrendsHistoryComparison.PreviousVersion when baselineAppVersion is null =>
                $"No earlier app version has comparable data yet; {currentAppVersion ?? "this version"} is becoming the baseline for the next version.",
            _ =>
                $"Release comparison requires {minimumBaselineRunCount:N0} baseline run(s) from {baselineAppVersion}; {baselineRunCount:N0} are available."
        };

    private static string DescribeComparison(
        WorkspaceTrendsHistoryComparison comparison,
        string? baselineAppVersion)
        => comparison switch
        {
            WorkspaceTrendsHistoryComparison.UnversionedReference => "Unversioned reference",
            _ => $"Previous-version{(baselineAppVersion is null ? string.Empty : $" ({baselineAppVersion})")}"
        };

    internal static bool IsRegression(
        WorkspaceTrendsHistoryRegressionDefinition regression,
        double? absoluteDelta,
        double? relativeDelta)
    {
        if (!absoluteDelta.HasValue)
        {
            return false;
        }
        var configuredThresholds = new List<bool>();
        if (regression.AbsoluteIncrease.HasValue)
        {
            configuredThresholds.Add(absoluteDelta.Value >= regression.AbsoluteIncrease.Value);
        }
        if (regression.AbsoluteDecrease.HasValue)
        {
            configuredThresholds.Add(-absoluteDelta.Value >= regression.AbsoluteDecrease.Value);
        }
        if (regression.RelativeIncreasePercent.HasValue)
        {
            configuredThresholds.Add(relativeDelta >= regression.RelativeIncreasePercent.Value);
        }
        if (regression.RelativeDecreasePercent.HasValue)
        {
            configuredThresholds.Add(-relativeDelta >= regression.RelativeDecreasePercent.Value);
        }
        return configuredThresholds.Count > 0
               && (regression.RequireAllThresholds
                   ? configuredThresholds.All(static threshold => threshold)
                   : configuredThresholds.Any(static threshold => threshold));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    internal static WorkspaceTrendsRunContext CreateRunContext(
        AppSessionSnapshot snapshot)
    {
        var osVersion = snapshot.DeviceProfile?.Device?.OsVersion;
        var osMajor = string.IsNullOrWhiteSpace(osVersion)
            ? null
            : osVersion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var appVersion = NormalizeVersion(snapshot.DeviceProfile?.App?.VersionName);
        return new WorkspaceTrendsRunContext(
            Guid.CreateVersion7().ToString("N"),
            $"session:{snapshot.SessionId}",
            snapshot.SessionId,
            snapshot.AppId,
            appVersion,
            snapshot.DeviceProfile?.App?.BuildNumber,
            snapshot.DeviceProfile?.Device?.OsName,
            snapshot.DeviceProfile?.Device?.Model,
            osMajor,
            ReadBuildConfiguration(snapshot),
            DateTimeOffset.UtcNow);
    }

    private static string? NormalizeVersion(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadBuildConfiguration(AppSessionSnapshot snapshot)
    {
        if (snapshot.CustomProperties is null)
        {
            return null;
        }
        foreach (var propertyName in new[] { "buildConfiguration", "build.configuration", "configuration" })
        {
            if (snapshot.CustomProperties[propertyName] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var result)
                && !string.IsNullOrWhiteSpace(result))
            {
                return result.Trim();
            }
        }
        return null;
    }

    private WorkspaceTrendsReport CreateUnavailableReport(
        string appId,
        string sessionId,
        string message)
    {
        var context = new WorkspaceTrendsRunContext(
            Guid.CreateVersion7().ToString("N"),
            $"session:{sessionId}",
            sessionId,
            appId,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
        return new WorkspaceTrendsReport(
            ReportSchemaVersion,
            context,
            WorkspaceTrendsStatus.Error,
            message,
            [],
            [],
            null,
            null);
    }

    internal static WorkspaceTrendsStatus ResolveReportStatus(
        IReadOnlyList<WorkspaceTrendsCheckResult> checks,
        IReadOnlyList<WorkspaceTrendsHistoryDecision> history)
    {
        var required = checks.Where(static check => check.Required).ToArray();
        if (required.Any(static check => check.Status == WorkspaceTrendsStatus.Error))
        {
            return WorkspaceTrendsStatus.Error;
        }
        if (required.Any(static check => check.Status == WorkspaceTrendsStatus.Failed))
        {
            return WorkspaceTrendsStatus.Failed;
        }
        if (required.Any(static check => check.Status == WorkspaceTrendsStatus.Inconclusive))
        {
            return WorkspaceTrendsStatus.Inconclusive;
        }
        if (history.Any(static history => history.Blocking && history.Status == WorkspaceTrendsHistoryStatus.Error))
        {
            return WorkspaceTrendsStatus.Error;
        }
        if (history.Any(static history => history.Blocking && history.Status == WorkspaceTrendsHistoryStatus.Regressed))
        {
            return WorkspaceTrendsStatus.Failed;
        }
        return WorkspaceTrendsStatus.Passed;
    }

    private static string BuildReportMessage(
        IReadOnlyList<WorkspaceTrendsCheckResult> checks,
        IReadOnlyList<WorkspaceTrendsHistoryDecision> history,
        WorkspaceTrendsStatus status,
        string? persistenceError)
    {
        var passed = checks.Count(static check => check.Status == WorkspaceTrendsStatus.Passed);
        var skipped = checks.Count(static check =>
            !check.Required && check.Status == WorkspaceTrendsStatus.Inconclusive);
        var blockingRegressions = history.Count(static history =>
            history.Blocking && history.Status == WorkspaceTrendsHistoryStatus.Regressed);
        var blockingErrors = history.Count(static history =>
            history.Blocking && history.Status == WorkspaceTrendsHistoryStatus.Error);
        var message = blockingErrors > 0
            ? $"{blockingErrors:N0} blocking history signal(s) could not be evaluated."
            : blockingRegressions > 0
            ? $"{blockingRegressions:N0} blocking history regression(s) detected."
            : status == WorkspaceTrendsStatus.Passed && skipped == 0
            ? $"All {checks.Count:N0} trends check(s) passed."
            : status == WorkspaceTrendsStatus.Passed
                ? $"{passed:N0} trends check(s) passed; {skipped:N0} unobserved span(s) were skipped."
            : $"{passed:N0} of {checks.Count:N0} trends check(s) passed; trends status is {status}.";
        return persistenceError is null
            ? message
            : message + $" Trends history could not be persisted: {persistenceError}";
    }

    private string? SaveSidecar(AppSessionSnapshot snapshot, WorkspaceTrendsReport report)
    {
        try
        {
            var filePath = ResolveSidecarPath(snapshot);
            var directoryPath = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(directoryPath);
            var temporaryPath = filePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(report with { SidecarFilePath = filePath }, reportJson));
            File.Move(temporaryPath, filePath, overwrite: true);
            return filePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string ResolveSidecarPath(AppSessionSnapshot snapshot)
        => Path.Combine(
            applicationPaths.ApplicationDataPath,
            "session-captures",
            FileNameUtil.Sanitize(snapshot.AppId),
            FileNameUtil.Sanitize(snapshot.SessionId),
            "trends-results.json");

    private void PublishEvents(WorkspaceTrendsReport report)
    {
        var failedChecks = report.Checks.Where(static check =>
            check.Required && check.Status is WorkspaceTrendsStatus.Failed or WorkspaceTrendsStatus.Error).ToArray();
        foreach (var check in failedChecks)
        {
            runtime.PublishTrendsEvent(new RuntimeTrendsEvent(
                DateTimeOffset.UtcNow,
                RuntimeTrendsEventKind.CheckFailed,
                report.Context.SessionId,
                report.Context.AppId,
                check.TrendsId,
                check.Message,
                check.Status));
        }
        if (report.Status == WorkspaceTrendsStatus.Error && failedChecks.Length == 0)
        {
            runtime.PublishTrendsEvent(new RuntimeTrendsEvent(
                DateTimeOffset.UtcNow,
                RuntimeTrendsEventKind.CheckFailed,
                report.Context.SessionId,
                report.Context.AppId,
                "trends.persistence",
                report.Message,
                WorkspaceTrendsStatus.Error));
        }
        foreach (var history in report.History.Where(static history =>
                     history.Status is WorkspaceTrendsHistoryStatus.Regressed or WorkspaceTrendsHistoryStatus.Recovered))
        {
            runtime.PublishTrendsEvent(new RuntimeTrendsEvent(
                DateTimeOffset.UtcNow,
                history.Status == WorkspaceTrendsHistoryStatus.Regressed
                    ? RuntimeTrendsEventKind.RegressionDetected
                    : RuntimeTrendsEventKind.RegressionRecovered,
                report.Context.SessionId,
                report.Context.AppId,
                history.DecisionId,
                history.Message,
                HistoryStatus: history.Status)
            {
                SpanGroup = history.SpanGroup,
                HistoryComparison = history.Comparison
            });
        }
    }

    private sealed record WorkspaceTrendsSeriesKey(
        string AppId,
        string DecisionId,
        string MetricKey,
        WorkspaceTrendsHistoryComparison Comparison,
        string? AppVersion,
        string? BaselineAppVersion,
        string? SpanGroup,
        string SeriesKey,
        string DefinitionHash,
        string MetricDefinitionHash);
}
