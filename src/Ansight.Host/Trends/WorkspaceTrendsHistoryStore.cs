using Microsoft.Data.Sqlite;

namespace Ansight.Host.Trends;

internal sealed record WorkspaceTrendsHistoryBaselineData(
    IReadOnlyList<double> Values,
    WorkspaceTrendsHistoryStatus? PreviousStatus,
    int ConsecutiveBreachCount,
    string? BaselineAppVersion);

internal sealed record WorkspaceTrendsHistoryRebuildScope(
    string AppId,
    string? AppVersion,
    bool HasAppVersionFilter,
    string? DecisionId);

internal sealed record WorkspaceTrendsHistoryRebuildRow(
    WorkspaceTrendsRunContext Context,
    WorkspaceTrendsHistoryDecision Result,
    bool IsBreach);

internal sealed class WorkspaceTrendsHistoryStore
{
    private static readonly Lock providerGate = new();
    private static bool providerInitialized;
    private readonly string databasePath;
    private readonly string legacyDatabasePath;
    private readonly Lock databaseGate = new();

    public WorkspaceTrendsHistoryStore(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        var applicationDataPath = applicationPaths.ApplicationDataPath;
        databasePath = Path.Combine(
            applicationDataPath,
            "trends",
            "trends-history.sqlite3");
        legacyDatabasePath = Path.Combine(
            applicationDataPath,
            "performance",
            "performance-history.sqlite3");
    }

    public string DatabasePath => databasePath;

    public int DeleteSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        EnsureDatabase();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var removedRows = 0;
        foreach (var tableName in new[] { "trends_history", "trends_metrics" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {tableName} WHERE session_id = $session_id;";
            command.Parameters.AddWithValue("$session_id", sessionId.Trim());
            removedRows += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return removedRows;
    }

    public IReadOnlyList<WorkspaceTrendsMetricHistoryEntry> LoadMetricHistory(
        string? appId,
        string? metricKey,
        int limit,
        string? spanGroup = null)
    {
        EnsureDatabase();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(appId))
        {
            filters.Add("app_id = $app_id");
            command.Parameters.AddWithValue("$app_id", appId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(metricKey))
        {
            filters.Add("metric_key = $metric_key");
            command.Parameters.AddWithValue("$metric_key", metricKey.Trim());
        }
        if (!string.IsNullOrWhiteSpace(spanGroup))
        {
            filters.Add("span_group = $span_group");
            command.Parameters.AddWithValue("$span_group", spanGroup.Trim());
        }
        var whereClause = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
        command.CommandText = $"""
            SELECT evaluation_id, run_id, session_id, app_id, app_version, build_number,
                   platform, device_model, operating_system_major, build_configuration,
                   trends_id, span_group, instance_index, metric_id, metric_key,
                   metric_definition_hash, value, unit, metric_status, evaluated_at_utc
            FROM trends_metrics
            {whereClause}
            ORDER BY evaluated_at_utc DESC, instance_index DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<WorkspaceTrendsMetricHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new WorkspaceTrendsMetricHistoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableString(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableString(reader, 8),
                ReadNullableString(reader, 9),
                reader.GetString(10),
                reader.GetInt32(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetDouble(16),
                reader.GetString(17),
                ParseTrendsStatus(reader.GetString(18)),
                DateTimeOffset.Parse(reader.GetString(19), null, System.Globalization.DateTimeStyles.RoundtripKind))
            {
                SpanGroup = ReadSpanGroup(reader, 11),
                MetricDefinitionHash = reader.GetString(15)
            });
        }
        return entries;
    }

    public IReadOnlyList<string> LoadMetricAppIds()
    {
        EnsureDatabase();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT app_id FROM trends_metrics ORDER BY app_id;";
        var appIds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            appIds.Add(reader.GetString(0));
        }
        return appIds;
    }

    public IReadOnlyList<WorkspaceTrendsMetricHistoryEntry> LoadMetricsForHistoryRebuild(
        string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        EnsureDatabase();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evaluation_id, run_id, session_id, app_id, app_version, build_number,
                   platform, device_model, operating_system_major, build_configuration,
                   trends_id, span_group, instance_index, metric_id, metric_key,
                   metric_definition_hash, value, unit, metric_status, evaluated_at_utc
            FROM trends_metrics
            WHERE app_id = $app_id
            ORDER BY evaluated_at_utc ASC, evaluation_id ASC, instance_index ASC;
            """;
        command.Parameters.AddWithValue("$app_id", appId.Trim());
        var entries = new List<WorkspaceTrendsMetricHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new WorkspaceTrendsMetricHistoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableString(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableString(reader, 8),
                ReadNullableString(reader, 9),
                reader.GetString(10),
                reader.GetInt32(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetDouble(16),
                reader.GetString(17),
                ParseTrendsStatus(reader.GetString(18)),
                DateTimeOffset.Parse(reader.GetString(19), null, System.Globalization.DateTimeStyles.RoundtripKind))
            {
                SpanGroup = ReadSpanGroup(reader, 11),
                MetricDefinitionHash = reader.GetString(15)
            });
        }
        return entries;
    }

