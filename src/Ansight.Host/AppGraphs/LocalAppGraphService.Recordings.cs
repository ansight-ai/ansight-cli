using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphRecording(
    Guid Id,
    string SessionId,
    string AppId,
    string AppName,
    Guid? AppGraphId,
    Guid? BaseVersionId,
    Guid? CommittedVersionId,
    string GraphName,
    string Intent,
    string Status,
    string? CurrentDestinationId,
    JsonObject Definition,
    JsonObject Evidence,
    JsonObject? PendingTransition,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    IReadOnlyList<string> Gaps);

public readonly record struct LocalAppGraphRecordingOperationResult(
    bool IsSuccess,
    string Message,
    LocalAppGraphRecording? Recording,
    Guid? GraphId = null,
    Guid? VersionId = null)
{
    public static LocalAppGraphRecordingOperationResult Success(
        LocalAppGraphRecording recording,
        string message,
        Guid? graphId = null,
        Guid? versionId = null)
        => new(true, message, recording, graphId, versionId);

    public static LocalAppGraphRecordingOperationResult Failure(string message)
        => new(false, message, null);
}

public sealed record LocalAppGraphRecordingStartRequest(
    string SessionId,
    string AppId,
    string AppName,
    string GraphName,
    string Intent,
    Guid? AppGraphId = null);

public sealed record LocalAppGraphRecordingDestinationRequest(
    string Kind,
    string Name,
    string Purpose,
    string? DestinationId = null,
    string? ParentDestinationId = null,
    IReadOnlyList<string>? Synonyms = null);

public sealed record LocalAppGraphRecordingArmTransitionRequest(string? FromDestinationId = null);

public sealed record LocalAppGraphRecordingCompleteTransitionRequest(
    string SemanticMeaning,
    string? AutomationId = null,
    string? ExistingDestinationId = null,
    LocalAppGraphRecordingDestinationRequest? Destination = null);

public sealed record LocalAppGraphRecordingNavigationHostRequest(
    string Kind,
    string Name,
    string DestinationId,
    string ActiveChildDestinationId,
    IReadOnlyList<string> ChildDestinationIds,
    string Framework,
    string? HostId = null,
    AppGraphNavigationTechnologyDescriptor? Technology = null);

public sealed record LocalAppGraphRecordingTabGroupRequest(
    string ParentDestinationId,
    string SelectedDestinationId,
    IReadOnlyList<string> TabDestinationIds,
    string? TabGroupId = null,
    AppGraphNavigationTechnologyDescriptor? Technology = null);

