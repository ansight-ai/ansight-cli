using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed class AppGraphExplorationDatabase
    : IAppGraphExplorationDatabase
{
    private static readonly Lock providerGate = new();
    private static bool providerInitialized;
    private readonly Lock databaseGate = new();
    private readonly string databasePath;

    public AppGraphExplorationDatabase(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        databasePath = Path.Combine(
            applicationPaths.ApplicationDataPath,
            "app-graph",
            "app-graph-exploration.sqlite");
    }

    internal string DatabasePath => databasePath;

    public void Save(SimulatorAgentAppGraphLiveRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (databaseGate)
        {
            try
            {
                EnsureDatabase();
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                SaveRun(connection, transaction, run);
                ReplaceNodes(connection, transaction, run);
                ReplaceEdges(connection, transaction, run);
                ReplaceNavigationHosts(connection, transaction, run);
                ReplaceTabGroups(connection, transaction, run);
                ReplaceActions(connection, transaction, run);
                transaction.Commit();
            }
            catch (Exception exception) when (exception is SqliteException
                                               or IOException
                                               or UnauthorizedAccessException)
            {
                // Exploration must remain available even if local persistence is unavailable.
            }
        }
    }

    public AppGraphResumeState? LoadLatestIncomplete(
        string sessionId,
        string? appId,
        string graphName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        lock (databaseGate)
        {
            try
            {
                EnsureDatabase();
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                var appFilter = string.IsNullOrWhiteSpace(appId)
                    ? string.Empty
                    : "AND app_id = $app_id";
                command.CommandText = $"""
                    SELECT run_id, status
                    FROM app_graph_runs
                    WHERE session_id = $session_id
                      AND graph_name = $graph_name
                      {appFilter}
                    ORDER BY updated_utc DESC
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$session_id", sessionId.Trim());
                command.Parameters.AddWithValue("$graph_name", graphName.Trim());
                if (appFilter.Length > 0)
                {
                    command.Parameters.AddWithValue("$app_id", appId!.Trim());
                }

                string? runId = null;
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read()
                        && !string.Equals(reader.GetString(1), "succeeded", StringComparison.Ordinal))
                    {
                        runId = reader.GetString(0);
                    }
                }

                return runId is null
                    ? null
                    : new AppGraphResumeState(
                        runId,
                        ReadNodes(connection, runId),
                        ReadEdges(connection, runId),
                        ReadNavigationHosts(connection, runId),
                        ReadTabGroups(connection, runId),
                        ReadActions(connection, runId));
            }
            catch (Exception exception) when (exception is SqliteException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or FormatException)
            {
                return null;
            }
        }
    }

    private void EnsureDatabase()
    {
        EnsureProviderInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS app_graph_runs (
                run_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                app_id TEXT NULL,
                graph_name TEXT NOT NULL,
                status TEXT NOT NULL,
                message TEXT NOT NULL,
                current_destination_id TEXT NULL,
                turn INTEGER NOT NULL,
                safe_actions_observed INTEGER NOT NULL,
                actions_explored INTEGER NOT NULL,
                scroll_containers_observed INTEGER NOT NULL,
                scroll_containers_completed INTEGER NOT NULL,
                started_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                completed_utc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS app_graph_nodes (
                run_id TEXT NOT NULL,
                node_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                parent_screen TEXT NULL,
                synonyms_json TEXT NOT NULL,
                purpose TEXT NOT NULL,
                description TEXT NOT NULL,
                scroll_status TEXT NOT NULL,
                confidence REAL NOT NULL,
                first_observed_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, node_id),
                FOREIGN KEY (run_id) REFERENCES app_graph_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_graph_edges (
                run_id TEXT NOT NULL,
                edge_id TEXT NOT NULL,
                source_node_id TEXT NOT NULL,
                destination_node_id TEXT NOT NULL,
                automation_id TEXT NOT NULL,
                semantic_meaning TEXT NOT NULL,
                confidence REAL NOT NULL,
                first_observed_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, edge_id),
                FOREIGN KEY (run_id) REFERENCES app_graph_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_graph_action_candidates (
                run_id TEXT NOT NULL,
                action_id TEXT NOT NULL,
                source_node_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                automation_id TEXT NOT NULL,
                selector_json TEXT NOT NULL,
                semantic_meaning TEXT NOT NULL,
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                last_outcome TEXT NOT NULL,
                result_node_id TEXT NULL,
                first_observed_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, action_id),
                FOREIGN KEY (run_id) REFERENCES app_graph_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_graph_navigation_hosts (
                run_id TEXT NOT NULL,
                host_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                destination_id TEXT NOT NULL,
                active_child_destination_id TEXT NOT NULL,
                child_destination_ids_json TEXT NOT NULL,
                framework TEXT NOT NULL,
                technology_json TEXT NULL,
                confidence REAL NOT NULL,
                first_observed_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, host_id),
                FOREIGN KEY (run_id) REFERENCES app_graph_runs(run_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS app_graph_tab_groups (
                run_id TEXT NOT NULL,
                tab_group_id TEXT NOT NULL,
                parent_destination_id TEXT NOT NULL,
                selected_destination_id TEXT NOT NULL,
                tab_destination_ids_json TEXT NOT NULL,
                technology_json TEXT NULL,
                confidence REAL NOT NULL,
                first_observed_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, tab_group_id),
                FOREIGN KEY (run_id) REFERENCES app_graph_runs(run_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_app_graph_action_frontier
                ON app_graph_action_candidates(run_id, status, source_node_id);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(
            connection,
            "app_graph_navigation_hosts",
            "technology_json",
            "TEXT NULL");
        EnsureColumn(
            connection,
            "app_graph_tab_groups",
            "technology_json",
            "TEXT NULL");
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        reader.Close();

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void SaveRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SimulatorAgentAppGraphLiveRun run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_graph_runs (
                run_id, session_id, app_id, graph_name, status, message,
                current_destination_id, turn, safe_actions_observed, actions_explored,
                scroll_containers_observed, scroll_containers_completed,
                started_utc, updated_utc, completed_utc)
            VALUES (
                $run_id, $session_id, $app_id, $graph_name, $status, $message,
                $current_destination_id, $turn, $safe_actions_observed, $actions_explored,
                $scroll_containers_observed, $scroll_containers_completed,
                $started_utc, $updated_utc, $completed_utc)
            ON CONFLICT(run_id) DO UPDATE SET
                session_id = excluded.session_id,
                app_id = excluded.app_id,
                graph_name = excluded.graph_name,
                status = excluded.status,
                message = excluded.message,
                current_destination_id = excluded.current_destination_id,
                turn = excluded.turn,
                safe_actions_observed = excluded.safe_actions_observed,
                actions_explored = excluded.actions_explored,
                scroll_containers_observed = excluded.scroll_containers_observed,
                scroll_containers_completed = excluded.scroll_containers_completed,
                updated_utc = excluded.updated_utc,
                completed_utc = excluded.completed_utc;
            """;
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$session_id", run.SessionId);
        command.Parameters.AddWithValue("$app_id", (object?)run.AppId ?? DBNull.Value);
        command.Parameters.AddWithValue("$graph_name", run.GraphName);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$message", run.Message);
        command.Parameters.AddWithValue(
            "$current_destination_id",
            (object?)run.CurrentDestinationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turn", run.Turn);
        command.Parameters.AddWithValue("$safe_actions_observed", run.Coverage.SafeActionsObserved);
        command.Parameters.AddWithValue("$actions_explored", run.Coverage.ActionsExplored);
        command.Parameters.AddWithValue(
            "$scroll_containers_observed",
            run.Coverage.ScrollContainersObserved);
        command.Parameters.AddWithValue(
            "$scroll_containers_completed",
            run.Coverage.ScrollContainersCompleted);
        command.Parameters.AddWithValue("$started_utc", run.StartedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_utc", run.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$completed_utc",
            run.CompletedUtc.HasValue ? run.CompletedUtc.Value.ToString("O") : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void ReplaceNodes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SimulatorAgentAppGraphLiveRun run)
    {
        DeleteRunRows(connection, transaction, "app_graph_nodes", run.RunId);
        foreach (var node in run.Nodes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_graph_nodes (
                    run_id, node_id, kind, name, parent_screen, synonyms_json, purpose,
                    description, scroll_status, confidence, first_observed_utc, updated_utc)
                VALUES (
                    $run_id, $node_id, $kind, $name, $parent_screen, $synonyms_json, $purpose,
                    $description, $scroll_status, $confidence, $first_observed_utc, $updated_utc);
                """;
            command.Parameters.AddWithValue("$run_id", run.RunId);
            command.Parameters.AddWithValue("$node_id", node.Id);
            command.Parameters.AddWithValue("$kind", node.Kind);
            command.Parameters.AddWithValue("$name", node.Name);
            command.Parameters.AddWithValue("$parent_screen", (object?)node.ParentScreen ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$synonyms_json",
                new JsonArray(node.Synonyms.Select(static value => (JsonNode?)value).ToArray())
                    .ToJsonString());
            command.Parameters.AddWithValue("$purpose", node.Purpose);
            command.Parameters.AddWithValue("$description", node.Description);
            command.Parameters.AddWithValue("$scroll_status", node.ScrollStatus);
            command.Parameters.AddWithValue("$confidence", (double)node.Confidence);
            command.Parameters.AddWithValue("$first_observed_utc", node.FirstObservedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated_utc", node.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static void ReplaceEdges(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SimulatorAgentAppGraphLiveRun run)
    {
        DeleteRunRows(connection, transaction, "app_graph_edges", run.RunId);
        foreach (var edge in run.Edges)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_graph_edges (
                    run_id, edge_id, source_node_id, destination_node_id, automation_id,
                    semantic_meaning, confidence, first_observed_utc, updated_utc)
                VALUES (
                    $run_id, $edge_id, $source_node_id, $destination_node_id, $automation_id,
                    $semantic_meaning, $confidence, $first_observed_utc, $updated_utc);
                """;
            command.Parameters.AddWithValue("$run_id", run.RunId);
            command.Parameters.AddWithValue("$edge_id", edge.Id);
            command.Parameters.AddWithValue("$source_node_id", edge.From);
            command.Parameters.AddWithValue("$destination_node_id", edge.To);
            command.Parameters.AddWithValue("$automation_id", edge.AutomationId);
            command.Parameters.AddWithValue("$semantic_meaning", edge.SemanticMeaning);
            command.Parameters.AddWithValue("$confidence", (double)edge.Confidence);
            command.Parameters.AddWithValue("$first_observed_utc", edge.FirstObservedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated_utc", edge.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static void ReplaceActions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SimulatorAgentAppGraphLiveRun run)
    {
        DeleteRunRows(connection, transaction, "app_graph_action_candidates", run.RunId);
        foreach (var action in run.Frontier)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_graph_action_candidates (
                    run_id, action_id, source_node_id, tool_name, automation_id, selector_json,
                    semantic_meaning, status, attempt_count, last_outcome, result_node_id,
                    first_observed_utc, updated_utc)
                VALUES (
                    $run_id, $action_id, $source_node_id, $tool_name, $automation_id, $selector_json,
                    $semantic_meaning, $status, $attempt_count, $last_outcome, $result_node_id,
                    $first_observed_utc, $updated_utc);
                """;
            command.Parameters.AddWithValue("$run_id", run.RunId);
            command.Parameters.AddWithValue("$action_id", action.Id);
            command.Parameters.AddWithValue("$source_node_id", action.DestinationId);
            command.Parameters.AddWithValue("$tool_name", action.ToolName);
            command.Parameters.AddWithValue("$automation_id", action.AutomationId);
            command.Parameters.AddWithValue("$selector_json", action.Selector.ToJsonString());
            command.Parameters.AddWithValue("$semantic_meaning", action.SemanticMeaning);
            command.Parameters.AddWithValue("$status", action.Status);
            command.Parameters.AddWithValue("$attempt_count", action.AttemptCount);
            command.Parameters.AddWithValue("$last_outcome", action.LastOutcome);
            command.Parameters.AddWithValue(
                "$result_node_id",
                (object?)action.ResultDestinationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$first_observed_utc", action.FirstObservedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated_utc", action.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static void ReplaceNavigationHosts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SimulatorAgentAppGraphLiveRun run)
    {
        DeleteRunRows(connection, transaction, "app_graph_navigation_hosts", run.RunId);
        foreach (var navigationHost in run.NavigationHosts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_graph_navigation_hosts (
                    run_id, host_id, kind, name, destination_id, active_child_destination_id,
                    child_destination_ids_json, framework, technology_json, confidence, first_observed_utc, updated_utc)
                VALUES (
                    $run_id, $host_id, $kind, $name, $destination_id, $active_child_destination_id,
                    $child_destination_ids_json, $framework, $technology_json, $confidence, $first_observed_utc, $updated_utc);
                """;
            command.Parameters.AddWithValue("$run_id", run.RunId);
            command.Parameters.AddWithValue("$host_id", navigationHost.Id);
            command.Parameters.AddWithValue("$kind", navigationHost.Kind);
            command.Parameters.AddWithValue("$name", navigationHost.Name);
            command.Parameters.AddWithValue("$destination_id", navigationHost.DestinationId);
            command.Parameters.AddWithValue(
                "$active_child_destination_id",
                navigationHost.ActiveChildDestinationId);
            command.Parameters.AddWithValue(
                "$child_destination_ids_json",
                SerializeStringArray(navigationHost.ChildDestinationIds));
            command.Parameters.AddWithValue("$framework", navigationHost.Framework);
            command.Parameters.AddWithValue(
                "$technology_json",
                navigationHost.Technology is null
                    ? DBNull.Value
                    : AppGraphNavigationTechnologyCatalog.ToJson(navigationHost.Technology).ToJsonString());
            command.Parameters.AddWithValue("$confidence", (double)navigationHost.Confidence);
            command.Parameters.AddWithValue("$first_observed_utc", navigationHost.FirstObservedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated_utc", navigationHost.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static void ReplaceTabGroups(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SimulatorAgentAppGraphLiveRun run)
    {
        DeleteRunRows(connection, transaction, "app_graph_tab_groups", run.RunId);
        foreach (var tabGroup in run.TabGroups)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_graph_tab_groups (
                    run_id, tab_group_id, parent_destination_id, selected_destination_id,
                    tab_destination_ids_json, technology_json, confidence, first_observed_utc, updated_utc)
                VALUES (
                    $run_id, $tab_group_id, $parent_destination_id, $selected_destination_id,
                    $tab_destination_ids_json, $technology_json, $confidence, $first_observed_utc, $updated_utc);
                """;
            command.Parameters.AddWithValue("$run_id", run.RunId);
            command.Parameters.AddWithValue("$tab_group_id", tabGroup.Id);
            command.Parameters.AddWithValue("$parent_destination_id", tabGroup.ParentDestinationId);
            command.Parameters.AddWithValue("$selected_destination_id", tabGroup.SelectedDestinationId);
            command.Parameters.AddWithValue(
                "$tab_destination_ids_json",
                SerializeStringArray(tabGroup.TabDestinationIds));
            command.Parameters.AddWithValue(
                "$technology_json",
                tabGroup.Technology is null
                    ? DBNull.Value
                    : AppGraphNavigationTechnologyCatalog.ToJson(tabGroup.Technology).ToJsonString());
            command.Parameters.AddWithValue("$confidence", (double)tabGroup.Confidence);
            command.Parameters.AddWithValue("$first_observed_utc", tabGroup.FirstObservedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated_utc", tabGroup.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteRunRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {tableName} WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SimulatorAgentAppGraphLiveNode> ReadNodes(
        SqliteConnection connection,
        string runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, kind, name, parent_screen, synonyms_json, purpose, description,
                   scroll_status, confidence, first_observed_utc, updated_utc
            FROM app_graph_nodes
            WHERE run_id = $run_id
            ORDER BY first_observed_utc, node_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var nodes = new List<SimulatorAgentAppGraphLiveNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var synonyms = JsonNode.Parse(reader.GetString(4)) as JsonArray;
            nodes.Add(new SimulatorAgentAppGraphLiveNode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                synonyms?.OfType<JsonValue>()
                    .Select(static value => value.GetValue<string>())
                    .ToArray() ?? [],
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                Convert.ToDecimal(reader.GetDouble(8)),
                DateTimeOffset.Parse(reader.GetString(9)),
                DateTimeOffset.Parse(reader.GetString(10))));
        }
        return nodes;
    }

    private static IReadOnlyList<SimulatorAgentAppGraphLiveEdge> ReadEdges(
        SqliteConnection connection,
        string runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT edge_id, source_node_id, destination_node_id, automation_id,
                   semantic_meaning, confidence, first_observed_utc, updated_utc
            FROM app_graph_edges
            WHERE run_id = $run_id
            ORDER BY first_observed_utc, edge_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var edges = new List<SimulatorAgentAppGraphLiveEdge>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new SimulatorAgentAppGraphLiveEdge(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                Convert.ToDecimal(reader.GetDouble(5)),
                DateTimeOffset.Parse(reader.GetString(6)),
                DateTimeOffset.Parse(reader.GetString(7))));
        }
        return edges;
    }

    private static IReadOnlyList<SimulatorAgentAppGraphLiveNavigationHost> ReadNavigationHosts(
        SqliteConnection connection,
        string runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT host_id, kind, name, destination_id, active_child_destination_id,
                   child_destination_ids_json, framework, technology_json, confidence, first_observed_utc, updated_utc
            FROM app_graph_navigation_hosts
            WHERE run_id = $run_id
            ORDER BY first_observed_utc, host_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var navigationHosts = new List<SimulatorAgentAppGraphLiveNavigationHost>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            navigationHosts.Add(new SimulatorAgentAppGraphLiveNavigationHost(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ReadStringArray(reader.GetString(5)),
                reader.GetString(6),
                Convert.ToDecimal(reader.GetDouble(8)),
                DateTimeOffset.Parse(reader.GetString(9)),
                DateTimeOffset.Parse(reader.GetString(10)),
                ReadTechnology(
                    reader,
                    7,
                    AppGraphNavigationTechnologyCatalog.NavigationHostScope)));
        }
        return navigationHosts;
    }

    private static IReadOnlyList<SimulatorAgentAppGraphLiveTabGroup> ReadTabGroups(
        SqliteConnection connection,
        string runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tab_group_id, parent_destination_id, selected_destination_id,
                   tab_destination_ids_json, technology_json, confidence, first_observed_utc, updated_utc
            FROM app_graph_tab_groups
            WHERE run_id = $run_id
            ORDER BY first_observed_utc, tab_group_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var tabGroups = new List<SimulatorAgentAppGraphLiveTabGroup>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tabGroups.Add(new SimulatorAgentAppGraphLiveTabGroup(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ReadStringArray(reader.GetString(3)),
                Convert.ToDecimal(reader.GetDouble(5)),
                DateTimeOffset.Parse(reader.GetString(6)),
                DateTimeOffset.Parse(reader.GetString(7)),
                ReadTechnology(
                    reader,
                    4,
                    AppGraphNavigationTechnologyCatalog.TabGroupScope)));
        }
        return tabGroups;
    }

    private static IReadOnlyList<SimulatorAgentAppGraphLiveAction> ReadActions(
        SqliteConnection connection,
        string runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT action_id, source_node_id, tool_name, automation_id, selector_json,
                   semantic_meaning, status, attempt_count, last_outcome, result_node_id,
                   first_observed_utc, updated_utc
            FROM app_graph_action_candidates
            WHERE run_id = $run_id
            ORDER BY first_observed_utc, action_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var actions = new List<SimulatorAgentAppGraphLiveAction>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            actions.Add(new SimulatorAgentAppGraphLiveAction(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                JsonNode.Parse(reader.GetString(4))?.AsObject() ?? new JsonObject(),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10)),
                DateTimeOffset.Parse(reader.GetString(11))));
        }
        return actions;
    }

    private static AppGraphNavigationTechnologyDescriptor? ReadTechnology(
        SqliteDataReader reader,
        int ordinal,
        string scope)
    {
        if (reader.IsDBNull(ordinal)
            || JsonNode.Parse(reader.GetString(ordinal)) is not JsonObject value
            || !AppGraphNavigationTechnologyCatalog.TryReadAndValidate(
                value,
                scope,
                out var technology,
                out _,
                out _))
        {
            return null;
        }

        return technology;
    }

    private static string SerializeStringArray(IReadOnlyList<string> values)
        => new JsonArray(values.Select(static value => (JsonNode?)value).ToArray())
            .ToJsonString();

    private static IReadOnlyList<string> ReadStringArray(string json)
        => JsonNode.Parse(json) is JsonArray values
            ? values.OfType<JsonValue>()
                .Select(static value => value.GetValue<string>())
                .ToArray()
            : [];

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
}