    public IReadOnlyList<WorkspaceTrendsHistoryEntry> LoadHistory(
        string? appId,
        string? decisionId,
        int limit,
        string? spanGroup = null,
        bool limitPerSeries = false)
    {
        EnsureDatabase();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(appId))
        {
            filters.Add("app_id = $app_id");
            command.Parameters.AddWithValue("$app_id", appId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(decisionId))
        {
            filters.Add("decision_id = $decision_id");
            command.Parameters.AddWithValue("$decision_id", decisionId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(spanGroup))
        {
            filters.Add("span_group = $span_group");
            command.Parameters.AddWithValue("$span_group", spanGroup.Trim());
        }
        var whereClause = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
        var historySource = limitPerSeries
            ? $"""
              FROM (
                  SELECT *, ROW_NUMBER() OVER (
                      PARTITION BY app_id, decision_id, metric_key, comparison, span_group,
                                   COALESCE(app_version, ''), COALESCE(baseline_app_version, ''),
                                   series_key, definition_hash, metric_definition_hash
                      ORDER BY evaluated_at_utc DESC) AS series_rank
                  FROM trends_history
                  {whereClause}
              )
              WHERE series_rank <= $limit
              """
            : $"""
              FROM trends_history
              {whereClause}
              """;
        command.CommandText = $"""
            SELECT evaluation_id, run_id, session_id, app_id, app_version, build_number,
                   decision_id, metric_key, comparison, span_group, status, current_value, baseline_value,
                   absolute_delta, relative_delta_percent, baseline_run_count,
                   minimum_baseline_run_count, consecutive_breach_count, evaluated_at_utc,
                   baseline_app_version, platform, device_model, operating_system_major,
                   build_configuration, definition_hash, metric_definition_hash,
                   unit, message, blocking, series_key
            {historySource}
            ORDER BY evaluated_at_utc DESC
            {(limitPerSeries ? string.Empty : "LIMIT $limit")};
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<WorkspaceTrendsHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new WorkspaceTrendsHistoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                reader.GetString(6),
                reader.GetString(7),
                ParseHistoryComparison(reader.GetString(8)),
                ParseHistoryStatus(reader.GetString(10)),
                reader.GetDouble(11),
                ReadNullableDouble(reader, 12),
                ReadNullableDouble(reader, 13),
                ReadNullableDouble(reader, 14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                DateTimeOffset.Parse(reader.GetString(18), null, System.Globalization.DateTimeStyles.RoundtripKind))
            {
                BaselineAppVersion = ReadNullableString(reader, 19),
                SpanGroup = ReadSpanGroup(reader, 9),
                Platform = ReadNullableString(reader, 20),
                DeviceModel = ReadNullableString(reader, 21),
                OperatingSystemMajor = ReadNullableString(reader, 22),
                BuildConfiguration = ReadNullableString(reader, 23),
                DefinitionHash = reader.GetString(24),
                MetricDefinitionHash = reader.GetString(25),
                Unit = reader.GetString(26),
                Message = reader.GetString(27),
                Blocking = reader.GetInt32(28) == 1,
                SeriesKey = reader.GetString(29)
            });
        }
        return entries;
    }

    public void SaveMetrics(
        WorkspaceTrendsRunContext context,
        IReadOnlyList<WorkspaceTrendsCheckResult> checks)
    {
        EnsureDatabase();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var check in checks)
        {
            foreach (var metric in check.Metrics.Where(static result => result.Value.HasValue))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO trends_metrics (
                        evaluation_id, run_id, session_id, app_id, app_version, build_number,
                        platform, device_model, operating_system_major, build_configuration,
                        trends_id, span_group, instance_index, metric_id, metric_key,
                        metric_definition_hash, value, unit, metric_status, evaluated_at_utc)
                    VALUES (
                        $evaluation_id, $run_id, $session_id, $app_id, $app_version, $build_number,
                        $platform, $device_model, $operating_system_major, $build_configuration,
                        $trends_id, $span_group, $instance_index, $metric_id, $metric_key,
                        $metric_definition_hash, $value, $unit, $metric_status, $evaluated_at_utc);
                    """;
                AddContextParameters(command, context);
                command.Parameters.AddWithValue("$trends_id", metric.TrendsId);
                command.Parameters.AddWithValue("$span_group", NormalizeSpanGroup(metric.SpanGroup));
                command.Parameters.AddWithValue("$instance_index", metric.InstanceIndex);
                command.Parameters.AddWithValue("$metric_id", metric.MetricId);
                command.Parameters.AddWithValue("$metric_key", metric.MetricKey);
                command.Parameters.AddWithValue("$metric_definition_hash", metric.MetricDefinitionHash);
                command.Parameters.AddWithValue("$value", metric.Value!.Value);
                command.Parameters.AddWithValue("$unit", metric.Unit);
                command.Parameters.AddWithValue("$metric_status", metric.Status.ToString());
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    public WorkspaceTrendsHistoryBaselineData LoadBaseline(
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsMetricResult metric,
        WorkspaceTrendsHistoryDefinition historyDefinition,
        WorkspaceTrendsHistoryComparison comparison)
    {
        EnsureDatabase();
        using var connection = OpenConnection();
        var baselineAppVersion = comparison switch
        {
            WorkspaceTrendsHistoryComparison.UnversionedReference => context.AppVersion,
            WorkspaceTrendsHistoryComparison.PreviousVersion when !string.IsNullOrWhiteSpace(context.AppVersion)
                => FindPinnedBaselineAppVersion(connection, context, metric, historyDefinition)
                   ?? FindBaselineAppVersion(connection, context, metric, historyDefinition),
            _ => null
        };
        using var command = connection.CreateCommand();
        var cohortClause = BuildCohortClause(command, context, historyDefinition.Cohort);
        var versionClause = comparison switch
        {
            WorkspaceTrendsHistoryComparison.UnversionedReference when baselineAppVersion is null
                => "AND app_version IS NULL",
            WorkspaceTrendsHistoryComparison.UnversionedReference
                => "AND app_version = $baseline_app_version",
            WorkspaceTrendsHistoryComparison.PreviousVersion when baselineAppVersion is not null
                => "AND app_version = $baseline_app_version",
            _ => "AND 1 = 0"
        };
        if (baselineAppVersion is not null)
        {
            command.Parameters.AddWithValue("$baseline_app_version", baselineAppVersion);
        }
        var baselineOrder = comparison == WorkspaceTrendsHistoryComparison.UnversionedReference ? "ASC" : "DESC";
        command.CommandText = $"""
            WITH comparable AS (
                SELECT value, evaluated_at_utc,
                       ROW_NUMBER() OVER (
                           PARTITION BY evaluation_id
                           ORDER BY instance_index DESC) AS instance_rank
                FROM trends_metrics
                WHERE metric_key = $metric_key
                  AND metric_definition_hash = $metric_definition_hash
                  AND app_id = $app_id
                  AND span_group = $span_group
                  AND evaluation_id <> $evaluation_id
                  {cohortClause}
                  {versionClause}
            )
            SELECT value
            FROM comparable
            WHERE instance_rank = 1
            ORDER BY evaluated_at_utc {baselineOrder}
            LIMIT $run_limit;
            """;
        command.Parameters.AddWithValue("$metric_key", metric.MetricKey);
        command.Parameters.AddWithValue("$metric_definition_hash", metric.MetricDefinitionHash);
        command.Parameters.AddWithValue("$app_id", context.AppId);
        command.Parameters.AddWithValue("$span_group", NormalizeSpanGroup(metric.SpanGroup));
        command.Parameters.AddWithValue("$evaluation_id", context.EvaluationId);
        command.Parameters.AddWithValue("$run_limit", historyDefinition.Baseline.Runs);
        var values = new List<double>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                values.Add(reader.GetDouble(0));
            }
        }

        using var statusCommand = connection.CreateCommand();
        var historyCohortClause = BuildCohortClause(statusCommand, context, historyDefinition.Cohort);
        statusCommand.CommandText = $"""
            SELECT status, consecutive_breach_count
            FROM trends_history
            WHERE decision_id = $decision_id
              AND definition_hash = $definition_hash
              AND metric_definition_hash = $metric_definition_hash
              AND app_id = $app_id
              AND span_group = $span_group
              AND comparison = $comparison
              AND COALESCE(app_version, '') = COALESCE($current_app_version, '')
              AND COALESCE(baseline_app_version, '') = COALESCE($baseline_app_version, '')
              {historyCohortClause}
            ORDER BY evaluated_at_utc DESC
            LIMIT 1;
            """;
        statusCommand.Parameters.AddWithValue("$decision_id", historyDefinition.DecisionId);
        statusCommand.Parameters.AddWithValue("$definition_hash", historyDefinition.DefinitionHash);
        statusCommand.Parameters.AddWithValue("$metric_definition_hash", metric.MetricDefinitionHash);
        statusCommand.Parameters.AddWithValue("$app_id", context.AppId);
        statusCommand.Parameters.AddWithValue("$span_group", NormalizeSpanGroup(metric.SpanGroup));
        statusCommand.Parameters.AddWithValue("$comparison", comparison.ToString());
        AddNullable(statusCommand, "$current_app_version", context.AppVersion);
        AddNullable(statusCommand, "$baseline_app_version", baselineAppVersion);
        WorkspaceTrendsHistoryStatus? previousStatus = null;
        var consecutiveBreachCount = 0;
        using (var reader = statusCommand.ExecuteReader())
        {
            if (reader.Read())
            {
                if (Enum.TryParse<WorkspaceTrendsHistoryStatus>(reader.GetString(0), out var parsedStatus))
                {
                    previousStatus = parsedStatus;
                }
                consecutiveBreachCount = reader.GetInt32(1);
            }
        }
        return new WorkspaceTrendsHistoryBaselineData(
            values,
            previousStatus,
            consecutiveBreachCount,
            baselineAppVersion);
    }

