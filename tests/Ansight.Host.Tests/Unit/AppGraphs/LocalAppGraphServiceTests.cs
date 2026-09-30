using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;
using Ansight.Infrastructure;
using Ansight.Host.Tests.Unit.Runtime;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.Tests.Unit.AppGraphs;

public sealed class LocalAppGraphServiceTests
{
    [Fact]
    public void NavigationTechnologyCatalog_MapsFrameworkKindToCompatibilityRole()
    {
        var technology = new AppGraphNavigationTechnologyDescriptor(
            "react-native",
            "react_navigation_drawer",
            "react.get_navigation_state",
            new string('a', 64));

        var valid = AppGraphNavigationTechnologyCatalog.TryValidate(
            technology,
            AppGraphNavigationTechnologyCatalog.NavigationHostScope,
            out var role,
            out var error);

        Assert.True(valid, error);
        Assert.Equal("drawer", role);
    }

    [Fact]
    public void NavigationTechnologyCatalog_RejectsToolFromAnotherFramework()
    {
        var technology = new AppGraphNavigationTechnologyDescriptor(
            "maui",
            "shell_flyout",
            "react.get_navigation_state",
            new string('a', 64));

        var valid = AppGraphNavigationTechnologyCatalog.TryValidate(
            technology,
            AppGraphNavigationTechnologyCatalog.NavigationHostScope,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("does not belong", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAndObserve_PersistExecutableVersionsAndBindings()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAppGraphService(new DataToolApplicationPaths(directory.RootPath));
        var definition = CreateDefinition("settings");
        var evidence = CreateEvidence("open-settings");

        var created = service.Create(
            "com.example.app",
            "Example",
            "Default",
            "Navigate Example",
            definition,
            evidence);

        Assert.True(created.IsSuccess, created.Message);
        var createdDetail = Assert.IsType<CloudAppGraphDetail>(created.Detail);
        Assert.Single(createdDetail.Bindings);
        Assert.Equal("open-settings", createdDetail.Bindings[0].EdgeId);

        var observation = service.SaveObservation(
            new CloudAppGraphObservationCreateRequest(
                LocalAppGraphService.LocalTeamId,
                createdDetail.Graph.Id,
                "session-1",
                "com.example.app",
                "audit-1",
                CreateDefinition("profile"),
                CreateEvidence("open-profile"),
                0.95m,
                "Observed profile"));

        Assert.True(observation.IsSuccess, observation.Message);
        var updated = Assert.IsType<CloudAppGraphDetail>(service.Get(createdDetail.Graph.Id).Detail);
        Assert.Equal(2, updated.Version.VersionNumber);
        Assert.Equal("open-profile", Assert.Single(updated.Bindings).EdgeId);
        Assert.Equal(2, updated.Graph.ObservationCount);
    }

    [Fact]
    public void Import_PersistsRemoteLinkAndCanBePulledAgain()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAppGraphService(new DataToolApplicationPaths(directory.RootPath));
        var remoteGraphId = Guid.NewGuid();
        var remoteVersionId = Guid.NewGuid();
        var remoteTeamId = Guid.NewGuid();
        var remote = new CloudAppGraphDetail(
            new CloudAppGraphSummary
            {
                Id = remoteGraphId,
                TeamId = remoteTeamId,
                TeamAppId = Guid.NewGuid(),
                Name = "Remote",
                Intent = "Navigate remotely",
                Status = "published",
                Version = 4,
                CurrentVersionId = remoteVersionId,
                PublishedVersionId = remoteVersionId,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new CloudAppGraphVersion
            {
                Id = remoteVersionId,
                TeamId = remoteTeamId,
                AppGraphId = remoteGraphId,
                VersionNumber = 4,
                Status = "published",
                Definition = CreateDefinition("settings"),
                CreatedAt = DateTimeOffset.UtcNow
            },
            []);

        var imported = service.Import(new LocalAppGraphImportRequest(
            "com.example.app",
            "Example",
            remote,
            remoteTeamId));

        var detail = Assert.IsType<CloudAppGraphDetail>(imported.Detail);
        Assert.Equal(remoteGraphId, detail.Graph.Id);
        var link = Assert.IsType<LocalAppGraphSyncLink>(service.GetSyncLink(detail.Graph.Id));
        Assert.Equal(remoteGraphId, link.RemoteGraphId);
        Assert.Equal(detail.Version.Id, link.LocalVersionId);
        Assert.Equal(remoteVersionId, link.RemoteVersionId);

        var importedAgain = service.Import(new LocalAppGraphImportRequest(
            "com.example.app",
            "Example",
            remote,
            remoteTeamId));
        Assert.Equal(1, Assert.IsType<CloudAppGraphDetail>(importedAgain.Detail).Version.VersionNumber);
        Assert.Single(service.List("com.example.app").Graphs);
    }

    [Fact]
    public void Import_MergesIntoAnUnlinkedGraphWithTheSameAppAndName()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAppGraphService(new DataToolApplicationPaths(directory.RootPath));
        var local = Assert.IsType<CloudAppGraphDetail>(service.Create(
            "com.example.app",
            "Example",
            "Default",
            "Navigate locally",
            CreateDefinition("profile"),
            CreateEvidence("open-profile")).Detail);
        var remoteGraphId = Guid.NewGuid();
        var remoteVersionId = Guid.NewGuid();
        var remoteTeamId = Guid.NewGuid();
        var remote = new CloudAppGraphDetail(
            new CloudAppGraphSummary
            {
                Id = remoteGraphId,
                TeamId = remoteTeamId,
                TeamAppId = Guid.NewGuid(),
                Name = "Default",
                Intent = "Navigate remotely",
                Status = "draft",
                Version = 3,
                CurrentVersionId = remoteVersionId,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new CloudAppGraphVersion
            {
                Id = remoteVersionId,
                TeamId = remoteTeamId,
                AppGraphId = remoteGraphId,
                VersionNumber = 3,
                Status = "draft",
                Definition = CreateDefinition("settings"),
                CreatedAt = DateTimeOffset.UtcNow
            },
            []);

        var imported = service.Import(new LocalAppGraphImportRequest(
            "com.example.app",
            "Example",
            remote,
            remoteTeamId));

        var detail = Assert.IsType<CloudAppGraphDetail>(imported.Detail);
        Assert.Equal(local.Graph.Id, detail.Graph.Id);
        Assert.Equal(2, detail.Version.VersionNumber);
        Assert.Equal(remoteGraphId, service.GetSyncLink(local.Graph.Id)?.RemoteGraphId);
        Assert.Single(service.List("com.example.app").Graphs);
    }

    [Fact]
    public void ExistingCatalog_AddsRecordingCompatibilityColumns()
    {
        using var directory = new TemporaryDirectory();
        var paths = new DataToolApplicationPaths(directory.RootPath);
        var databasePath = Path.Combine(paths.ApplicationDataPath, "app-graph", "app-graphs.sqlite");
        var service = new LocalAppGraphService(paths);
        Assert.True(service.List().IsSuccess);
        using (var connection = CreateUnpooledConnection(databasePath))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE local_app_graph_sync_links;
                CREATE TABLE local_app_graph_sync_links (
                    local_graph_id TEXT PRIMARY KEY,
                    remote_graph_id TEXT NOT NULL,
                    remote_team_id TEXT NOT NULL,
                    remote_version_id TEXT NULL,
                    synced_utc TEXT NOT NULL,
                    UNIQUE(remote_graph_id, remote_team_id)
                );
                DROP TABLE local_app_graph_observations;
                CREATE TABLE local_app_graph_observations (
                    id TEXT PRIMARY KEY,
                    graph_id TEXT NOT NULL,
                    source_session_id TEXT NOT NULL,
                    source_app_id TEXT NOT NULL,
                    audit_run_id TEXT NOT NULL,
                    candidate_definition_json TEXT NOT NULL,
                    evidence_json TEXT NOT NULL,
                    confidence REAL NOT NULL,
                    notes TEXT NULL,
                    created_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        Assert.True(service.List().IsSuccess);

        using var verification = CreateUnpooledConnection(databasePath);
        verification.Open();
        using var inspect = verification.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(local_app_graph_sync_links);";
        using var reader = inspect.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        Assert.Contains("local_version_id", columns);
        reader.Close();
        using var observationInspect = verification.CreateCommand();
        observationInspect.CommandText = "PRAGMA table_info(local_app_graph_observations);";
        using var observationReader = observationInspect.ExecuteReader();
        var observationColumns = new List<string>();
        while (observationReader.Read()) observationColumns.Add(observationReader.GetString(1));
        Assert.Contains("source_kind", observationColumns);
    }

    [Fact]
    public void ManualRecording_PersistsAcrossServiceInstancesAndCommitsOneVersion()
    {
        using var directory = new TemporaryDirectory();
        var paths = new DataToolApplicationPaths(directory.RootPath);
        var service = new LocalAppGraphService(paths);
        var started = service.StartRecording(new LocalAppGraphRecordingStartRequest(
            "session-1",
            "com.example.app",
            "Example",
            "Recorded",
            "Navigate the recorded app"));
        var recording = Assert.IsType<LocalAppGraphRecording>(started.Recording);
        var captured = service.CaptureRecordingDestination(
            recording.Id,
            new LocalAppGraphRecordingDestinationRequest(
                "screen",
                "Home",
                "Start navigating"),
            CreateSnapshot(DateTimeOffset.UtcNow));
        Assert.True(captured.IsSuccess, captured.Message);

        var restoredService = new LocalAppGraphService(paths);
        var restored = Assert.Single(restoredService.ListRecordings("session-1"));
        Assert.Equal("Home", restored.Definition["nodes"]![0]!["name"]!.GetValue<string>());
        Assert.True(restoredService.StopRecording(restored.Id).IsSuccess);

        var committed = restoredService.CommitRecording(restored.Id);

        Assert.True(committed.IsSuccess, committed.Message);
        Assert.NotNull(committed.GraphId);
        Assert.NotNull(committed.VersionId);
        Assert.Equal("committed", committed.Recording?.Status);
        var graph = Assert.IsType<CloudAppGraphDetail>(restoredService.Get(committed.GraphId!.Value).Detail);
        Assert.Equal(1, graph.Version.VersionNumber);
        Assert.Equal("Home", graph.Version.Definition["nodes"]![0]!["name"]!.GetValue<string>());
        using var connection = CreateUnpooledConnection(
            Path.Combine(paths.ApplicationDataPath, "app-graph", "app-graphs.sqlite"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_kind FROM local_app_graph_observations WHERE graph_id = $graph_id;";
        command.Parameters.AddWithValue("$graph_id", committed.GraphId.Value.ToString("D"));
        Assert.Equal("manual_recording", command.ExecuteScalar());
    }

    [Fact]
    public void ManualRecording_ResolvesStableTapTargetAndPreservesEvidenceBinding()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAppGraphService(new DataToolApplicationPaths(directory.RootPath));
        var now = DateTimeOffset.UtcNow;
        var recording = Assert.IsType<LocalAppGraphRecording>(service.StartRecording(
            new LocalAppGraphRecordingStartRequest(
                "session-1",
                "com.example.app",
                "Example",
                "Recorded",
                "Navigate the recorded app")).Recording);
        recording = Assert.IsType<LocalAppGraphRecording>(service.CaptureRecordingDestination(
            recording.Id,
            new LocalAppGraphRecordingDestinationRequest("screen", "Home", "Start navigating"),
            CreateSnapshot(now, CreateVisualTree(now, "open-settings-button"))).Recording);
        Assert.True(service.ArmRecordingTransition(
            recording.Id,
            new LocalAppGraphRecordingArmTransitionRequest(),
            CreateSnapshot(now, CreateVisualTree(now, "open-settings-button"))).IsSuccess);
        var completed = service.CompleteRecordingTransition(
            recording.Id,
            new LocalAppGraphRecordingCompleteTransitionRequest(
                "Open settings",
                Destination: new LocalAppGraphRecordingDestinationRequest(
                    "dialog",
                    "Settings",
                    "Change preferences")),
            CreateSnapshot(
                now.AddMinutes(2),
                CreateVisualTree(now, "open-settings-button"),
                CreateTap(now.AddMinutes(2))));

        Assert.True(completed.IsSuccess, completed.Message);
        var updated = Assert.IsType<LocalAppGraphRecording>(completed.Recording);
        var edge = Assert.IsType<JsonObject>(updated.Definition["edges"]![0]);
        Assert.Equal("open-settings-button", edge["action"]!["automationId"]!.GetValue<string>());
        var binding = Assert.IsType<JsonObject>(updated.Evidence["bindingCandidates"]![0]);
        Assert.Equal("open-settings-button", binding["configuration"]!["selector"]!["automationId"]!.GetValue<string>());
        Assert.Null(updated.PendingTransition);
    }

    [Fact]
    public void ManualRecording_RequiresCompleteNavigationAndTabGroupsBeforeCommit()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAppGraphService(new DataToolApplicationPaths(directory.RootPath));
        var now = DateTimeOffset.UtcNow;
        var recording = Assert.IsType<LocalAppGraphRecording>(service.StartRecording(
            new LocalAppGraphRecordingStartRequest(
                "session-1",
                "com.example.app",
                "Example",
                "Recorded",
                "Navigate the recorded app")).Recording);
        recording = Assert.IsType<LocalAppGraphRecording>(service.CaptureRecordingDestination(
            recording.Id,
            new LocalAppGraphRecordingDestinationRequest("screen", "Shell", "Host navigation"),
            CreateSnapshot(now)).Recording);
        var shellId = recording.CurrentDestinationId!;
        recording = Assert.IsType<LocalAppGraphRecording>(service.CaptureRecordingDestination(
            recording.Id,
            new LocalAppGraphRecordingDestinationRequest("screen", "Home", "Use home"),
            CreateSnapshot(now)).Recording);
        var homeId = recording.CurrentDestinationId!;
        Assert.True(service.UpsertRecordingNavigationHost(
            recording.Id,
            new LocalAppGraphRecordingNavigationHostRequest(
                "flyout",
                "Main drawer",
                shellId,
                homeId,
                [homeId],
                "maui",
                Technology: new AppGraphNavigationTechnologyDescriptor(
                    "maui",
                    "shell_flyout",
                    "maui.get_navigation_state",
                    new string('a', 64)))).IsSuccess);
        Assert.True(service.StopRecording(recording.Id).IsSuccess);

        var committed = service.CommitRecording(recording.Id);

        Assert.False(committed.IsSuccess);
        Assert.Contains("at least two child screens", committed.Message, StringComparison.Ordinal);
    }

    private static JsonObject CreateDefinition(string destination) => JsonNode.Parse(
        $$"""
        {
          "schema": "ansight.app-graph/v1",
          "nodes": [
            { "id": "home", "kind": "screen", "name": "Home", "synonyms": [], "purpose": "Start", "isEntry": true },
            { "id": "{{destination}}", "kind": "screen", "name": "{{destination}}", "synonyms": [], "purpose": "Open {{destination}}" }
          ],
          "edges": [
            {
              "id": "open-{{destination}}",
              "from": "home",
              "to": "{{destination}}",
              "action": { "automationId": "{{destination}}-button", "semanticMeaning": "Open {{destination}}" },
              "postconditions": ["{{destination}} is visible"]
            }
          ]
        }
        """)!.AsObject();

    private static JsonObject CreateEvidence(string edgeId) => new()
    {
        ["schema"] = "ansight.app-graph-exploration-evidence/v1",
        ["bindingCandidates"] = new JsonArray
        {
            new JsonObject
            {
                ["edgeId"] = edgeId,
                ["mechanism"] = "ui_action",
                ["configuration"] = new JsonObject
                {
                    ["action"] = "tap",
                    ["selector"] = new JsonObject { ["automationId"] = $"{edgeId}-button" }
                },
                ["preconditions"] = new JsonArray(),
                ["postconditions"] = new JsonArray("Destination is visible"),
                ["confidence"] = 0.9m
            }
        }
    };

    private static AppSessionSnapshot CreateSnapshot(
        DateTimeOffset timestamp,
        SessionVisualTreeSnapshot? visualTree = null,
        params SessionTouchInputRecord[] touches)
        => new()
        {
            SessionId = "session-1",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "local",
            CreatedUtc = timestamp,
            ConfigId = null,
            Status = "connected",
            LastUpdatedUtc = timestamp,
            IsHistorical = false,
            Touches = touches,
            VisualTreeSnapshots = visualTree is null ? [] : [visualTree],
            MetricChannels = [],
            Metrics = []
        };

    private static SessionVisualTreeSnapshot CreateVisualTree(
        DateTimeOffset timestamp,
        string automationId)
        => new()
        {
            SnapshotId = $"tree-{timestamp.UtcTicks}",
            CapturedAtUtc = timestamp,
            Source = "test",
            NodeCount = 1,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = "button-1",
                    ["type"] = "Button",
                    ["automationId"] = automationId,
                    ["label"] = "Open settings",
                    ["normalizedBounds"] = new JsonObject
                    {
                        ["x"] = 0.4,
                        ["y"] = 0.4,
                        ["width"] = 0.2,
                        ["height"] = 0.2
                    },
                    ["children"] = new JsonArray()
                }
            }
        };

    private static SessionTouchInputRecord[] CreateTap(DateTimeOffset timestamp)
        =>
        [
            new SessionTouchInputRecord
            {
                Id = "touch-down",
                Action = "down",
                CapturedAtUtc = timestamp,
                PointerId = 1,
                PointerIndex = 0,
                PointerCount = 1,
                X = 0.5,
                Y = 0.5,
                NormalizedX = 0.5,
                NormalizedY = 0.5,
                CoordinateUnit = "normalized"
            },
            new SessionTouchInputRecord
            {
                Id = "touch-up",
                Action = "up",
                CapturedAtUtc = timestamp.AddMilliseconds(80),
                PointerId = 1,
                PointerIndex = 0,
                PointerCount = 1,
                X = 0.5,
                Y = 0.5,
                NormalizedX = 0.5,
                NormalizedY = 0.5,
                CoordinateUnit = "normalized"
            }
        ];

    private static SqliteConnection CreateUnpooledConnection(string filePath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Pooling = false
        }.ToString());
}