public sealed partial class LocalAppGraphService
{
    private static readonly HashSet<string> destinationKinds = new(
        ["screen", "dialog", "state"],
        StringComparer.Ordinal);
    public IReadOnlyList<LocalAppGraphRecording> ListRecordings(string? sessionId = null)
    {
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, session_id, app_id, app_name, graph_id, base_version_id,
                       committed_version_id, graph_name, intent, status,
                       current_destination_id, definition_json, evidence_json,
                       pending_transition_json, started_utc, updated_utc, stopped_utc
                FROM local_app_graph_recordings
                WHERE ($session_id = '' OR session_id = $session_id)
                ORDER BY updated_utc DESC;
                """;
            command.Parameters.AddWithValue("$session_id", sessionId?.Trim() ?? string.Empty);
            using var reader = command.ExecuteReader();
            var recordings = new List<LocalAppGraphRecording>();
            while (reader.Read())
            {
                recordings.Add(ReadRecording(reader));
            }
            return recordings;
        }
    }

    public LocalAppGraphRecording? GetRecording(Guid recordingId)
    {
        if (recordingId == Guid.Empty)
        {
            return null;
        }

        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            return ReadRecording(connection, recordingId);
        }
    }

    public LocalAppGraphRecordingOperationResult StartRecording(LocalAppGraphRecordingStartRequest request)
        => analytics.ObserveUsage("graph_recording", () => StartRecordingMeasuredCore(request),
            result => result.IsSuccess ? "succeeded" : "blocked");

    private LocalAppGraphRecordingOperationResult StartRecordingMeasuredCore(LocalAppGraphRecordingStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.AppId)
            || string.IsNullOrWhiteSpace(request.GraphName))
        {
            return LocalAppGraphRecordingOperationResult.Failure(
                "Session, app, and App Graph name are required.");
        }

        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var active = FindActiveRecording(connection, transaction, request.SessionId.Trim());
            if (active is not null)
            {
                transaction.Commit();
                return LocalAppGraphRecordingOperationResult.Success(
                    active,
                    $"Resumed local walkthrough '{active.GraphName}'.");
            }

            Guid? baseVersionId = null;
            JsonObject definition;
            JsonObject evidence;
            if (request.AppGraphId.HasValue)
            {
                var graph = ReadGraph(connection, request.AppGraphId.Value, transaction);
                if (graph is null)
                {
                    return LocalAppGraphRecordingOperationResult.Failure(
                        $"Local App Graph '{request.AppGraphId.Value:D}' was not found.");
                }
                var identity = GetIdentityInTransaction(connection, transaction, request.AppGraphId.Value);
                if (identity is null
                    || !string.Equals(identity.AppId, request.AppId.Trim(), StringComparison.Ordinal))
                {
                    return LocalAppGraphRecordingOperationResult.Failure(
                        "The selected App Graph belongs to a different app.");
                }
                if (!graph.CurrentVersionId.HasValue)
                {
                    return LocalAppGraphRecordingOperationResult.Failure(
                        "The selected App Graph has no current version.");
                }

                var version = ReadVersionInTransaction(connection, transaction, graph.CurrentVersionId.Value);
                if (version is null)
                {
                    return LocalAppGraphRecordingOperationResult.Failure(
                        "The selected App Graph version could not be read.");
                }
                baseVersionId = version.Id;
                definition = version.Definition.DeepClone().AsObject();
                evidence = CreateRecordingEvidence(ReadBindingsInTransaction(connection, transaction, version.Id));
            }
            else
            {
                definition = CreateEmptyRecordingDefinition();
                evidence = CreateRecordingEvidence([]);
            }

            EnsureDefinitionCollections(definition);
            var now = DateTimeOffset.UtcNow;
            var recording = new LocalAppGraphRecording(
                Guid.NewGuid(),
                request.SessionId.Trim(),
                request.AppId.Trim(),
                string.IsNullOrWhiteSpace(request.AppName) ? request.AppId.Trim() : request.AppName.Trim(),
                request.AppGraphId,
                baseVersionId,
                null,
                request.GraphName.Trim(),
                request.Intent?.Trim() ?? string.Empty,
                "recording",
                null,
                definition,
                evidence,
                null,
                now,
                now,
                null,
                []);
            InsertRecording(connection, transaction, recording);
            transaction.Commit();
            return LocalAppGraphRecordingOperationResult.Success(
                recording with { Gaps = CollectRecordingGaps(recording.Definition, null, strict: false) },
                $"Started local walkthrough '{recording.GraphName}'.");
        }
    }

    public LocalAppGraphRecordingOperationResult CaptureRecordingDestination(
        Guid recordingId,
        LocalAppGraphRecordingDestinationRequest request,
        AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        return UpdateRecording(recordingId, "capture a destination", (recording, now) =>
        {
            if (!TryCaptureDestination(recording, request, snapshot, now, out var updated, out var error))
            {
                return RecordingMutation.Failure(error);
            }
            return RecordingMutation.Success(updated!, $"Recorded {request.Kind.Trim().ToLowerInvariant()} '{request.Name.Trim()}'.");
        });
    }

    public LocalAppGraphRecordingOperationResult ArmRecordingTransition(
        Guid recordingId,
        LocalAppGraphRecordingArmTransitionRequest request,
        AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        return UpdateRecording(recordingId, "arm a transition", (recording, now) =>
        {
            if (recording.PendingTransition is not null)
            {
                return RecordingMutation.Failure("A transition is already armed.");
            }

            var from = string.IsNullOrWhiteSpace(request.FromDestinationId)
                ? recording.CurrentDestinationId
                : request.FromDestinationId.Trim();
            if (string.IsNullOrWhiteSpace(from) || FindNode(recording.Definition, from) is null)
            {
                return RecordingMutation.Failure("Choose a recorded source destination first.");
            }

            var visualTree = snapshot.VisualTreeSnapshots.OrderBy(candidate => candidate.CapturedAtUtc).LastOrDefault();
            var screenshot = snapshot.Images.OrderBy(candidate => candidate.CapturedAtUtc).LastOrDefault();
            var pending = new JsonObject
            {
                ["fromDestinationId"] = from,
                ["armedAtUtc"] = now,
                ["touchStartIndex"] = snapshot.Touches.Count,
                ["preVisualTreeSnapshotId"] = visualTree?.SnapshotId,
                ["preScreenshotFrameId"] = visualTree?.ScreenshotFrameId ?? screenshot?.FrameId
            };
            return RecordingMutation.Success(
                recording with
                {
                    CurrentDestinationId = from,
                    PendingTransition = pending,
                    UpdatedAtUtc = now
                },
                "Transition armed. Perform one app interaction, then capture its result.");
        });
    }

    public LocalAppGraphRecordingOperationResult CompleteRecordingTransition(
        Guid recordingId,
        LocalAppGraphRecordingCompleteTransitionRequest request,
        AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        return UpdateRecording(recordingId, "complete a transition", (recording, now) =>
        {
            if (recording.PendingTransition is not JsonObject pending)
            {
                return RecordingMutation.Failure("Arm a transition before capturing its result.");
            }
            if (string.IsNullOrWhiteSpace(request.SemanticMeaning))
            {
                return RecordingMutation.Failure("Describe what the interacted element does.");
            }

            var working = recording;
            string toDestinationId;
            if (!string.IsNullOrWhiteSpace(request.ExistingDestinationId))
            {
                toDestinationId = request.ExistingDestinationId.Trim();
                if (FindNode(working.Definition, toDestinationId) is null)
                {
                    return RecordingMutation.Failure("The selected result destination was not found in this draft.");
                }
                working = AppendCaptureEvidence(working, toDestinationId, "transition_result", snapshot, now);
            }
            else if (request.Destination is not null)
            {
                if (!TryCaptureDestination(working, request.Destination, snapshot, now, out var captured, out var captureError))
                {
                    return RecordingMutation.Failure(captureError);
                }
                working = captured!;
                toDestinationId = working.CurrentDestinationId!;
            }
            else
            {
                return RecordingMutation.Failure("Choose an existing result destination or describe a new one.");
            }

            var touchStartIndex = pending["touchStartIndex"]?.GetValue<int>() ?? snapshot.Touches.Count;
            touchStartIndex = Math.Clamp(touchStartIndex, 0, snapshot.Touches.Count);
            var touches = snapshot.Touches.Skip(touchStartIndex).ToArray();
            var gestures = TouchGestureSegmenter.BuildGestureSegments(
                touches,
                TimeSpan.FromMilliseconds(TouchReviewDefaults.DefaultGestureGapMilliseconds));
            if (gestures.Count > 1)
            {
                return RecordingMutation.Failure(
                    "More than one app gesture occurred. Record an intermediate destination and capture one transition at a time.");
            }

            var gesture = gestures.SingleOrDefault();
            var automationId = request.AutomationId?.Trim();
            JsonObject? matchedTarget = null;
            if (string.IsNullOrWhiteSpace(automationId) && gesture is not null)
            {
                var point = TouchReviewGeometry.ResolveGesturePoint(gesture);
                if (point.HasValue)
                {
                    var preVisualTreeSnapshotId = ReadString(pending, "preVisualTreeSnapshotId");
                    var preVisualTree = snapshot.VisualTreeSnapshots.FirstOrDefault(candidate => string.Equals(
                        candidate.SnapshotId,
                        preVisualTreeSnapshotId,
                        StringComparison.Ordinal));
                    var targetCandidates = preVisualTree is null
                        ? TapTargetMatcher.FindTapTargetCandidates(
                            snapshot,
                            gesture,
                            point.Value,
                            TimeSpan.FromSeconds(10))
                        : TapTargetMatcher.FindVisualTreeTapTargetCandidates(
                            snapshot,
                            preVisualTree,
                            point.Value);
                    matchedTarget = targetCandidates
                        .Where(candidate => !string.IsNullOrWhiteSpace(ReadString(candidate, "automationId")))
                        .OrderByDescending(candidate => candidate["depth"]?.GetValue<int>() ?? 0)
                        .FirstOrDefault();
                    automationId = ReadString(matchedTarget, "automationId");
                }
            }

            if (string.IsNullOrWhiteSpace(automationId))
            {
                return RecordingMutation.Failure(
                    "No stable automation ID could be proven for this interaction. Add one in the app or enter the known ID before completing the transition.");
            }

            var fromDestinationId = ReadString(pending, "fromDestinationId")!;
            var edges = working.Definition["edges"]!.AsArray();
            var edgeId = CreateUniqueId(
                edges.OfType<JsonObject>().Select(edge => ReadString(edge, "id")),
                $"{fromDestinationId}-{request.SemanticMeaning}-{toDestinationId}",
                "transition");
            var destinationName = ReadString(FindNode(working.Definition, toDestinationId), "name") ?? toDestinationId;
            var postcondition = $"{destinationName} is visible";
            edges.Add(new JsonObject
            {
                ["id"] = edgeId,
                ["from"] = fromDestinationId,
                ["to"] = toDestinationId,
                ["action"] = new JsonObject
                {
                    ["automationId"] = automationId,
                    ["semanticMeaning"] = request.SemanticMeaning.Trim()
                },
                ["postconditions"] = new JsonArray(postcondition),
                ["confidence"] = matchedTarget is null ? 0.8m : 0.95m
            });

            var postTree = snapshot.VisualTreeSnapshots.OrderBy(candidate => candidate.CapturedAtUtc).LastOrDefault();
            var postScreenshot = snapshot.Images.OrderBy(candidate => candidate.CapturedAtUtc).LastOrDefault();
            var bindingCandidates = working.Evidence["bindingCandidates"]!.AsArray();
            bindingCandidates.Add(new JsonObject
            {
                ["edgeId"] = edgeId,
                ["mechanism"] = "ui_action",
                ["configuration"] = new JsonObject
                {
                    ["action"] = MapGestureToAction(gesture?.Kind),
                    ["selector"] = new JsonObject { ["automationId"] = automationId }
                },
                ["preconditions"] = new JsonArray(),
                ["postconditions"] = new JsonArray(postcondition),
                ["confidence"] = matchedTarget is null ? 0.8m : 0.95m,
                ["recordingEvidence"] = new JsonObject
                {
                    ["gestureId"] = gesture?.GestureId,
                    ["gestureKind"] = gesture?.Kind,
                    ["preVisualTreeSnapshotId"] = ReadString(pending, "preVisualTreeSnapshotId"),
                    ["preScreenshotFrameId"] = ReadString(pending, "preScreenshotFrameId"),
                    ["postVisualTreeSnapshotId"] = postTree?.SnapshotId,
                    ["postScreenshotFrameId"] = postTree?.ScreenshotFrameId ?? postScreenshot?.FrameId,
                    ["matchedTarget"] = matchedTarget?.DeepClone()
                }
            });

            return RecordingMutation.Success(
                working with
                {
                    CurrentDestinationId = toDestinationId,
                    PendingTransition = null,
                    UpdatedAtUtc = now
                },
                $"Recorded transition to '{destinationName}'.");
        });
    }

    public LocalAppGraphRecordingOperationResult UpsertRecordingNavigationHost(
        Guid recordingId,
        LocalAppGraphRecordingNavigationHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateRecording(recordingId, "define a navigation host", (recording, now) =>
        {
            var children = NormalizeIds(request.ChildDestinationIds);
            if (!AppGraphNavigationTechnologyCatalog.TryValidate(
                    request.Technology,
                    AppGraphNavigationTechnologyCatalog.NavigationHostScope,
                    out var kind,
                    out var technologyError))
            {
                return RecordingMutation.Failure(technologyError);
            }
            if (!string.IsNullOrWhiteSpace(request.Kind)
                && !string.Equals(request.Kind.Trim(), kind, StringComparison.OrdinalIgnoreCase))
            {
                return RecordingMutation.Failure(
                    $"Navigation host kind must be '{kind}' for technology '{request.Technology!.Framework}/{request.Technology.Kind}'.");
            }
            if (!string.IsNullOrWhiteSpace(request.Framework)
                && !string.Equals(
                    request.Framework.Trim(),
                    request.Technology!.Framework.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return RecordingMutation.Failure(
                    "Navigation host framework must match its technology descriptor.");
            }
            if (string.IsNullOrWhiteSpace(request.Name)
                || children.Count == 0)
            {
                return RecordingMutation.Failure(
                    "Navigation host kind, name, and at least one child destination are required.");
            }
            if (FindNode(recording.Definition, request.DestinationId) is not JsonObject hostDestination
                || children.Any(child => FindNode(recording.Definition, child) is null)
                || !children.Contains(request.ActiveChildDestinationId, StringComparer.Ordinal))
            {
                return RecordingMutation.Failure(
                    "Navigation host, active child, and child destinations must reference recorded destinations.");
            }
            if (children.Any(child => !string.Equals(ReadString(FindNode(recording.Definition, child), "kind"), "screen", StringComparison.Ordinal)))
            {
                return RecordingMutation.Failure("Navigation host children must be screen destinations.");
            }

            var hosts = recording.Definition["navigationHosts"]!.AsArray();
            var hostId = string.IsNullOrWhiteSpace(request.HostId)
                ? CreateUniqueId(hosts.OfType<JsonObject>().Select(host => ReadString(host, "id")), request.Name, "navigation-host")
                : request.HostId.Trim();
            var host = new JsonObject
            {
                ["id"] = hostId,
                ["kind"] = kind,
                ["name"] = request.Name.Trim(),
                ["destinationId"] = request.DestinationId.Trim(),
                ["activeChildDestinationId"] = request.ActiveChildDestinationId.Trim(),
                ["childDestinationIds"] = new JsonArray(children.Select(value => (JsonNode?)value).ToArray()),
                ["framework"] = request.Technology!.Framework.Trim().ToLowerInvariant(),
                ["technology"] = AppGraphNavigationTechnologyCatalog.ToJson(request.Technology),
                ["confidence"] = 1m
            };
            UpsertById(hosts, hostId, host);
            var parentName = ReadString(hostDestination, "name")!;
            foreach (var child in children)
            {
                FindNode(recording.Definition, child)!["parentScreen"] = parentName;
            }
            return RecordingMutation.Success(
                recording with { UpdatedAtUtc = now },
                $"Saved navigation host '{request.Name.Trim()}'.");
        });
    }

    public LocalAppGraphRecordingOperationResult UpsertRecordingTabGroup(
        Guid recordingId,
        LocalAppGraphRecordingTabGroupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateRecording(recordingId, "define an internal tab group", (recording, now) =>
        {
            if (!AppGraphNavigationTechnologyCatalog.TryValidate(
                    request.Technology,
                    AppGraphNavigationTechnologyCatalog.TabGroupScope,
                    out _,
                    out var technologyError))
            {
                return RecordingMutation.Failure(technologyError);
            }

            var tabs = NormalizeIds(request.TabDestinationIds);
            if (tabs.Count == 0
                || FindNode(recording.Definition, request.ParentDestinationId) is not JsonObject parent
                || tabs.Any(tab => FindNode(recording.Definition, tab) is null)
                || !tabs.Contains(request.SelectedDestinationId, StringComparer.Ordinal))
            {
                return RecordingMutation.Failure(
                    "Tab parent, selected tab, and at least one recorded tab destination are required.");
            }
            if (tabs.Any(tab => !string.Equals(ReadString(FindNode(recording.Definition, tab), "kind"), "state", StringComparison.Ordinal)))
            {
                return RecordingMutation.Failure("Internal tab destinations must use kind state.");
            }

            var groups = recording.Definition["tabGroups"]!.AsArray();
            var groupId = string.IsNullOrWhiteSpace(request.TabGroupId)
                ? CreateUniqueId(groups.OfType<JsonObject>().Select(group => ReadString(group, "id")), $"{ReadString(parent, "name")}-tabs", "tab-group")
                : request.TabGroupId.Trim();
            var group = new JsonObject
            {
                ["id"] = groupId,
                ["parentDestinationId"] = request.ParentDestinationId.Trim(),
                ["selectedDestinationId"] = request.SelectedDestinationId.Trim(),
                ["tabDestinationIds"] = new JsonArray(tabs.Select(value => (JsonNode?)value).ToArray()),
                ["technology"] = AppGraphNavigationTechnologyCatalog.ToJson(request.Technology!),
                ["confidence"] = 1m
            };
            UpsertById(groups, groupId, group);
            var parentName = ReadString(parent, "name")!;
            foreach (var tab in tabs)
            {
                FindNode(recording.Definition, tab)!["parentScreen"] = parentName;
            }
            return RecordingMutation.Success(
                recording with { UpdatedAtUtc = now },
                $"Saved internal tab group for '{parentName}'.");
        });
    }

    public LocalAppGraphRecordingOperationResult StopRecording(Guid recordingId)
        => UpdateRecording(recordingId, "stop recording", (recording, now) =>
        {
            if (recording.PendingTransition is not null)
            {
                return RecordingMutation.Failure("Complete the armed transition before stopping.");
            }
            return RecordingMutation.Success(
                recording with { Status = "review", UpdatedAtUtc = now, StoppedAtUtc = now },
                "Walkthrough stopped and is ready for review.");
        });

    public LocalAppGraphRecordingOperationResult ResumeRecording(Guid recordingId)
        => UpdateRecording(recordingId, "resume recording", (recording, now) =>
            RecordingMutation.Success(
                recording with { Status = "recording", UpdatedAtUtc = now, StoppedAtUtc = null },
                "Walkthrough recording resumed."),
            allowReview: true);

    public LocalAppGraphRecordingOperationResult CancelRecording(Guid recordingId)
        => UpdateRecording(recordingId, "cancel recording", (recording, now) =>
            RecordingMutation.Success(
                recording with { Status = "cancelled", PendingTransition = null, UpdatedAtUtc = now, StoppedAtUtc = now },
                "Walkthrough draft cancelled."),
            allowReview: true);

    public LocalAppGraphRecordingOperationResult CommitRecording(Guid recordingId)
        => analytics.ObserveUsage("graph_saved", () => CommitRecordingMeasuredCore(recordingId),
            result => result.IsSuccess ? "succeeded" : "blocked");

    private LocalAppGraphRecordingOperationResult CommitRecordingMeasuredCore(Guid recordingId)
    {
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var recording = ReadRecording(connection, recordingId, transaction);
            if (recording is null)
            {
                return LocalAppGraphRecordingOperationResult.Failure("Walkthrough recording was not found.");
            }
            if (!string.Equals(recording.Status, "review", StringComparison.Ordinal))
            {
                return LocalAppGraphRecordingOperationResult.Failure("Stop the walkthrough before committing it.");
            }
            var gaps = CollectRecordingGaps(recording.Definition, recording.PendingTransition, strict: true);
            if (gaps.Count > 0)
            {
                return LocalAppGraphRecordingOperationResult.Failure(
                    $"The walkthrough is not ready to commit: {string.Join(" ", gaps)}");
            }

            var now = DateTimeOffset.UtcNow;
            Guid graphId;
            Guid versionId = Guid.NewGuid();
            int versionNumber;
            if (recording.AppGraphId.HasValue)
            {
                var graph = ReadGraph(connection, recording.AppGraphId.Value, transaction);
                if (graph is null)
                {
                    return LocalAppGraphRecordingOperationResult.Failure("The target App Graph no longer exists.");
                }
                if (graph.CurrentVersionId != recording.BaseVersionId)
                {
                    return LocalAppGraphRecordingOperationResult.Failure(
                        "The App Graph changed after this walkthrough started. Start a new recording from the latest version.");
                }
                graphId = graph.Id;
                versionNumber = graph.Version + 1;
                InsertVersion(
                    connection,
                    transaction,
                    versionId,
                    graphId,
                    versionNumber,
                    "draft",
                    recording.Definition,
                    "Updated from a local manual walkthrough.",
                    now);
                ReplaceBindings(connection, transaction, versionId, recording.Evidence, bindings: null);
                InsertManualRecordingObservation(connection, transaction, recording, graphId, now);
                using var updateGraph = connection.CreateCommand();
                updateGraph.Transaction = transaction;
                updateGraph.CommandText = """
                    UPDATE local_app_graphs
                    SET status = 'draft', version = $version, current_version_id = $version_id,
                        observation_count = observation_count + 1, updated_utc = $updated_utc
                    WHERE id = $graph_id;
                    """;
                updateGraph.Parameters.AddWithValue("$version", versionNumber);
                updateGraph.Parameters.AddWithValue("$version_id", versionId.ToString("D"));
                updateGraph.Parameters.AddWithValue("$updated_utc", now.ToString("O"));
                updateGraph.Parameters.AddWithValue("$graph_id", graphId.ToString("D"));
                updateGraph.ExecuteNonQuery();
            }
            else
            {
                if (FindGraphId(connection, transaction, recording.AppId, recording.GraphName).HasValue)
                {
                    return LocalAppGraphRecordingOperationResult.Failure(
                        $"App '{recording.AppId}' already has an App Graph named '{recording.GraphName}'.");
                }
                graphId = Guid.NewGuid();
                versionNumber = 1;
                InsertGraph(
                    connection,
                    transaction,
                    graphId,
                    recording.AppId,
                    recording.AppName,
                    recording.GraphName,
                    recording.Intent,
                    "draft",
                    versionNumber,
                    versionId,
                    publishedVersionId: null,
                    observationCount: 1,
                    successfulRunCount: 0,
                    failedRunCount: 0,
                    now);
                InsertVersion(
                    connection,
                    transaction,
                    versionId,
                    graphId,
                    versionNumber,
                    "draft",
                    recording.Definition,
                    "Created from a local manual walkthrough.",
                    now);
                ReplaceBindings(connection, transaction, versionId, recording.Evidence, bindings: null);
                InsertManualRecordingObservation(connection, transaction, recording, graphId, now);
            }

            var committed = recording with
            {
                AppGraphId = graphId,
                BaseVersionId = versionId,
                CommittedVersionId = versionId,
                Status = "committed",
                UpdatedAtUtc = now,
                StoppedAtUtc = now,
                Gaps = []
            };
            WriteRecording(connection, transaction, committed);
            transaction.Commit();
            return LocalAppGraphRecordingOperationResult.Success(
                committed,
                $"Committed App Graph '{recording.GraphName}' version {versionNumber}.",
                graphId,
                versionId);
        }
    }

    private LocalAppGraphRecordingOperationResult UpdateRecording(
        Guid recordingId,
        string operation,
        Func<LocalAppGraphRecording, DateTimeOffset, RecordingMutation> mutate,
        bool allowReview = false)
    {
        lock (databaseGate)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var recording = ReadRecording(connection, recordingId, transaction);
            if (recording is null)
            {
                return LocalAppGraphRecordingOperationResult.Failure("Walkthrough recording was not found.");
            }
            if (!string.Equals(recording.Status, "recording", StringComparison.Ordinal)
                && !(allowReview && string.Equals(recording.Status, "review", StringComparison.Ordinal)))
            {
                return LocalAppGraphRecordingOperationResult.Failure(
                    $"Cannot {operation} while the walkthrough is {recording.Status}.");
            }

            var mutation = mutate(recording, DateTimeOffset.UtcNow);
            if (!mutation.IsSuccess || mutation.Recording is null)
            {
                return LocalAppGraphRecordingOperationResult.Failure(mutation.Message);
            }
            var updated = mutation.Recording with
            {
                Gaps = CollectRecordingGaps(
                    mutation.Recording.Definition,
                    mutation.Recording.PendingTransition,
                    strict: string.Equals(mutation.Recording.Status, "review", StringComparison.Ordinal))
            };
            WriteRecording(connection, transaction, updated);
            transaction.Commit();
            return LocalAppGraphRecordingOperationResult.Success(updated, mutation.Message);
        }
    }

    private static bool TryCaptureDestination(
        LocalAppGraphRecording recording,
        LocalAppGraphRecordingDestinationRequest request,
        AppSessionSnapshot snapshot,
        DateTimeOffset now,
        out LocalAppGraphRecording? updated,
        out string error)
    {
        updated = null;
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !destinationKinds.Contains(kind))
        {
            error = "Destination kind must be screen, dialog, or state.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Purpose))
        {
            error = "Destination name and purpose are required.";
            return false;
        }

        string? parentScreen = null;
        if (kind == "state")
        {
            if (string.IsNullOrWhiteSpace(request.ParentDestinationId)
                || FindNode(recording.Definition, request.ParentDestinationId) is not JsonObject parent)
            {
                error = "An internal state requires its recorded containing screen.";
                return false;
            }
            parentScreen = ReadString(parent, "name");
        }
        else if (kind == "dialog" && !string.IsNullOrWhiteSpace(request.ParentDestinationId))
        {
            error = "Dialogs are top-level destinations and cannot have a containing screen.";
            return false;
        }

        var nodes = recording.Definition["nodes"]!.AsArray();
        var existingId = request.DestinationId?.Trim();
        var id = string.IsNullOrWhiteSpace(existingId)
            ? CreateUniqueId(nodes.OfType<JsonObject>().Select(node => ReadString(node, "id")), request.Name, kind)
            : existingId;
        var existing = FindNode(recording.Definition, id);
        var index = existing is null ? nodes.Count : IndexOfId(nodes, id);
        var node = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["name"] = request.Name.Trim(),
            ["synonyms"] = new JsonArray((request.Synonyms ?? [])
                .Select(value => value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => (JsonNode?)value)
                .ToArray()),
            ["purpose"] = request.Purpose.Trim(),
            ["isEntry"] = existing?["isEntry"]?.GetValue<bool>() ?? nodes.Count == 0,
            ["x"] = existing?["x"]?.GetValue<double>() ?? 120 + ((index % 4) * 280),
            ["y"] = existing?["y"]?.GetValue<double>() ?? 120 + ((index / 4) * 180),
            ["confidence"] = 1m
        };
        if (!string.IsNullOrWhiteSpace(parentScreen))
        {
            node["parentScreen"] = parentScreen;
        }
        UpsertById(nodes, id, node);
        updated = AppendCaptureEvidence(
            recording with { CurrentDestinationId = id, UpdatedAtUtc = now },
            id,
            kind,
            snapshot,
            now);
        error = string.Empty;
        return true;
    }

    private static LocalAppGraphRecording AppendCaptureEvidence(
        LocalAppGraphRecording recording,
        string destinationId,
        string kind,
        AppSessionSnapshot snapshot,
        DateTimeOffset now)
    {
        var visualTree = snapshot.VisualTreeSnapshots.OrderBy(candidate => candidate.CapturedAtUtc).LastOrDefault();
        var screenshot = snapshot.Images.OrderBy(candidate => candidate.CapturedAtUtc).LastOrDefault();
        recording.Evidence["captures"]!.AsArray().Add(new JsonObject
        {
            ["kind"] = kind,
            ["destinationId"] = destinationId,
            ["capturedAtUtc"] = now,
            ["visualTreeSnapshotId"] = visualTree?.SnapshotId,
            ["visualTreeHash"] = visualTree?.TreeHash,
            ["screenshotFrameId"] = visualTree?.ScreenshotFrameId ?? screenshot?.FrameId,
            ["screenshotHash"] = visualTree?.ScreenshotHash
        });
        return recording;
    }

    private static IReadOnlyList<string> CollectRecordingGaps(
        JsonObject definition,
        JsonObject? pendingTransition,
        bool strict)
    {
        var gaps = new List<string>();
        var nodes = definition["nodes"] as JsonArray ?? [];
        var edges = definition["edges"] as JsonArray ?? [];
        var nodesById = nodes.OfType<JsonObject>()
            .Where(node => !string.IsNullOrWhiteSpace(ReadString(node, "id")))
            .GroupBy(node => ReadString(node, "id")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (nodes.Count == 0)
        {
            gaps.Add("Record at least one destination.");
        }
        if (pendingTransition is not null)
        {
            gaps.Add("Complete the armed transition.");
        }
        foreach (var node in nodes.OfType<JsonObject>())
        {
            var id = ReadString(node, "id") ?? "unknown";
            var kind = ReadString(node, "kind");
            if (string.IsNullOrWhiteSpace(ReadString(node, "name"))
                || string.IsNullOrWhiteSpace(ReadString(node, "purpose")))
            {
                gaps.Add($"Destination '{id}' needs a name and purpose.");
            }
            if (kind == "state" && string.IsNullOrWhiteSpace(ReadString(node, "parentScreen")))
            {
                gaps.Add($"Internal state '{id}' needs a containing screen.");
            }
            if (kind == "dialog" && !string.IsNullOrWhiteSpace(ReadString(node, "parentScreen")))
            {
                gaps.Add($"Dialog '{id}' must remain top-level.");
            }
        }
        foreach (var edge in edges.OfType<JsonObject>())
        {
            var id = ReadString(edge, "id") ?? "unknown";
            var action = edge["action"] as JsonObject;
            if (!nodesById.ContainsKey(ReadString(edge, "from") ?? string.Empty)
                || !nodesById.ContainsKey(ReadString(edge, "to") ?? string.Empty))
            {
                gaps.Add($"Transition '{id}' references an unknown destination.");
            }
            if (string.IsNullOrWhiteSpace(ReadString(action, "automationId"))
                || string.IsNullOrWhiteSpace(ReadString(action, "semanticMeaning")))
            {
                gaps.Add($"Transition '{id}' needs a stable automation ID and semantic meaning.");
            }
        }

        var navigationHostChildren = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var host in (definition["navigationHosts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = ReadString(host, "id") ?? "unknown";
            var children = ReadIds(host["childDestinationIds"]);
            if (strict && children.Count < 2)
            {
                gaps.Add($"Navigation host '{id}' needs at least two child screens.");
            }
            if (!children.Contains(ReadString(host, "activeChildDestinationId") ?? string.Empty, StringComparer.Ordinal))
            {
                gaps.Add($"Navigation host '{id}' must include its active child.");
            }
            foreach (var child in children)
            {
                navigationHostChildren[child] = host;
            }
        }
        foreach (var node in nodesById.Values.Where(node => ReadString(node, "kind") == "screen"))
        {
            var parent = ReadString(node, "parentScreen");
            if (string.IsNullOrWhiteSpace(parent))
            {
                continue;
            }
            var id = ReadString(node, "id")!;
            if (!navigationHostChildren.TryGetValue(id, out var host)
                || !nodesById.TryGetValue(ReadString(host, "destinationId") ?? string.Empty, out var hostNode)
                || !string.Equals(ReadString(hostNode, "name"), parent, StringComparison.Ordinal))
            {
                gaps.Add($"Screen '{id}' identifies a parent without matching navigation-host membership.");
            }
        }
        foreach (var group in (definition["tabGroups"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = ReadString(group, "id") ?? "unknown";
            var tabs = ReadIds(group["tabDestinationIds"]);
            if (strict && tabs.Count < 2)
            {
                gaps.Add($"Tab group '{id}' needs at least two internal states.");
            }
            if (!tabs.Contains(ReadString(group, "selectedDestinationId") ?? string.Empty, StringComparer.Ordinal))
            {
                gaps.Add($"Tab group '{id}' must include its selected state.");
            }
            if (tabs.Any(tab => !nodesById.TryGetValue(tab, out var node) || ReadString(node, "kind") != "state"))
            {
                gaps.Add($"Tab group '{id}' may contain only internal state destinations.");
            }
        }
        return gaps.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static JsonObject CreateEmptyRecordingDefinition() => new()
    {
        ["schema"] = "ansight.app-graph/v1",
        ["nodes"] = new JsonArray(),
        ["edges"] = new JsonArray(),
        ["navigationHosts"] = new JsonArray(),
        ["tabGroups"] = new JsonArray()
    };

    private static JsonObject CreateRecordingEvidence(IReadOnlyList<CloudAppGraphBinding> bindings)
    {
        var candidates = new JsonArray();
        foreach (var binding in bindings.OrderBy(binding => binding.EdgeId).ThenBy(binding => binding.Priority))
        {
            candidates.Add(new JsonObject
            {
                ["edgeId"] = binding.EdgeId,
                ["mechanism"] = binding.Mechanism,
                ["configuration"] = binding.Configuration.DeepClone(),
                ["preconditions"] = new JsonArray(binding.Preconditions.Select(value => (JsonNode?)value).ToArray()),
                ["postconditions"] = new JsonArray(binding.Postconditions.Select(value => (JsonNode?)value).ToArray()),
                ["confidence"] = binding.Confidence
            });
        }
        return new JsonObject
        {
            ["schema"] = "ansight.app-graph-manual-recording-evidence/v1",
            ["captures"] = new JsonArray(),
            ["bindingCandidates"] = candidates
        };
    }

    private static void EnsureDefinitionCollections(JsonObject definition)
    {
        definition["schema"] = "ansight.app-graph/v1";
        definition["nodes"] ??= new JsonArray();
        definition["edges"] ??= new JsonArray();
        definition["navigationHosts"] ??= new JsonArray();
        definition["tabGroups"] ??= new JsonArray();
    }

    private static string MapGestureToAction(string? gestureKind) => gestureKind switch
    {
        "longPress" => "long_press",
        "drag" => "swipe",
        _ => "tap"
    };

    private static List<string> NormalizeIds(IEnumerable<string>? values)
        => (values ?? [])
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

    private static IReadOnlyList<string> ReadIds(JsonNode? value)
        => value is JsonArray array
            ? array.OfType<JsonValue>()
                .Select(item => item.TryGetValue<string>(out var text) ? text?.Trim() : null)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray()
            : [];

    private static JsonObject? FindNode(JsonObject definition, string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : (definition["nodes"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(node => string.Equals(ReadString(node, "id"), id.Trim(), StringComparison.Ordinal));

    private static string? ReadString(JsonObject? value, string property)
        => value?[property] is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
            ? text?.Trim()
            : null;

    private static int IndexOfId(JsonArray values, string id)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is JsonObject value
                && string.Equals(ReadString(value, "id"), id, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static void UpsertById(JsonArray values, string id, JsonObject value)
    {
        var index = IndexOfId(values, id);
        if (index >= 0)
        {
            values[index] = value;
        }
        else
        {
            values.Add(value);
        }
    }

    private static string CreateUniqueId(
        IEnumerable<string?> existingIds,
        string source,
        string fallback)
    {
        var existing = existingIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var slug = new string(source.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        slug = slug.Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = fallback;
        }
        var candidate = slug;
        var suffix = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"{slug}-{suffix++}";
        }
        return candidate;
    }

    private static LocalAppGraphIdentity? GetIdentityInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid graphId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT app_id, app_name FROM local_app_graphs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", graphId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new LocalAppGraphIdentity(graphId, reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static CloudAppGraphVersion? ReadVersionInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid versionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, graph_id, version_number, status, definition_json, merge_summary, created_utc
            FROM local_app_graph_versions WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", versionId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
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

    private static IReadOnlyList<CloudAppGraphBinding> ReadBindingsInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid versionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, edge_id, priority, mechanism, configuration_json,
                   preconditions_json, postconditions_json, confidence
            FROM local_app_graph_bindings
            WHERE version_id = $version_id
            ORDER BY edge_id, priority;
            """;
        command.Parameters.AddWithValue("$version_id", versionId.ToString("D"));
        using var reader = command.ExecuteReader();
        var bindings = new List<CloudAppGraphBinding>();
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

    private static void InsertManualRecordingObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAppGraphRecording recording,
        Guid graphId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_app_graph_observations (
                id, graph_id, source_session_id, source_app_id, audit_run_id, source_kind,
                candidate_definition_json, evidence_json, confidence, notes, created_utc)
            VALUES ($id, $graph_id, $session_id, $app_id, $audit_id, 'manual_recording',
                    $definition, $evidence, 1.0, $notes, $created_utc);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$graph_id", graphId.ToString("D"));
        command.Parameters.AddWithValue("$session_id", recording.SessionId);
        command.Parameters.AddWithValue("$app_id", recording.AppId);
        command.Parameters.AddWithValue("$audit_id", $"manual-recording/{recording.Id:D}");
        command.Parameters.AddWithValue("$definition", recording.Definition.ToJsonString());
        command.Parameters.AddWithValue("$evidence", recording.Evidence.ToJsonString());
        command.Parameters.AddWithValue("$notes", $"Recorded manually in local Host walkthrough {recording.Id:D}.");
        command.Parameters.AddWithValue("$created_utc", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static LocalAppGraphRecording? FindActiveRecording(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, session_id, app_id, app_name, graph_id, base_version_id,
                   committed_version_id, graph_name, intent, status,
                   current_destination_id, definition_json, evidence_json,
                   pending_transition_json, started_utc, updated_utc, stopped_utc
            FROM local_app_graph_recordings
            WHERE session_id = $session_id AND status IN ('recording', 'review')
            ORDER BY updated_utc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecording(reader) : null;
    }

    private static LocalAppGraphRecording? ReadRecording(
        SqliteConnection connection,
        Guid recordingId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, session_id, app_id, app_name, graph_id, base_version_id,
                   committed_version_id, graph_name, intent, status,
                   current_destination_id, definition_json, evidence_json,
                   pending_transition_json, started_utc, updated_utc, stopped_utc
            FROM local_app_graph_recordings WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", recordingId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecording(reader) : null;
    }

    private static LocalAppGraphRecording ReadRecording(SqliteDataReader reader)
    {
        var definition = JsonNode.Parse(reader.GetString(11))?.AsObject() ?? CreateEmptyRecordingDefinition();
        var evidence = JsonNode.Parse(reader.GetString(12))?.AsObject() ?? CreateRecordingEvidence([]);
        EnsureDefinitionCollections(definition);
        evidence["captures"] ??= new JsonArray();
        evidence["bindingCandidates"] ??= new JsonArray();
        var pending = reader.IsDBNull(13)
            ? null
            : JsonNode.Parse(reader.GetString(13))?.AsObject();
        return new LocalAppGraphRecording(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ReadGuid(reader, 4),
            ReadGuid(reader, 5),
            ReadGuid(reader, 6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            definition,
            evidence,
            pending,
            DateTimeOffset.Parse(reader.GetString(14)),
            DateTimeOffset.Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            CollectRecordingGaps(
                definition,
                pending,
                strict: string.Equals(reader.GetString(9), "review", StringComparison.Ordinal)));
    }

    private static void InsertRecording(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAppGraphRecording recording)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_app_graph_recordings (
                id, session_id, app_id, app_name, graph_id, base_version_id,
                committed_version_id, graph_name, intent, status,
                current_destination_id, definition_json, evidence_json,
                pending_transition_json, started_utc, updated_utc, stopped_utc)
            VALUES ($id, $session_id, $app_id, $app_name, $graph_id, $base_version_id,
                    $committed_version_id, $graph_name, $intent, $status,
                    $current_destination_id, $definition, $evidence,
                    $pending_transition, $started_utc, $updated_utc, $stopped_utc);
            """;
        BindRecording(command, recording);
        command.ExecuteNonQuery();
    }

    private static void WriteRecording(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAppGraphRecording recording)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_app_graph_recordings
            SET session_id = $session_id, app_id = $app_id, app_name = $app_name,
                graph_id = $graph_id, base_version_id = $base_version_id,
                committed_version_id = $committed_version_id, graph_name = $graph_name,
                intent = $intent, status = $status,
                current_destination_id = $current_destination_id,
                definition_json = $definition, evidence_json = $evidence,
                pending_transition_json = $pending_transition,
                started_utc = $started_utc, updated_utc = $updated_utc,
                stopped_utc = $stopped_utc
            WHERE id = $id;
            """;
        BindRecording(command, recording);
        command.ExecuteNonQuery();
    }

    private static void BindRecording(SqliteCommand command, LocalAppGraphRecording recording)
    {
        command.Parameters.AddWithValue("$id", recording.Id.ToString("D"));
        command.Parameters.AddWithValue("$session_id", recording.SessionId);
        command.Parameters.AddWithValue("$app_id", recording.AppId);
        command.Parameters.AddWithValue("$app_name", recording.AppName);
        command.Parameters.AddWithValue("$graph_id", recording.AppGraphId.HasValue ? recording.AppGraphId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$base_version_id", recording.BaseVersionId.HasValue ? recording.BaseVersionId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$committed_version_id", recording.CommittedVersionId.HasValue ? recording.CommittedVersionId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$graph_name", recording.GraphName);
        command.Parameters.AddWithValue("$intent", recording.Intent);
        command.Parameters.AddWithValue("$status", recording.Status);
        command.Parameters.AddWithValue("$current_destination_id", (object?)recording.CurrentDestinationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$definition", recording.Definition.ToJsonString());
        command.Parameters.AddWithValue("$evidence", recording.Evidence.ToJsonString());
        command.Parameters.AddWithValue("$pending_transition", recording.PendingTransition is null ? DBNull.Value : recording.PendingTransition.ToJsonString());
        command.Parameters.AddWithValue("$started_utc", recording.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_utc", recording.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$stopped_utc", recording.StoppedAtUtc.HasValue ? recording.StoppedAtUtc.Value.ToString("O") : DBNull.Value);
    }

    private sealed record RecordingMutation(
        bool IsSuccess,
        string Message,
        LocalAppGraphRecording? Recording)
    {
        public static RecordingMutation Success(LocalAppGraphRecording recording, string message)
            => new(true, message, recording);

        public static RecordingMutation Failure(string message)
            => new(false, message, null);
    }
}
