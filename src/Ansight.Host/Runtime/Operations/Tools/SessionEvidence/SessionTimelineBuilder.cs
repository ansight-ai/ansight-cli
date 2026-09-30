using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class SessionTimelineBuilder
{
    public static IReadOnlyList<SessionTimelineEvent> BuildEvents(
        AppSessionSnapshot snapshot,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc)
    {
        var events = new List<SessionTimelineEvent>();
        var sequence = 0;

        AddEvent(
            events,
            snapshot.CreatedUtc,
            sequence++,
            "session",
            "created",
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["clientName"] = snapshot.ClientName
            },
            startUtc,
            endUtc);

        foreach (var transition in SessionLifecycleTransitionResolver.Build(snapshot))
        {
            AddEvent(
                events,
                transition.TimestampUtc,
                sequence++,
                "appState",
                "changed",
                new JsonObject
                {
                    ["appState"] = transition.State.ToString()
                },
                startUtc,
                endUtc);
        }

        foreach (var appEvent in snapshot.ApplicationEvents)
        {
            AddEvent(
                events,
                appEvent.CapturedAtUtc,
                sequence++,
                "applicationEvent",
                appEvent.EventType,
                new JsonObject
                {
                    ["eventId"] = appEvent.EventId,
                    ["label"] = appEvent.Label,
                    ["eventType"] = appEvent.EventType,
                    ["details"] = appEvent.Details,
                    ["channelId"] = appEvent.ChannelId
                },
                startUtc,
                endUtc);
        }

        foreach (var log in snapshot.Logs)
        {
            AddEvent(
                events,
                log.TimestampUtc,
                sequence++,
                "log",
                log.Priority.ToString(),
                new JsonObject
                {
                    ["priority"] = log.Priority.ToString(),
                    ["source"] = log.Source,
                    ["tag"] = log.Tag,
                    ["eventId"] = log.EventId,
                    ["message"] = log.Message
                },
                startUtc,
                endUtc);
        }

        foreach (var request in snapshot.NetworkRequests)
        {
            AddEvent(
                events,
                request.StartedAtUtc,
                sequence++,
                "networkRequest",
                "captured",
                new JsonObject
                {
                    ["requestId"] = request.Id,
                    ["method"] = request.Method,
                    ["url"] = request.Url,
                    ["startedAtUtc"] = request.StartedAtUtc,
                    ["completedAtUtc"] = request.CompletedAtUtc,
                    ["durationMilliseconds"] = request.DurationMilliseconds,
                    ["statusCode"] = request.StatusCode,
                    ["errorType"] = request.ErrorType,
                    ["errorMessage"] = request.ErrorMessage
                },
                startUtc,
                endUtc);
        }

        foreach (var frame in snapshot.Images)
        {
            AddEvent(
                events,
                frame.CapturedAtUtc,
                sequence++,
                "screenshot",
                "captured",
                SessionEvidencePayloads.BuildScreenshotFramePayload(snapshot, frame),
                startUtc,
                endUtc);
        }

        foreach (var touch in snapshot.Touches)
        {
            AddEvent(
                events,
                touch.CapturedAtUtc,
                sequence++,
                "touch",
                TouchReviewGeometry.NormalizeTouchAction(touch.Action),
                TouchReviewPayloads.BuildTouchPayload(touch),
                startUtc,
                endUtc);
        }

        foreach (var annotation in snapshot.Annotations)
        {
            AddEvent(
                events,
                annotation.StartUtc,
                sequence++,
                "annotation",
                "start",
                PayloadJson.BuildSessionAnnotationPayload(annotation),
                startUtc,
                endUtc);
        }

        foreach (var visualTree in snapshot.VisualTreeSnapshots)
        {
            AddEvent(
                events,
                visualTree.CapturedAtUtc,
                sequence++,
                "visualTree",
                "captured",
                PayloadJson.BuildSessionVisualTreeSnapshotSummaryPayload(snapshot, visualTree),
                startUtc,
                endUtc);
        }

        foreach (var actionGroup in snapshot.VisualTreeSnapshots
                     .Where(visualTree => !string.IsNullOrWhiteSpace(visualTree.ActionId))
                     .GroupBy(visualTree => visualTree.ActionId!, StringComparer.Ordinal))
        {
            var orderedEvidence = actionGroup
                .OrderBy(visualTree => visualTree.CapturedAtUtc)
                .ToArray();
            var before = orderedEvidence.FirstOrDefault(visualTree =>
                string.Equals(visualTree.EvidencePhase, "before", StringComparison.OrdinalIgnoreCase));
            var after = orderedEvidence.FirstOrDefault(visualTree =>
                string.Equals(visualTree.EvidencePhase, "after", StringComparison.OrdinalIgnoreCase));
            var startedUtc = before?.CapturedAtUtc ?? orderedEvidence[0].CapturedAtUtc;
            AddEvent(
                events,
                startedUtc,
                sequence++,
                "uiAction",
                before?.ActionCapability ?? after?.ActionCapability ?? "ui.action",
                new JsonObject
                {
                    ["actionId"] = actionGroup.Key,
                    ["capability"] = before?.ActionCapability ?? after?.ActionCapability,
                    ["startedAtUtc"] = startedUtc,
                    ["completedAtUtc"] = after?.CapturedAtUtc,
                    ["beforeVisualTreeSnapshotId"] = before?.SnapshotId,
                    ["beforeTreeHash"] = before?.TreeHash,
                    ["beforeScreenshotFrameId"] = before?.ScreenshotFrameId,
                    ["beforeScreenshotHash"] = before?.ScreenshotHash,
                    ["afterVisualTreeSnapshotId"] = after?.SnapshotId,
                    ["afterTreeHash"] = after?.TreeHash,
                    ["afterScreenshotFrameId"] = after?.ScreenshotFrameId,
                    ["afterScreenshotHash"] = after?.ScreenshotHash
                },
                startUtc,
                endUtc);
        }

        var channelMap = snapshot.MetricChannels.ToDictionary(channel => channel.ChannelId);
        foreach (var metric in snapshot.Metrics)
        {
            AddEvent(
                events,
                metric.CapturedAtUtc,
                sequence++,
                "telemetry",
                PayloadJson.ResolveTelemetryType(metric.ChannelId, channelMap),
                SessionEvidencePayloads.BuildMetricSamplePayload(metric, channelMap),
                startUtc,
                endUtc);
        }

        foreach (var artifactSnapshot in snapshot.ArtifactSnapshots)
        {
            AddEvent(
                events,
                artifactSnapshot.CapturedAtUtc,
                sequence++,
                "artifactSnapshot",
                artifactSnapshot.Kind,
                SessionEvidencePayloads.BuildArtifactSnapshotPayload(artifactSnapshot),
                startUtc,
                endUtc);
        }

        return events
            .OrderBy(timelineEvent => timelineEvent.TimestampUtc)
            .ThenBy(timelineEvent => timelineEvent.Sequence)
            .ToArray();
    }

    private static void AddEvent(
        ICollection<SessionTimelineEvent> events,
        DateTimeOffset timestampUtc,
        int sequence,
        string category,
        string kind,
        JsonObject details,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc)
    {
        var normalizedTimestampUtc = timestampUtc.ToUniversalTime();
        if (!PayloadJson.MatchesTimestamp(normalizedTimestampUtc, startUtc, endUtc))
        {
            return;
        }

        events.Add(new SessionTimelineEvent
        {
            TimestampUtc = normalizedTimestampUtc,
            Sequence = sequence,
            Payload = new JsonObject
            {
                ["eventId"] = $"{category}:{normalizedTimestampUtc.UtcTicks}:{sequence}",
                ["timestampUtc"] = normalizedTimestampUtc,
                ["category"] = category,
                ["kind"] = kind,
                ["details"] = details
            }
        });
    }
}
