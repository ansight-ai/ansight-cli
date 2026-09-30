using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphSyncLink(
    Guid LocalGraphId,
    Guid RemoteGraphId,
    Guid RemoteTeamId,
    Guid? LocalVersionId,
    Guid? RemoteVersionId,
    DateTimeOffset SyncedAtUtc);

public sealed record LocalAppGraphImportRequest(
    string AppId,
    string AppName,
    CloudAppGraphDetail Detail,
    Guid RemoteTeamId);

public sealed record LocalAppGraphIdentity(
    Guid GraphId,
    string AppId,
    string AppName);

public sealed partial class LocalAppGraphService
{
    public static readonly Guid LocalTeamId = Guid.Parse("6c6f6361-6c2d-4d6f-9e64-650000000001");

    private static readonly Lock providerGate = new();
    private static bool providerInitialized;
    private readonly Lock databaseGate = new();
    private readonly string databasePath;
    private readonly ProductAnalytics analytics;

    public LocalAppGraphService(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        analytics = ProductAnalytics.For(applicationPaths);
        databasePath = Path.Combine(
            applicationPaths.ApplicationDataPath,
            "app-graph",
            "app-graphs.sqlite");
    }

    public CloudRegisteredApp ResolveApp(string appId, string? appName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        return new CloudRegisteredApp(
            CreateStableAppId(normalizedAppId),
            LocalTeamId,
            normalizedAppId,
            string.IsNullOrWhiteSpace(appName) ? normalizedAppId : appName.Trim(),
            null,
            DateTimeOffset.MinValue,
            DateTimeOffset.UtcNow);
    }