    public void SaveHistoryDecision(
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsHistoryDecision result,
        bool isBreach)
    {
        EnsureDatabase();
        using var connection = OpenConnection();
        InsertHistoryDecision(connection, transaction: null, context, result, isBreach);
    }

    public int ReplaceHistory(
        IReadOnlyList<WorkspaceTrendsHistoryRebuildScope> scopes,
        IReadOnlyList<WorkspaceTrendsHistoryRebuildRow> rows)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(rows);
        EnsureDatabase();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var removedCount = 0;
        foreach (var scope in scopes)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            var filters = new List<string> { "app_id = $app_id" };
            delete.Parameters.AddWithValue("$app_id", scope.AppId);
            if (scope.HasAppVersionFilter)
            {
                filters.Add("COALESCE(app_version, '') = COALESCE($app_version, '')");
                AddNullable(delete, "$app_version", scope.AppVersion);
            }
            if (!string.IsNullOrWhiteSpace(scope.DecisionId))
            {
                filters.Add("decision_id = $decision_id");
                delete.Parameters.AddWithValue("$decision_id", scope.DecisionId.Trim());
            }
            delete.CommandText = $"DELETE FROM trends_history WHERE {string.Join(" AND ", filters)};";
            removedCount += delete.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            InsertHistoryDecision(connection, transaction, row.Context, row.Result, row.IsBreach);
        }
        transaction.Commit();
        return removedCount;
    }

    public int CountHistory(IReadOnlyList<WorkspaceTrendsHistoryRebuildScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        EnsureDatabase();
        using var connection = OpenConnection();
        var count = 0;
        foreach (var scope in scopes)
        {
            using var command = connection.CreateCommand();
            var filters = new List<string> { "app_id = $app_id" };
            command.Parameters.AddWithValue("$app_id", scope.AppId);
            if (scope.HasAppVersionFilter)
            {
                filters.Add("COALESCE(app_version, '') = COALESCE($app_version, '')");
                AddNullable(command, "$app_version", scope.AppVersion);
            }
            if (!string.IsNullOrWhiteSpace(scope.DecisionId))
            {
                filters.Add("decision_id = $decision_id");
                command.Parameters.AddWithValue("$decision_id", scope.DecisionId.Trim());
            }
            command.CommandText = $"SELECT COUNT(*) FROM trends_history WHERE {string.Join(" AND ", filters)};";
            count += Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return count;
    }

    private static void InsertHistoryDecision(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsHistoryDecision result,
        bool isBreach)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO trends_history (
                evaluation_id, run_id, session_id, app_id, app_version, build_number,
                platform, device_model, operating_system_major, build_configuration,
                decision_id, metric_key, comparison, span_group, status, current_value,
                baseline_value, absolute_delta, relative_delta_percent, baseline_run_count,
                minimum_baseline_run_count,
                consecutive_breach_count, is_breach, definition_hash, evaluated_at_utc,
                baseline_app_version, metric_definition_hash, unit, message, blocking, series_key)
            VALUES (
                $evaluation_id, $run_id, $session_id, $app_id, $app_version, $build_number,
                $platform, $device_model, $operating_system_major, $build_configuration,
                $decision_id, $metric_key, $comparison, $span_group, $status, $current_value,
                $baseline_value, $absolute_delta, $relative_delta_percent, $baseline_run_count,
                $minimum_baseline_run_count,
                $consecutive_breach_count, $is_breach, $definition_hash, $evaluated_at_utc,
                $baseline_app_version, $metric_definition_hash, $unit, $message, $blocking, $series_key);
            """;
        command.Parameters.AddWithValue("$evaluation_id", context.EvaluationId);
        command.Parameters.AddWithValue("$run_id", context.RunId);
        command.Parameters.AddWithValue("$session_id", context.SessionId);
        command.Parameters.AddWithValue("$app_id", context.AppId);
        AddNullable(command, "$app_version", context.AppVersion);
        AddNullable(command, "$build_number", context.BuildNumber);
        AddNullable(command, "$platform", context.Platform);
        AddNullable(command, "$device_model", context.DeviceModel);
        AddNullable(command, "$operating_system_major", context.OperatingSystemMajor);
        AddNullable(command, "$build_configuration", context.BuildConfiguration);
        command.Parameters.AddWithValue("$decision_id", result.DecisionId);
        command.Parameters.AddWithValue("$metric_key", result.MetricKey);
        command.Parameters.AddWithValue("$comparison", result.Comparison.ToString());
        command.Parameters.AddWithValue("$span_group", NormalizeSpanGroup(result.SpanGroup));
        command.Parameters.AddWithValue("$status", result.Status.ToString());
        command.Parameters.AddWithValue("$current_value", result.CurrentValue);
        AddNullable(command, "$baseline_value", result.BaselineValue);
        AddNullable(command, "$absolute_delta", result.AbsoluteDelta);
        AddNullable(command, "$relative_delta_percent", result.RelativeDeltaPercent);
        command.Parameters.AddWithValue("$baseline_run_count", result.BaselineRunCount);
        command.Parameters.AddWithValue("$minimum_baseline_run_count", result.MinimumBaselineRunCount);
        command.Parameters.AddWithValue("$consecutive_breach_count", result.ConsecutiveBreachCount);
        command.Parameters.AddWithValue("$is_breach", isBreach ? 1 : 0);
        command.Parameters.AddWithValue("$definition_hash", result.DefinitionHash);
        command.Parameters.AddWithValue("$evaluated_at_utc", context.EvaluatedAtUtc.ToString("O"));
        AddNullable(command, "$baseline_app_version", result.BaselineAppVersion);
        command.Parameters.AddWithValue("$metric_definition_hash", result.MetricDefinitionHash);
        command.Parameters.AddWithValue("$unit", result.Unit);
        command.Parameters.AddWithValue("$message", result.Message);
        command.Parameters.AddWithValue("$blocking", result.Blocking ? 1 : 0);
        command.Parameters.AddWithValue("$series_key", result.SeriesKey);
        command.ExecuteNonQuery();
    }

    private void EnsureDatabase()
    {
        lock (databaseGate)
        {
            EnsureDatabaseCore();
        }
    }

    private void EnsureDatabaseCore()
    {
        EnsureProviderInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        MigrateLegacyDatabaseFileIfNeeded();
        using var connection = OpenConnection();
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            pragmaCommand.ExecuteNonQuery();
        }
        MigrateLegacySchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS trends_metrics (
                evaluation_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                app_id TEXT NOT NULL,
                app_version TEXT NULL,
                build_number TEXT NULL,
                platform TEXT NULL,
                device_model TEXT NULL,
                operating_system_major TEXT NULL,
                build_configuration TEXT NULL,
                trends_id TEXT NOT NULL,
                span_group TEXT NOT NULL DEFAULT '',
                instance_index INTEGER NOT NULL,
                metric_id TEXT NOT NULL,
                metric_key TEXT NOT NULL,
                metric_definition_hash TEXT NOT NULL,
                value REAL NOT NULL,
                unit TEXT NOT NULL,
                metric_status TEXT NOT NULL,
                evaluated_at_utc TEXT NOT NULL,
                PRIMARY KEY (evaluation_id, trends_id, metric_id, instance_index)
            );
            CREATE INDEX IF NOT EXISTS ix_trends_metrics_series
                ON trends_metrics (
                    app_id, metric_key, metric_definition_hash, evaluated_at_utc DESC);
            CREATE TABLE IF NOT EXISTS trends_history (
                evaluation_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                app_id TEXT NOT NULL,
                app_version TEXT NULL,
                build_number TEXT NULL,
                platform TEXT NULL,
                device_model TEXT NULL,
                operating_system_major TEXT NULL,
                build_configuration TEXT NULL,
                decision_id TEXT NOT NULL,
                metric_key TEXT NOT NULL,
                comparison TEXT NOT NULL,
                span_group TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL,
                current_value REAL NOT NULL,
                baseline_value REAL NULL,
                absolute_delta REAL NULL,
                relative_delta_percent REAL NULL,
                baseline_run_count INTEGER NOT NULL,
                minimum_baseline_run_count INTEGER NOT NULL,
                consecutive_breach_count INTEGER NOT NULL,
                is_breach INTEGER NOT NULL,
                definition_hash TEXT NOT NULL,
                evaluated_at_utc TEXT NOT NULL,
                baseline_app_version TEXT NULL,
                metric_definition_hash TEXT NOT NULL DEFAULT '',
                unit TEXT NOT NULL DEFAULT '',
                message TEXT NOT NULL DEFAULT '',
                blocking INTEGER NOT NULL DEFAULT 0,
                series_key TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (evaluation_id, decision_id, span_group, comparison)
            );
            """;
        command.ExecuteNonQuery();
        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_trends_history_series
                ON trends_history (
                    app_id, decision_id, span_group, comparison, evaluated_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_trends_history_series_key
                ON trends_history (
                    app_id, decision_id, span_group, comparison, series_key, evaluated_at_utc DESC);
            """;
        indexCommand.ExecuteNonQuery();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version = 3;";
        versionCommand.ExecuteNonQuery();
    }

    private void MigrateLegacyDatabaseFileIfNeeded()
    {
        if (File.Exists(databasePath) || !File.Exists(legacyDatabasePath))
        {
            return;
        }

        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = legacyDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void MigrateLegacySchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        if (TableExists(connection, "performance_metrics"))
        {
            command.CommandText = "ALTER TABLE performance_metrics RENAME TO trends_metrics;";
            command.ExecuteNonQuery();
        }
        if (TableExists(connection, "performance_history"))
        {
            command.CommandText = "ALTER TABLE performance_history RENAME TO trends_history;";
            command.ExecuteNonQuery();
        }
        if (TableExists(connection, "trends_metrics") && ColumnExists(connection, "trends_metrics", "performance_id"))
        {
            command.CommandText = "ALTER TABLE trends_metrics RENAME COLUMN performance_id TO trends_id;";
            command.ExecuteNonQuery();
        }
        RenameLegacyGroupColumn(connection, command, "trends_metrics");
        RenameLegacyGroupColumn(connection, command, "trends_history");
        if (!TableExists(connection, "trends_metrics")
            || (!ColumnExists(connection, "trends_metrics", "test_id")
                && !ColumnExists(connection, "trends_metrics", "functional_succeeded")))
        {
            return;
        }

        command.CommandText = """
            CREATE TABLE trends_metrics_without_test_coupling (
                evaluation_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                app_id TEXT NOT NULL,
                app_version TEXT NULL,
                build_number TEXT NULL,
                platform TEXT NULL,
                device_model TEXT NULL,
                operating_system_major TEXT NULL,
                build_configuration TEXT NULL,
                trends_id TEXT NOT NULL,
                span_group TEXT NOT NULL DEFAULT '',
                instance_index INTEGER NOT NULL,
                metric_id TEXT NOT NULL,
                metric_key TEXT NOT NULL,
                metric_definition_hash TEXT NOT NULL,
                value REAL NOT NULL,
                unit TEXT NOT NULL,
                metric_status TEXT NOT NULL,
                evaluated_at_utc TEXT NOT NULL,
                PRIMARY KEY (evaluation_id, trends_id, metric_id, instance_index)
            );
            INSERT OR REPLACE INTO trends_metrics_without_test_coupling (
                evaluation_id, run_id, session_id, app_id, app_version, build_number,
                platform, device_model, operating_system_major, build_configuration,
                trends_id, span_group, instance_index, metric_id, metric_key,
                metric_definition_hash, value, unit, metric_status, evaluated_at_utc)
            SELECT evaluation_id, run_id, session_id, app_id, app_version, build_number,
                   platform, device_model, operating_system_major, build_configuration,
                   trends_id, span_group, instance_index, metric_id, metric_key,
                   metric_definition_hash, value, unit, metric_status, evaluated_at_utc
            FROM trends_metrics;
            DROP TABLE trends_metrics;
            ALTER TABLE trends_metrics_without_test_coupling RENAME TO trends_metrics;
            """;
        command.ExecuteNonQuery();
    }

    private static void RenameLegacyGroupColumn(
        SqliteConnection connection,
        SqliteCommand command,
        string tableName)
    {
        const string legacyColumnName = "window_group";
        if (!TableExists(connection, tableName)
            || !ColumnExists(connection, tableName, legacyColumnName)
            || ColumnExists(connection, tableName, "span_group"))
        {
            return;
        }

        command.CommandText = $"ALTER TABLE {tableName} RENAME COLUMN {legacyColumnName} TO span_group;";
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string BuildCohortClause(
        SqliteCommand command,
        WorkspaceTrendsRunContext context,
        IReadOnlyList<string> cohort)
    {
        var clauses = new List<string>();
        foreach (var dimension in cohort.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mapping = ResolveCohortDimension(dimension, context);
            clauses.Add($"AND COALESCE({mapping.ColumnName}, '') = COALESCE({mapping.ParameterName}, '')");
            AddNullable(command, mapping.ParameterName, mapping.Value);
        }
        return string.Join(Environment.NewLine, clauses);
    }

    private static string? FindBaselineAppVersion(
        SqliteConnection connection,
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsMetricResult metric,
        WorkspaceTrendsHistoryDefinition historyDefinition)
    {
        using var command = connection.CreateCommand();
        var cohortClause = BuildCohortClause(command, context, historyDefinition.Cohort);
        command.CommandText = $"""
            SELECT app_version
            FROM trends_metrics
            WHERE metric_key = $metric_key
              AND metric_definition_hash = $metric_definition_hash
              AND app_id = $app_id
              AND span_group = $span_group
              AND evaluation_id <> $evaluation_id
              AND app_version IS NOT NULL
              AND app_version <> $app_version
              {cohortClause}
            ORDER BY evaluated_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$metric_key", metric.MetricKey);
        command.Parameters.AddWithValue("$metric_definition_hash", metric.MetricDefinitionHash);
        command.Parameters.AddWithValue("$app_id", context.AppId);
        command.Parameters.AddWithValue("$span_group", NormalizeSpanGroup(metric.SpanGroup));
        command.Parameters.AddWithValue("$evaluation_id", context.EvaluationId);
        command.Parameters.AddWithValue("$app_version", context.AppVersion);
        return command.ExecuteScalar() as string;
    }

    private static string? FindPinnedBaselineAppVersion(
        SqliteConnection connection,
        WorkspaceTrendsRunContext context,
        WorkspaceTrendsMetricResult metric,
        WorkspaceTrendsHistoryDefinition historyDefinition)
    {
        using var command = connection.CreateCommand();
        var cohortClause = BuildCohortClause(command, context, historyDefinition.Cohort);
        command.CommandText = $"""
            SELECT baseline_app_version
            FROM trends_history
            WHERE decision_id = $decision_id
              AND definition_hash = $definition_hash
              AND metric_definition_hash = $metric_definition_hash
              AND app_id = $app_id
              AND span_group = $span_group
              AND comparison = $comparison
              AND app_version = $app_version
              AND baseline_app_version IS NOT NULL
              {cohortClause}
            ORDER BY evaluated_at_utc ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$decision_id", historyDefinition.DecisionId);
        command.Parameters.AddWithValue("$definition_hash", historyDefinition.DefinitionHash);
        command.Parameters.AddWithValue("$metric_definition_hash", metric.MetricDefinitionHash);
        command.Parameters.AddWithValue("$app_id", context.AppId);
        command.Parameters.AddWithValue("$span_group", NormalizeSpanGroup(metric.SpanGroup));
        command.Parameters.AddWithValue("$comparison", WorkspaceTrendsHistoryComparison.PreviousVersion.ToString());
        command.Parameters.AddWithValue("$app_version", context.AppVersion!);
        return command.ExecuteScalar() as string;
    }

    private static WorkspaceCohortDimension ResolveCohortDimension(
        string dimension,
        WorkspaceTrendsRunContext context)
        => dimension.Trim().ToLowerInvariant() switch
        {
            "platform" => new WorkspaceCohortDimension("platform", "$cohort_platform", context.Platform),
            "devicemodel" => new WorkspaceCohortDimension("device_model", "$cohort_device_model", context.DeviceModel),
            "operatingsystemmajor" => new WorkspaceCohortDimension("operating_system_major", "$cohort_os_major", context.OperatingSystemMajor),
            "buildconfiguration" => new WorkspaceCohortDimension("build_configuration", "$cohort_build_configuration", context.BuildConfiguration),
            "buildnumber" => new WorkspaceCohortDimension("build_number", "$cohort_build_number", context.BuildNumber),
            _ => throw new InvalidDataException($"Unsupported trends cohort dimension '{dimension}'.")
        };

    private static void AddContextParameters(SqliteCommand command, WorkspaceTrendsRunContext context)
    {
        command.Parameters.AddWithValue("$evaluation_id", context.EvaluationId);
        command.Parameters.AddWithValue("$run_id", context.RunId);
        command.Parameters.AddWithValue("$session_id", context.SessionId);
        command.Parameters.AddWithValue("$app_id", context.AppId);
        AddNullable(command, "$app_version", context.AppVersion);
        AddNullable(command, "$build_number", context.BuildNumber);
        AddNullable(command, "$platform", context.Platform);
        AddNullable(command, "$device_model", context.DeviceModel);
        AddNullable(command, "$operating_system_major", context.OperatingSystemMajor);
        AddNullable(command, "$build_configuration", context.BuildConfiguration);
        command.Parameters.AddWithValue("$evaluated_at_utc", context.EvaluatedAtUtc.ToString("O"));
    }

    private static void AddNullable(SqliteCommand command, string parameterName, object? value)
        => command.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? ReadSpanGroup(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || string.IsNullOrEmpty(reader.GetString(ordinal))
            ? null
            : reader.GetString(ordinal);

    private static string NormalizeSpanGroup(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static WorkspaceTrendsStatus ParseTrendsStatus(string value)
        => Enum.TryParse<WorkspaceTrendsStatus>(value, out var status)
            ? status
            : WorkspaceTrendsStatus.Error;

    private static WorkspaceTrendsHistoryStatus ParseHistoryStatus(string value)
        => Enum.TryParse<WorkspaceTrendsHistoryStatus>(value, out var status)
            ? status
            : WorkspaceTrendsHistoryStatus.Error;

    private static WorkspaceTrendsHistoryComparison ParseHistoryComparison(string value)
        => Enum.TryParse<WorkspaceTrendsHistoryComparison>(value, out var comparison)
            ? comparison
            : WorkspaceTrendsHistoryComparison.UnversionedReference;

    private static void EnsureProviderInitialized()
    {
        lock (providerGate)
        {
            if (providerInitialized)
            {
                return;
            }
            try
            {
                _ = SQLitePCL.raw.sqlite3_libversion();
            }
            catch
            {
                SQLitePCL.ISQLite3Provider provider = OperatingSystem.IsWindows()
                    ? new SQLitePCL.SQLite3Provider_winsqlite3()
                    : new SQLitePCL.SQLite3Provider_sqlite3();
                SQLitePCL.raw.SetProvider(provider);
                SQLitePCL.raw.FreezeProvider();
            }
            providerInitialized = true;
        }
    }

    private sealed record WorkspaceCohortDimension(
        string ColumnName,
        string ParameterName,
        string? Value);
}