    public CloudAppGraphQueryResult List(string? appId = null, string? query = null)
    {
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, app_id, name, intent, status, version, current_version_id,
                       published_version_id, observation_count, successful_run_count,
                       failed_run_count, updated_utc
                FROM local_app_graphs
                WHERE ($app_id = '' OR app_id = $app_id)
                ORDER BY updated_utc DESC;
                """;
            command.Parameters.AddWithValue("$app_id", appId?.Trim() ?? string.Empty);
            var normalizedQuery = query?.Trim();
            var graphs = new List<CloudAppGraphSummary>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var graph = ReadGraph(reader);
                if (string.IsNullOrWhiteSpace(normalizedQuery)
                    || graph.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || graph.Intent.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || graph.Id.ToString("D").Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    graphs.Add(graph);
                }
            }
            return CloudAppGraphQueryResult.Success(graphs);
        }
    }

    public CloudAppGraphDetailResult Get(Guid graphId, bool publishedOnly = false)
    {
        if (graphId == Guid.Empty)
        {
            return CloudAppGraphDetailResult.Failure("A non-empty local App Graph ID is required.");
        }

        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            var graph = ReadGraph(connection, graphId);
            if (graph is null)
            {
                return CloudAppGraphDetailResult.Failure($"Local App Graph '{graphId:D}' was not found.");
            }
            var versionId = publishedOnly
                ? graph.PublishedVersionId
                : graph.CurrentVersionId ?? graph.PublishedVersionId;
            if (!versionId.HasValue)
            {
                return CloudAppGraphDetailResult.Failure(
                    publishedOnly
                        ? $"Local App Graph '{graph.Name}' does not have a published version."
                        : $"Local App Graph '{graph.Name}' does not have a version.");
            }
            var version = ReadVersion(connection, versionId.Value);
            if (version is null)
            {
                return CloudAppGraphDetailResult.Failure(
                    $"Local App Graph version '{versionId.Value:D}' was not found.");
            }
            return CloudAppGraphDetailResult.Success(
                new CloudAppGraphDetail(graph, version, ReadBindings(connection, version.Id)));
        }
    }

    public CloudAppGraphDetailResult Find(string appId, string graphName, bool publishedOnly = false)
    {
        var graph = List(appId, graphName).Graphs.FirstOrDefault(candidate =>
            candidate.Name.Equals(graphName.Trim(), StringComparison.OrdinalIgnoreCase));
        return graph is null
            ? CloudAppGraphDetailResult.Failure(
                $"Local App Graph '{graphName}' was not found for app '{appId}'.")
            : Get(graph.Id, publishedOnly);
    }

    public CloudAppGraphCreateResult Create(
        string appId,
        string appName,
        string name,
        string intent,
        JsonObject definition,
        JsonObject? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsDefinitionValid(definition))
        {
            return CloudAppGraphCreateResult.Failure("A valid App Graph definition is required.");
        }

        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = FindGraphId(connection, transaction, appId, name);
            if (existing.HasValue)
            {
                return CloudAppGraphCreateResult.Failure(
                    $"Local app '{appId}' already has an App Graph named '{name.Trim()}'.");
            }
            var graphId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            InsertGraph(
                connection,
                transaction,
                graphId,
                appId.Trim(),
                string.IsNullOrWhiteSpace(appName) ? appId.Trim() : appName.Trim(),
                name.Trim(),
                intent?.Trim() ?? string.Empty,
                "draft",
                1,
                versionId,
                publishedVersionId: null,
                observationCount: 1,
                successfulRunCount: 0,
                failedRunCount: 0,
                now);
            InsertVersion(connection, transaction, versionId, graphId, 1, "draft", definition, "Created from local exploration.", now);
            ReplaceBindings(connection, transaction, versionId, evidence, bindings: null);
            transaction.Commit();
            var detailResult = Get(graphId);
            return detailResult.Detail is null
                ? CloudAppGraphCreateResult.Failure(detailResult.Message)
                : CloudAppGraphCreateResult.Success(
                    detailResult.Detail,
                    $"Created local draft App Graph '{name.Trim()}'.");
        }
    }

    public CloudAppGraphObservationOperationResult SaveObservation(
        CloudAppGraphObservationCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsDefinitionValid(request.CandidateDefinition))
        {
            return CloudAppGraphObservationOperationResult.Failure(
                "A valid candidate App Graph definition is required.");
        }

        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var graph = ReadGraph(connection, request.AppGraphId, transaction);
            if (graph is null)
            {
                return CloudAppGraphObservationOperationResult.Failure(
                    $"Local App Graph '{request.AppGraphId:D}' was not found.");
            }
            var observationId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var createsVersion = request.MergedIntoVersionId != graph.CurrentVersionId;
            var versionNumber = createsVersion ? graph.Version + 1 : graph.Version;
            var now = DateTimeOffset.UtcNow;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO local_app_graph_observations (
                        id, graph_id, source_session_id, source_app_id, audit_run_id,
                        candidate_definition_json, evidence_json, confidence, notes, created_utc)
                    VALUES ($id, $graph_id, $session_id, $app_id, $audit_id,
                            $definition, $evidence, $confidence, $notes, $created_utc);
                    """;
                command.Parameters.AddWithValue("$id", observationId.ToString("D"));
                command.Parameters.AddWithValue("$graph_id", graph.Id.ToString("D"));
                command.Parameters.AddWithValue("$session_id", request.SourceLocalSessionId.Trim());
                command.Parameters.AddWithValue("$app_id", request.SourceAppId.Trim());
                command.Parameters.AddWithValue("$audit_id", request.ExplorationAuditRunId.Trim());
                command.Parameters.AddWithValue("$definition", request.CandidateDefinition.ToJsonString());
                command.Parameters.AddWithValue("$evidence", request.Evidence.ToJsonString());
                command.Parameters.AddWithValue("$confidence", Math.Clamp(request.Confidence, 0m, 1m));
                command.Parameters.AddWithValue("$notes", (object?)request.Notes?.Trim() ?? DBNull.Value);
                command.Parameters.AddWithValue("$created_utc", now.ToString("O"));
                command.ExecuteNonQuery();
            }
            if (createsVersion)
            {
                InsertVersion(
                    connection,
                    transaction,
                    versionId,
                    graph.Id,
                    versionNumber,
                    "draft",
                    request.CandidateDefinition,
                    "Updated from local exploration.",
                    now);
                ReplaceBindings(connection, transaction, versionId, request.Evidence, bindings: null);
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = createsVersion
                    ? """
                      UPDATE local_app_graphs
                      SET status = 'draft', version = $version, current_version_id = $version_id,
                          observation_count = observation_count + 1, updated_utc = $updated_utc
                      WHERE id = $graph_id;
                      """
                    : """
                      UPDATE local_app_graphs
                      SET observation_count = observation_count + 1, updated_utc = $updated_utc
                      WHERE id = $graph_id;
                      """;
                command.Parameters.AddWithValue("$version", versionNumber);
                command.Parameters.AddWithValue("$version_id", versionId.ToString("D"));
                command.Parameters.AddWithValue("$updated_utc", now.ToString("O"));
                command.Parameters.AddWithValue("$graph_id", graph.Id.ToString("D"));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return CloudAppGraphObservationOperationResult.Success(
                observationId,
                $"Saved local App Graph '{graph.Name}' as version {versionNumber}.");
        }
    }

    public CloudAppGraphRunOperationResult CreateRun(CloudAppGraphRunCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            var runId = Guid.NewGuid();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO local_app_graph_execution_runs (
                    id, graph_id, version_id, requested_intent, parameters_json,
                    target_session_id, status, started_utc)
                VALUES ($id, $graph_id, $version_id, $intent, $parameters,
                        $session_id, 'running', $started_utc);
                """;
            command.Parameters.AddWithValue("$id", runId.ToString("D"));
            command.Parameters.AddWithValue("$graph_id", request.AppGraphId.ToString("D"));
            command.Parameters.AddWithValue("$version_id", request.AppGraphVersionId.ToString("D"));
            command.Parameters.AddWithValue("$intent", request.RequestedIntent.Trim());
            command.Parameters.AddWithValue("$parameters", request.Parameters.ToJsonString());
            command.Parameters.AddWithValue("$session_id", request.TargetSessionId.Trim());
            command.Parameters.AddWithValue("$started_utc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            return CloudAppGraphRunOperationResult.Success(runId, "Local App Graph run started.");
        }
    }

    public CloudAppGraphRunOperationResult WriteRunStep(CloudAppGraphRunStepWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO local_app_graph_execution_steps (
                    run_id, step_index, edge_id, binding_id, status, request_json,
                    response_json, evidence_json, error_message, started_utc, completed_utc)
                VALUES ($run_id, $step_index, $edge_id, $binding_id, $status, $request,
                        $response, $evidence, $error, $started, $completed);
                """;
            command.Parameters.AddWithValue("$run_id", request.RunId.ToString("D"));
            command.Parameters.AddWithValue("$step_index", request.StepIndex);
            command.Parameters.AddWithValue("$edge_id", request.EdgeId);
            command.Parameters.AddWithValue("$binding_id", request.BindingId.HasValue ? request.BindingId.Value.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$status", request.Status);
            command.Parameters.AddWithValue("$request", request.Request.ToJsonString());
            command.Parameters.AddWithValue("$response", request.Response.ToJsonString());
            command.Parameters.AddWithValue("$evidence", request.Evidence.ToJsonString());
            command.Parameters.AddWithValue("$error", (object?)request.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$started", request.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$completed", request.CompletedAt.ToString("O"));
            command.ExecuteNonQuery();
            return CloudAppGraphRunOperationResult.Success(request.RunId, $"Recorded local App Graph run step {request.StepIndex}.");
        }
    }

    public CloudAppGraphRunOperationResult CompleteRun(Guid runId, string status, string? message)
    {
        if (runId == Guid.Empty || status is not ("succeeded" or "failed" or "cancelled"))
        {
            return CloudAppGraphRunOperationResult.Failure("A run ID and terminal status are required.", runId);
        }
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            Guid graphId;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT graph_id FROM local_app_graph_execution_runs WHERE id = $id;";
                read.Parameters.AddWithValue("$id", runId.ToString("D"));
                var value = read.ExecuteScalar() as string;
                if (!Guid.TryParse(value, out graphId))
                {
                    return CloudAppGraphRunOperationResult.Failure($"Local App Graph run '{runId:D}' was not found.", runId);
                }
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE local_app_graph_execution_runs
                    SET status = $status, result_message = $message, completed_utc = $completed
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$status", status);
                update.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
                update.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$id", runId.ToString("D"));
                update.ExecuteNonQuery();
            }
            using (var updateGraph = connection.CreateCommand())
            {
                updateGraph.Transaction = transaction;
                updateGraph.CommandText = status == "succeeded"
                    ? "UPDATE local_app_graphs SET successful_run_count = successful_run_count + 1, updated_utc = $updated WHERE id = $id;"
                    : status == "failed"
                        ? "UPDATE local_app_graphs SET failed_run_count = failed_run_count + 1, updated_utc = $updated WHERE id = $id;"
                        : "UPDATE local_app_graphs SET updated_utc = $updated WHERE id = $id;";
                updateGraph.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                updateGraph.Parameters.AddWithValue("$id", graphId.ToString("D"));
                updateGraph.ExecuteNonQuery();
            }
            transaction.Commit();
            return CloudAppGraphRunOperationResult.Success(runId, message ?? $"Local App Graph run {status}.");
        }
    }

    public CloudAppGraphDetailResult Import(LocalAppGraphImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var remote = request.Detail;
            var linkedLocalId = FindLocalGraphIdForRemote(connection, transaction, remote.Graph.Id, request.RemoteTeamId);
            var matchingLocalId = linkedLocalId.HasValue
                ? null
                : FindGraphId(connection, transaction, request.AppId, remote.Graph.Name);
            var localGraphId = linkedLocalId ?? matchingLocalId ?? remote.Graph.Id;
            var existing = ReadGraph(connection, localGraphId, transaction);
            var existingLink = existing is null
                ? null
                : ReadSyncLink(connection, transaction, localGraphId);
            if (!linkedLocalId.HasValue
                && existingLink is not null
                && (existingLink.RemoteGraphId != remote.Graph.Id
                    || existingLink.RemoteTeamId != request.RemoteTeamId))
            {
                return CloudAppGraphDetailResult.Failure(
                    $"Local App Graph '{remote.Graph.Name}' is already linked to hosted graph "
                    + $"'{existingLink.RemoteGraphId:D}' in organisation '{existingLink.RemoteTeamId:D}'.");
            }
            if (existing is not null && existingLink?.RemoteVersionId == remote.Version.Id)
            {
                UpsertSyncLink(
                    connection,
                    transaction,
                    localGraphId,
                    remote.Graph.Id,
                    request.RemoteTeamId,
                    existingLink.LocalVersionId,
                    remote.Version.Id,
                    DateTimeOffset.UtcNow);
                transaction.Commit();
                return Get(localGraphId);
            }
            var versionNumber = existing is null ? 1 : existing.Version + 1;
            var localVersionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                InsertGraph(
                    connection,
                    transaction,
                    localGraphId,
                    request.AppId.Trim(),
                    string.IsNullOrWhiteSpace(request.AppName) ? request.AppId.Trim() : request.AppName.Trim(),
                    remote.Graph.Name,
                    remote.Graph.Intent,
                    remote.Version.Status,
                    versionNumber,
                    localVersionId,
                    remote.Version.Status == "published" ? localVersionId : null,
                    remote.Graph.ObservationCount,
                    remote.Graph.SuccessfulRunCount,
                    remote.Graph.FailedRunCount,
                    now);
            }
            else
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE local_app_graphs
                    SET app_id = $app_id, app_name = $app_name, name = $name, intent = $intent,
                        status = $status, version = $version, current_version_id = $version_id,
                        published_version_id = $published_version_id, updated_utc = $updated
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$app_id", request.AppId.Trim());
                update.Parameters.AddWithValue("$app_name", request.AppName.Trim());
                update.Parameters.AddWithValue("$name", remote.Graph.Name);
                update.Parameters.AddWithValue("$intent", remote.Graph.Intent);
                update.Parameters.AddWithValue("$status", remote.Version.Status);
                update.Parameters.AddWithValue("$version", versionNumber);
                update.Parameters.AddWithValue("$version_id", localVersionId.ToString("D"));
                update.Parameters.AddWithValue("$published_version_id", remote.Version.Status == "published" ? localVersionId.ToString("D") : DBNull.Value);
                update.Parameters.AddWithValue("$updated", now.ToString("O"));
                update.Parameters.AddWithValue("$id", localGraphId.ToString("D"));
                update.ExecuteNonQuery();
            }
            InsertVersion(
                connection,
                transaction,
                localVersionId,
                localGraphId,
                versionNumber,
                remote.Version.Status,
                remote.Version.Definition,
                $"Pulled from hosted App Graph {remote.Graph.Id:D} version {remote.Version.VersionNumber}.",
                now);
            ReplaceBindings(connection, transaction, localVersionId, evidence: null, remote.Bindings);
            UpsertSyncLink(
                connection,
                transaction,
                localGraphId,
                remote.Graph.Id,
                request.RemoteTeamId,
                localVersionId,
                remote.Version.Id,
                now);
            transaction.Commit();
            return Get(localGraphId);
        }
    }

    public LocalAppGraphSyncLink? GetSyncLink(Guid localGraphId)
    {
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT local_graph_id, remote_graph_id, remote_team_id, local_version_id,
                       remote_version_id, synced_utc
                FROM local_app_graph_sync_links WHERE local_graph_id = $id;
                """;
            command.Parameters.AddWithValue("$id", localGraphId.ToString("D"));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSyncLink(reader) : null;
        }
    }

    public LocalAppGraphIdentity? GetIdentity(Guid localGraphId)
    {
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT app_id, app_name FROM local_app_graphs WHERE id = $id;";
            command.Parameters.AddWithValue("$id", localGraphId.ToString("D"));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new LocalAppGraphIdentity(localGraphId, reader.GetString(0), reader.GetString(1))
                : null;
        }
    }

    public void SetSyncLink(
        Guid localGraphId,
        Guid remoteGraphId,
        Guid remoteTeamId,
        Guid? localVersionId,
        Guid? remoteVersionId)
    {
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            UpsertSyncLink(
                connection,
                transaction,
                localGraphId,
                remoteGraphId,
                remoteTeamId,
                localVersionId,
                remoteVersionId,
                DateTimeOffset.UtcNow);
            transaction.Commit();
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

            CREATE TABLE IF NOT EXISTS local_app_graphs (
                id TEXT PRIMARY KEY,
                app_id TEXT NOT NULL,
                app_name TEXT NOT NULL,
                name TEXT NOT NULL COLLATE NOCASE,
                intent TEXT NOT NULL,
                status TEXT NOT NULL,
                version INTEGER NOT NULL,
                current_version_id TEXT NULL,
                published_version_id TEXT NULL,
                observation_count INTEGER NOT NULL,
                successful_run_count INTEGER NOT NULL,
                failed_run_count INTEGER NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE(app_id, name)
            );
            CREATE TABLE IF NOT EXISTS local_app_graph_versions (
                id TEXT PRIMARY KEY,
                graph_id TEXT NOT NULL,
                version_number INTEGER NOT NULL,
                status TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                merge_summary TEXT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(graph_id, version_number),
                FOREIGN KEY(graph_id) REFERENCES local_app_graphs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS local_app_graph_bindings (
                id TEXT PRIMARY KEY,
                version_id TEXT NOT NULL,
                edge_id TEXT NOT NULL,
                priority INTEGER NOT NULL,
                mechanism TEXT NOT NULL,
                configuration_json TEXT NOT NULL,
                preconditions_json TEXT NOT NULL,
                postconditions_json TEXT NOT NULL,
                confidence REAL NOT NULL,
                UNIQUE(version_id, edge_id, priority),
                FOREIGN KEY(version_id) REFERENCES local_app_graph_versions(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS local_app_graph_observations (
                id TEXT PRIMARY KEY,
                graph_id TEXT NOT NULL,
                source_session_id TEXT NOT NULL,
                source_app_id TEXT NOT NULL,
                audit_run_id TEXT NOT NULL,
                source_kind TEXT NOT NULL DEFAULT 'agent_exploration',
                candidate_definition_json TEXT NOT NULL,
                evidence_json TEXT NOT NULL,
                confidence REAL NOT NULL,
                notes TEXT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY(graph_id) REFERENCES local_app_graphs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS local_app_graph_execution_runs (
                id TEXT PRIMARY KEY,
                graph_id TEXT NOT NULL,
                version_id TEXT NOT NULL,
                requested_intent TEXT NOT NULL,
                parameters_json TEXT NOT NULL,
                target_session_id TEXT NOT NULL,
                status TEXT NOT NULL,
                result_message TEXT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                FOREIGN KEY(graph_id) REFERENCES local_app_graphs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS local_app_graph_execution_steps (
                run_id TEXT NOT NULL,
                step_index INTEGER NOT NULL,
                edge_id TEXT NOT NULL,
                binding_id TEXT NULL,
                status TEXT NOT NULL,
                request_json TEXT NOT NULL,
                response_json TEXT NOT NULL,
                evidence_json TEXT NOT NULL,
                error_message TEXT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NOT NULL,
                PRIMARY KEY(run_id, step_index),
                FOREIGN KEY(run_id) REFERENCES local_app_graph_execution_runs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS local_app_graph_sync_links (
                local_graph_id TEXT PRIMARY KEY,
                remote_graph_id TEXT NOT NULL,
                remote_team_id TEXT NOT NULL,
                local_version_id TEXT NULL,
                remote_version_id TEXT NULL,
                synced_utc TEXT NOT NULL,
                UNIQUE(remote_graph_id, remote_team_id),
                FOREIGN KEY(local_graph_id) REFERENCES local_app_graphs(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS local_app_graph_recordings (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                app_id TEXT NOT NULL,
                app_name TEXT NOT NULL,
                graph_id TEXT NULL,
                base_version_id TEXT NULL,
                committed_version_id TEXT NULL,
                graph_name TEXT NOT NULL,
                intent TEXT NOT NULL,
                status TEXT NOT NULL,
                current_destination_id TEXT NULL,
                definition_json TEXT NOT NULL,
                evidence_json TEXT NOT NULL,
                pending_transition_json TEXT NULL,
                started_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                stopped_utc TEXT NULL,
                FOREIGN KEY(graph_id) REFERENCES local_app_graphs(id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS ix_local_app_graph_recordings_session_updated
                ON local_app_graph_recordings(session_id, updated_utc DESC);
            """;
        command.ExecuteNonQuery();
        EnsureSyncLinkLocalVersionColumn(connection);
        EnsureObservationSourceKindColumn(connection);
    }

    private static void EnsureObservationSourceKindColumn(SqliteConnection connection)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(local_app_graph_observations);";
        using var reader = inspect.ExecuteReader();
        var hasColumn = false;
        while (reader.Read())
        {
            if (reader.GetString(1).Equals("source_kind", StringComparison.OrdinalIgnoreCase))
            {
                hasColumn = true;
                break;
            }
        }
        reader.Close();
        if (hasColumn)
        {
            return;
        }

        using var migrate = connection.CreateCommand();
        migrate.CommandText = "ALTER TABLE local_app_graph_observations ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'agent_exploration';";
        migrate.ExecuteNonQuery();
    }

    private static void EnsureSyncLinkLocalVersionColumn(SqliteConnection connection)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(local_app_graph_sync_links);";
        using var reader = inspect.ExecuteReader();
        var hasColumn = false;
        while (reader.Read())
        {
            if (reader.GetString(1).Equals("local_version_id", StringComparison.OrdinalIgnoreCase))
            {
                hasColumn = true;
                break;
            }
        }
        reader.Close();
        if (hasColumn)
        {
            return;
        }

        using var migrate = connection.CreateCommand();
        migrate.CommandText = "ALTER TABLE local_app_graph_sync_links ADD COLUMN local_version_id TEXT NULL;";
        migrate.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static CloudAppGraphSummary? ReadGraph(
        SqliteConnection connection,
        Guid graphId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, app_id, name, intent, status, version, current_version_id,
                   published_version_id, observation_count, successful_run_count,
                   failed_run_count, updated_utc
            FROM local_app_graphs WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", graphId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGraph(reader) : null;
    }

    private static CloudAppGraphSummary ReadGraph(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TeamId = LocalTeamId,
        TeamAppId = CreateStableAppId(reader.GetString(1)),
        Name = reader.GetString(2),
        Intent = reader.GetString(3),
        Status = reader.GetString(4),
        Version = reader.GetInt32(5),
        CurrentVersionId = ReadGuid(reader, 6),
        PublishedVersionId = ReadGuid(reader, 7),
        ObservationCount = reader.GetInt32(8),
        SuccessfulRunCount = reader.GetInt32(9),
        FailedRunCount = reader.GetInt32(10),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(11))
    };

    private static CloudAppGraphVersion? ReadVersion(SqliteConnection connection, Guid versionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, graph_id, version_number, status, definition_json, merge_summary, created_utc
            FROM local_app_graph_versions WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", versionId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new CloudAppGraphVersion
        {
            Id = Guid.Parse(reader.GetString(0)),
            TeamId = LocalTeamId,
            AppGraphId = Guid.Parse(reader.GetString(1)),
            VersionNumber = reader.GetInt32(2),
            Status = reader.GetString(3),
            Definition = JsonNode.Parse(reader.GetString(4))?.AsObject() ?? new JsonObject(),
            MergeSummary = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }

    private static IReadOnlyList<CloudAppGraphBinding> ReadBindings(SqliteConnection connection, Guid versionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, edge_id, priority, mechanism, configuration_json,
                   preconditions_json, postconditions_json, confidence
            FROM local_app_graph_bindings
            WHERE version_id = $version_id
            ORDER BY edge_id, priority;
            """;
        command.Parameters.AddWithValue("$version_id", versionId.ToString("D"));
        var bindings = new List<CloudAppGraphBinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bindings.Add(new CloudAppGraphBinding
            {
                Id = Guid.Parse(reader.GetString(0)),
                TeamId = LocalTeamId,
                AppGraphVersionId = versionId,
                EdgeId = reader.GetString(1),
                Priority = reader.GetInt32(2),
                Mechanism = reader.GetString(3),
                Configuration = JsonNode.Parse(reader.GetString(4))?.AsObject() ?? new JsonObject(),
                Preconditions = ReadStringArray(reader.GetString(5)),
                Postconditions = ReadStringArray(reader.GetString(6)),
                Confidence = reader.GetDecimal(7)
            });
        }
        return bindings;
    }

    private static void InsertGraph(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid graphId,
        string appId,
        string appName,
        string name,
        string intent,
        string status,
        int version,
        Guid currentVersionId,
        Guid? publishedVersionId,
        int observationCount,
        int successfulRunCount,
        int failedRunCount,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_app_graphs (
                id, app_id, app_name, name, intent, status, version, current_version_id,
                published_version_id, observation_count, successful_run_count,
                failed_run_count, updated_utc)
            VALUES ($id, $app_id, $app_name, $name, $intent, $status, $version,
                    $current_version_id, $published_version_id, $observation_count,
                    $successful_run_count, $failed_run_count, $updated_utc);
            """;
        command.Parameters.AddWithValue("$id", graphId.ToString("D"));
        command.Parameters.AddWithValue("$app_id", appId);
        command.Parameters.AddWithValue("$app_name", appName);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$intent", intent);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$current_version_id", currentVersionId.ToString("D"));
        command.Parameters.AddWithValue("$published_version_id", publishedVersionId.HasValue ? publishedVersionId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$observation_count", observationCount);
        command.Parameters.AddWithValue("$successful_run_count", successfulRunCount);
        command.Parameters.AddWithValue("$failed_run_count", failedRunCount);
        command.Parameters.AddWithValue("$updated_utc", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void InsertVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid versionId,
        Guid graphId,
        int versionNumber,
        string status,
        JsonObject definition,
        string? mergeSummary,
        DateTimeOffset createdAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_app_graph_versions (
                id, graph_id, version_number, status, definition_json, merge_summary, created_utc)
            VALUES ($id, $graph_id, $version_number, $status, $definition, $summary, $created_utc);
            """;
        command.Parameters.AddWithValue("$id", versionId.ToString("D"));
        command.Parameters.AddWithValue("$graph_id", graphId.ToString("D"));
        command.Parameters.AddWithValue("$version_number", versionNumber);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$definition", definition.ToJsonString());
        command.Parameters.AddWithValue("$summary", (object?)mergeSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_utc", createdAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void ReplaceBindings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid versionId,
        JsonObject? evidence,
        IReadOnlyList<CloudAppGraphBinding>? bindings)
    {
        var sourceBindings = bindings?.Select(binding => new LocalBindingCandidate(
                binding.Id,
                binding.EdgeId,
                binding.Priority,
                binding.Mechanism,
                binding.Configuration,
                binding.Preconditions,
                binding.Postconditions,
                binding.Confidence))
            .ToArray()
            ?? ReadBindingCandidates(evidence);
        foreach (var binding in sourceBindings)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_app_graph_bindings (
                    id, version_id, edge_id, priority, mechanism, configuration_json,
                    preconditions_json, postconditions_json, confidence)
                VALUES ($id, $version_id, $edge_id, $priority, $mechanism, $configuration,
                        $preconditions, $postconditions, $confidence);
                """;
            command.Parameters.AddWithValue("$id", (binding.Id == Guid.Empty ? Guid.NewGuid() : binding.Id).ToString("D"));
            command.Parameters.AddWithValue("$version_id", versionId.ToString("D"));
            command.Parameters.AddWithValue("$edge_id", binding.EdgeId);
            command.Parameters.AddWithValue("$priority", binding.Priority);
            command.Parameters.AddWithValue("$mechanism", binding.Mechanism);
            command.Parameters.AddWithValue("$configuration", binding.Configuration.ToJsonString());
            command.Parameters.AddWithValue("$preconditions", JsonSerializer.Serialize(binding.Preconditions));
            command.Parameters.AddWithValue("$postconditions", JsonSerializer.Serialize(binding.Postconditions));
            command.Parameters.AddWithValue("$confidence", binding.Confidence);
            command.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<LocalBindingCandidate> ReadBindingCandidates(JsonObject? evidence)
    {
        if (evidence?["bindingCandidates"] is not JsonArray candidates)
        {
            return [];
        }
        var result = new List<LocalBindingCandidate>();
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates.OfType<JsonObject>())
        {
            var edgeId = candidate["edgeId"]?.GetValue<string>()?.Trim();
            var mechanism = candidate["mechanism"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(edgeId)
                || string.IsNullOrWhiteSpace(mechanism)
                || candidate["configuration"] is not JsonObject configuration)
            {
                continue;
            }
            var priority = priorities.GetValueOrDefault(edgeId);
            priorities[edgeId] = priority + 1;
            result.Add(new LocalBindingCandidate(
                Guid.NewGuid(),
                edgeId,
                priority,
                mechanism,
                configuration.DeepClone().AsObject(),
                ReadStringArray(candidate["preconditions"]),
                ReadStringArray(candidate["postconditions"]),
                candidate["confidence"]?.GetValue<decimal>() ?? 0.5m));
        }
        return result;
    }

    private static Guid? FindGraphId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string appId,
        string graphName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM local_app_graphs WHERE app_id = $app_id AND name = $name;";
        command.Parameters.AddWithValue("$app_id", appId.Trim());
        command.Parameters.AddWithValue("$name", graphName.Trim());
        return Guid.TryParse(command.ExecuteScalar() as string, out var id) ? id : null;
    }

    private static Guid? FindLocalGraphIdForRemote(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid remoteGraphId,
        Guid remoteTeamId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT local_graph_id FROM local_app_graph_sync_links
            WHERE remote_graph_id = $graph_id AND remote_team_id = $team_id;
            """;
        command.Parameters.AddWithValue("$graph_id", remoteGraphId.ToString("D"));
        command.Parameters.AddWithValue("$team_id", remoteTeamId.ToString("D"));
        return Guid.TryParse(command.ExecuteScalar() as string, out var id) ? id : null;
    }

    private static void UpsertSyncLink(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid localGraphId,
        Guid remoteGraphId,
        Guid remoteTeamId,
        Guid? localVersionId,
        Guid? remoteVersionId,
        DateTimeOffset syncedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_app_graph_sync_links (
                local_graph_id, remote_graph_id, remote_team_id, local_version_id,
                remote_version_id, synced_utc)
            VALUES ($local_id, $remote_id, $team_id, $local_version_id, $remote_version_id, $synced_utc)
            ON CONFLICT(local_graph_id) DO UPDATE SET
                remote_graph_id = excluded.remote_graph_id,
                remote_team_id = excluded.remote_team_id,
                local_version_id = excluded.local_version_id,
                remote_version_id = excluded.remote_version_id,
                synced_utc = excluded.synced_utc;
            """;
        command.Parameters.AddWithValue("$local_id", localGraphId.ToString("D"));
        command.Parameters.AddWithValue("$remote_id", remoteGraphId.ToString("D"));
        command.Parameters.AddWithValue("$team_id", remoteTeamId.ToString("D"));
        command.Parameters.AddWithValue("$local_version_id", localVersionId.HasValue ? localVersionId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$remote_version_id", remoteVersionId.HasValue ? remoteVersionId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$synced_utc", syncedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static LocalAppGraphSyncLink ReadSyncLink(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        ReadGuid(reader, 3),
        ReadGuid(reader, 4),
        DateTimeOffset.Parse(reader.GetString(5)));

    private static LocalAppGraphSyncLink? ReadSyncLink(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid localGraphId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT local_graph_id, remote_graph_id, remote_team_id, local_version_id,
                   remote_version_id, synced_utc
            FROM local_app_graph_sync_links WHERE local_graph_id = $id;
            """;
        command.Parameters.AddWithValue("$id", localGraphId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSyncLink(reader) : null;
    }

    private static bool IsDefinitionValid(JsonObject definition)
        => definition["schema"]?.GetValue<string>() == "ansight.app-graph/v1"
           && definition["nodes"] is JsonArray
           && definition["edges"] is JsonArray;

    private static Guid CreateStableAppId(string appId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(appId.Trim().ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid? ReadGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string[] ReadStringArray(string json)
        => JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static string[] ReadStringArray(JsonNode? node)
        => node is JsonArray values
            ? values.OfType<JsonValue>().Select(static value => value.GetValue<string>()).ToArray()
            : [];

    private static void EnsureProviderInitialized()
    {
        lock (providerGate)
        {
            if (providerInitialized) return;
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

    private sealed record LocalBindingCandidate(
        Guid Id,
        string EdgeId,
        int Priority,
        string Mechanism,
        JsonObject Configuration,
        IReadOnlyList<string> Preconditions,
        IReadOnlyList<string> Postconditions,
        decimal Confidence);
}
