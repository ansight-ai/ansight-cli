using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Runtime.Operations;

internal static class PayloadJson
{
    public static JsonObject BuildDevicePayload(AppSessionSnapshot snapshot, bool isLive)
    {
        return new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["clientName"] = snapshot.ClientName,
            ["remoteAddress"] = snapshot.RemoteAddress,
            ["status"] = snapshot.Status,
            ["appState"] = SerializeAppLifecycleState(snapshot.AppState),
            ["appStateChangedUtc"] = snapshot.AppStateChangedUtc,
            ["isLive"] = isLive,
            ["createdUtc"] = snapshot.CreatedUtc,
            ["lastUpdatedUtc"] = snapshot.LastUpdatedUtc,
            ["sdkVersion"] = snapshot.SdkVersion,
            ["sdk"] = snapshot.DeviceProfile?.Sdk is null ? null : JsonSerializer.SerializeToNode(snapshot.DeviceProfile.Sdk, JsonUtil.Compact),
            ["device"] = snapshot.DeviceProfile?.Device is null ? null : JsonSerializer.SerializeToNode(snapshot.DeviceProfile.Device, JsonUtil.Compact),
            ["app"] = snapshot.DeviceProfile?.App is null ? null : JsonSerializer.SerializeToNode(snapshot.DeviceProfile.App, JsonUtil.Compact),
            ["runtime"] = snapshot.DeviceProfile?.Runtime is null ? null : JsonSerializer.SerializeToNode(snapshot.DeviceProfile.Runtime, JsonUtil.Compact),
            ["graphics"] = snapshot.DeviceProfile?.Graphics is null ? null : JsonSerializer.SerializeToNode(snapshot.DeviceProfile.Graphics, JsonUtil.Compact),
            ["permissions"] = snapshot.DeviceProfile?.Permissions is null ? null : JsonSerializer.SerializeToNode(snapshot.DeviceProfile.Permissions, JsonUtil.Compact),
            ["tags"] = snapshot.DeviceProfile?.Tags is null ? null : JsonSerializer.SerializeToNode(snapshot.DeviceProfile.Tags, JsonUtil.Compact),
            ["rawDeviceProfileJson"] = snapshot.DeviceProfileJson
        };
    }

    public static JsonObject BuildSessionPayload(AppSessionSnapshot snapshot, bool isLive)
    {
        return new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["clientName"] = snapshot.ClientName,
            ["remoteAddress"] = snapshot.RemoteAddress,
            ["status"] = snapshot.Status,
            ["appState"] = SerializeAppLifecycleState(snapshot.AppState),
            ["appStateChangedUtc"] = snapshot.AppStateChangedUtc,
            ["isLive"] = isLive,
            ["isHistorical"] = snapshot.IsHistorical,
            ["author"] = BuildAuthorPayload(snapshot.Author),
            ["createdUtc"] = snapshot.CreatedUtc,
            ["lastUpdatedUtc"] = snapshot.LastUpdatedUtc,
            ["configId"] = snapshot.ConfigId,
            ["analysisCount"] = snapshot.Analyses.Count,
            ["annotationCount"] = snapshot.Annotations.Count,
            ["imageCount"] = snapshot.Images.Count,
            ["visualTreeSnapshotCount"] = snapshot.VisualTreeSnapshots.Count,
            ["logCount"] = snapshot.Logs.Count,
            ["logStreamCount"] = snapshot.LogStreams.Count,
            ["logStreams"] = CreateJsonArray(snapshot.LogStreams.Select(stream => (JsonNode?)BuildSessionLogStreamDescriptorPayload(stream))),
            ["metricChannelCount"] = snapshot.MetricChannels.Count,
            ["metricSampleCount"] = snapshot.Metrics.Count,
            ["sdkVersion"] = snapshot.SdkVersion,
            ["appName"] = snapshot.DeviceProfile?.App?.AppName,
            ["appVersion"] = snapshot.DeviceProfile?.App?.VersionName,
            ["deviceName"] = snapshot.DeviceProfile?.Device?.Model,
            ["deviceFormFactor"] = snapshot.DeviceProfile?.Device?.FormFactor,
            ["osName"] = snapshot.DeviceProfile?.Device?.OsName,
            ["isVirtual"] = snapshot.DeviceProfile?.Device?.IsVirtual ?? snapshot.DeviceProfile?.Device?.IsEmulator,
            ["isEmulator"] = snapshot.DeviceProfile?.Device?.IsEmulator
        };
    }

    public static JsonObject BuildSessionSummaryResourcePayload(AppSessionSnapshot snapshot)
    {
        return new JsonObject
        {
            ["session"] = BuildSessionPayload(snapshot, isLive: !snapshot.IsHistorical),
            ["resourceUri"] = BuildSessionSummaryResourceUri(snapshot.SessionId)
        };
    }

    private static JsonObject? BuildAuthorPayload(SessionCaptureAuthorMetadata? author)
    {
        return author is null
            ? null
            : new JsonObject
            {
                ["email"] = author.Email,
                ["name"] = author.Name,
                ["company"] = author.Company
            };
    }

    public static JsonObject BuildSessionLogsResourcePayload(AppSessionSnapshot snapshot)
    {
        return new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["logCount"] = snapshot.Logs.Count,
            ["streamCount"] = snapshot.LogStreams.Count,
            ["streams"] = CreateJsonArray(snapshot.LogStreams.Select(stream => (JsonNode?)new JsonObject
            {
                ["stream"] = BuildSessionLogStreamDescriptorPayload(stream),
                ["logs"] = CreateJsonArray(stream.Entries
                    .OrderBy(log => log.TimestampUtc)
                    .Select(log => (JsonNode?)BuildLogPayload(log)))
            })),
            ["logs"] = CreateJsonArray(snapshot.Logs
                .OrderBy(log => log.TimestampUtc)
                .Select(log => (JsonNode?)BuildLogPayload(log)))
        };
    }

    private static JsonObject BuildSessionLogStreamDescriptorPayload(SessionLogStream stream)
        => new()
        {
            ["streamId"] = stream.StreamId,
            ["kind"] = stream.Kind,
            ["displayName"] = stream.DisplayName,
            ["status"] = stream.Status,
            ["statusMessage"] = stream.StatusMessage,
            ["startedUtc"] = stream.StartedUtc,
            ["endedUtc"] = stream.EndedUtc,
            ["entryCount"] = stream.Entries.Count,
            ["metadata"] = JsonSerializer.SerializeToNode(stream.Metadata, JsonUtil.Compact)
        };

    private static JsonObject BuildLogPayload(LogEntry log)
        => new()
        {
            ["streamId"] = log.StreamId,
            ["timestampUtc"] = log.TimestampUtc,
            ["priority"] = log.Priority.ToString(),
            ["source"] = log.Source,
            ["tag"] = log.Tag,
            ["eventId"] = log.EventId,
            ["processId"] = log.ProcessId,
            ["threadId"] = log.ThreadId,
            ["message"] = log.Message
        };

    public static JsonObject BuildSessionDeviceProfileResourcePayload(AppSessionSnapshot snapshot)
    {
        var payload = new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId
        };

        if (!string.IsNullOrWhiteSpace(snapshot.DeviceProfileJson))
        {
            try
            {
                payload["profile"] = JsonNode.Parse(snapshot.DeviceProfileJson);
            }
            catch
            {
                payload["profileJson"] = snapshot.DeviceProfileJson;
            }
        }
        else
        {
            payload["profile"] = snapshot.DeviceProfile is null
                ? null
                : JsonSerializer.SerializeToNode(snapshot.DeviceProfile, JsonUtil.Compact);
        }

        return payload;
    }

    public static JsonObject BuildSessionTelemetryResourcePayload(AppSessionSnapshot snapshot, string type)
    {
        var channelMap = snapshot.MetricChannels.ToDictionary(channel => channel.ChannelId);
        var normalizedType = type.Trim();
        var matchedSamples = string.Equals(normalizedType, "all", StringComparison.OrdinalIgnoreCase)
            ? snapshot.Metrics
            : snapshot.Metrics.Where(metric => string.Equals(
                    ResolveTelemetryType(metric.ChannelId, channelMap),
                    normalizedType,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var grouped = matchedSamples
            .OrderBy(metric => metric.CapturedAtUtc)
            .ThenBy(metric => metric.ChannelId)
            .GroupBy(metric => metric.ChannelId)
            .Select(group =>
            {
                channelMap.TryGetValue(group.Key, out var channel);
                return (JsonNode?)new JsonObject
                {
                    ["channelId"] = group.Key,
                    ["name"] = channel?.Name ?? $"Channel {group.Key}",
                    ["type"] = ResolveTelemetryType(group.Key, channelMap),
                    ["colorHex"] = channel?.ColorHex,
                    ["source"] = channel?.Source,
                    ["group"] = channel?.Group,
                    ["kind"] = channel?.Kind,
                    ["sampleCount"] = group.Count(),
                    ["samples"] = CreateJsonArray(group.Select(metric => (JsonNode?)new JsonObject
                    {
                        ["capturedAtUtc"] = metric.CapturedAtUtc,
                        ["value"] = metric.Value
                    }))
                };
            });

        return new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["requestedType"] = normalizedType,
            ["matchedSampleCount"] = matchedSamples.Count,
            ["telemetry"] = CreateJsonArray(grouped)
        };
    }

    public static JsonObject BuildSessionAnnotationsResourcePayload(AppSessionSnapshot snapshot)
    {
        return new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["annotationCount"] = snapshot.Annotations.Count,
            ["annotations"] = CreateJsonArray(snapshot.Annotations
                .OrderBy(annotation => annotation.StartUtc)
                .ThenBy(annotation => annotation.EndUtc ?? annotation.StartUtc)
                .Select(annotation => (JsonNode?)BuildSessionAnnotationPayload(annotation)))
        };
    }

    public static JsonObject BuildSessionVisualTreesResourcePayload(AppSessionSnapshot snapshot)
    {
        return new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["visualTreeSnapshotCount"] = snapshot.VisualTreeSnapshots.Count,
            ["visualTreeSnapshots"] = CreateJsonArray(snapshot.VisualTreeSnapshots
                .OrderBy(treeSnapshot => treeSnapshot.CapturedAtUtc)
                .ThenBy(treeSnapshot => treeSnapshot.SnapshotId, StringComparer.Ordinal)
                .Select(treeSnapshot => (JsonNode?)BuildSessionVisualTreeSnapshotSummaryPayload(snapshot, treeSnapshot)))
        };
    }

    public static JsonObject BuildSessionVisualTreeSnapshotSummaryPayload(
        AppSessionSnapshot session,
        SessionVisualTreeSnapshot snapshot)
    {
        return new JsonObject
        {
            ["snapshotId"] = snapshot.SnapshotId,
            ["capturedAtUtc"] = snapshot.CapturedAtUtc,
            ["visualTreeKind"] = snapshot.VisualTreeKind,
            ["visualTreeFormat"] = snapshot.VisualTreeFormat,
            ["runtimePlatform"] = snapshot.RuntimePlatform,
            ["source"] = snapshot.Source,
            ["rootScope"] = snapshot.RootScope,
            ["maxDepth"] = snapshot.MaxDepth,
            ["includeProperties"] = snapshot.IncludeProperties,
            ["includeBindableProperties"] = snapshot.IncludeBindableProperties,
            ["nodeCount"] = snapshot.NodeCount,
            ["truncated"] = snapshot.Truncated,
            ["screenshotFrameId"] = snapshot.ScreenshotFrameId,
            ["screenshotCapturedAtUtc"] = snapshot.ScreenshotCapturedAtUtc,
            ["actionId"] = snapshot.ActionId,
            ["actionCapability"] = snapshot.ActionCapability,
            ["evidencePhase"] = snapshot.EvidencePhase,
            ["treeHash"] = snapshot.TreeHash,
            ["screenshotHash"] = snapshot.ScreenshotHash,
            ["payloadResourceUri"] = BuildSessionVisualTreeSnapshotResourceUri(session.SessionId, snapshot.SnapshotId),
            ["rootSummary"] = BuildVisualTreeRootSummary(snapshot.Payload)
        };
    }

    public static JsonObject BuildSessionVisualTreeSnapshotPayload(
        AppSessionSnapshot session,
        SessionVisualTreeSnapshot snapshot)
    {
        var payload = BuildSessionVisualTreeSnapshotPayload(snapshot);
        payload["sessionId"] = session.SessionId;
        payload["appId"] = session.AppId;
        payload["resourceUri"] = BuildSessionVisualTreeSnapshotResourceUri(session.SessionId, snapshot.SnapshotId);
        return payload;
    }

    public static JsonObject BuildSessionVisualTreeSnapshotPayload(SessionVisualTreeSnapshot snapshot)
    {
        return new JsonObject
        {
            ["snapshotId"] = snapshot.SnapshotId,
            ["capturedAtUtc"] = snapshot.CapturedAtUtc,
            ["visualTreeKind"] = snapshot.VisualTreeKind,
            ["visualTreeFormat"] = snapshot.VisualTreeFormat,
            ["runtimePlatform"] = snapshot.RuntimePlatform,
            ["source"] = snapshot.Source,
            ["rootScope"] = snapshot.RootScope,
            ["maxDepth"] = snapshot.MaxDepth,
            ["includeProperties"] = snapshot.IncludeProperties,
            ["includeBindableProperties"] = snapshot.IncludeBindableProperties,
            ["nodeCount"] = snapshot.NodeCount,
            ["truncated"] = snapshot.Truncated,
            ["screenshotFrameId"] = snapshot.ScreenshotFrameId,
            ["screenshotCapturedAtUtc"] = snapshot.ScreenshotCapturedAtUtc,
            ["actionId"] = snapshot.ActionId,
            ["actionCapability"] = snapshot.ActionCapability,
            ["evidencePhase"] = snapshot.EvidencePhase,
            ["treeHash"] = snapshot.TreeHash,
            ["screenshotHash"] = snapshot.ScreenshotHash,
            ["payload"] = VisualTreePresentationNormalizer.Create(snapshot.Payload)
        };
    }

    private static JsonObject? BuildVisualTreeRootSummary(JsonObject payload)
    {
        if (payload["root"] is not JsonObject root)
        {
            return null;
        }

        var summary = new JsonObject();
        CopyVisualTreeSummaryProperty(root, summary, "id");
        CopyVisualTreeSummaryProperty(root, summary, "type");
        CopyVisualTreeSummaryProperty(root, summary, "kind");
        CopyVisualTreeSummaryProperty(root, summary, "label");
        CopyVisualTreeSummaryProperty(root, summary, "automationId");
        CopyVisualTreeSummaryProperty(root, summary, "childCount");
        return summary.Count == 0 ? null : summary;
    }

    private static void CopyVisualTreeSummaryProperty(JsonObject source, JsonObject target, string propertyName)
    {
        if (source[propertyName] is { } value)
        {
            target[propertyName] = value.DeepClone();
        }
    }

    public static string BuildSessionSummaryResourceUri(string sessionId)
        => $"ansight://sessions/{Uri.EscapeDataString(sessionId)}/summary";

    public static string BuildSessionAnnotationsResourceUri(string sessionId)
        => $"ansight://sessions/{Uri.EscapeDataString(sessionId)}/annotations";

    public static string BuildSessionVisualTreesResourceUri(string sessionId)
        => $"ansight://sessions/{Uri.EscapeDataString(sessionId)}/visual-trees";

    public static string BuildSessionVisualTreeSnapshotResourceUri(string sessionId, string snapshotId)
        => $"ansight://sessions/{Uri.EscapeDataString(sessionId)}/visual-trees/{Uri.EscapeDataString(snapshotId)}";

    public static string BuildSessionResourceName(AppSessionSnapshot snapshot)
    {
        var appName = snapshot.DeviceProfile?.App?.AppName ?? snapshot.ClientName;
        return $"{appName} ({snapshot.SessionId})";
    }

    public static JsonObject CreateToolDefinition(
        string name,
        string title,
        string description,
        JsonObject inputSchema)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["title"] = title,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
    }

    public static JsonObject CreateResourceDescriptor(
        string uri,
        string name,
        string description,
        string mimeType)
    {
        return new JsonObject
        {
            ["uri"] = uri,
            ["name"] = name,
            ["description"] = description,
            ["mimeType"] = mimeType
        };
    }

    public static JsonObject CreateResourceTemplateDescriptor(
        string uriTemplate,
        string name,
        string description,
        string mimeType)
    {
        return new JsonObject
        {
            ["uriTemplate"] = uriTemplate,
            ["name"] = name,
            ["description"] = description,
            ["mimeType"] = mimeType
        };
    }

    public static JsonObject CreateResourceContent(string uri, string mimeType, JsonObject payload)
    {
        return new JsonObject
        {
            ["uri"] = uri,
            ["mimeType"] = mimeType,
            ["text"] = JsonSerializer.Serialize(payload, JsonUtil.Pretty)
        };
    }

    public static JsonArray CreateJsonArray(IEnumerable<JsonNode?> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    public static bool MatchesTimestamp(DateTimeOffset timestampUtc, DateTimeOffset? startUtc, DateTimeOffset? endUtc)
    {
        return (!startUtc.HasValue || timestampUtc >= startUtc.Value)
               && (!endUtc.HasValue || timestampUtc <= endUtc.Value);
    }

    public static bool MatchesMinimumVerbosity(LogPriority priority, LogPriority minimumVerbosity)
    {
        if (priority == LogPriority.Unknown)
        {
            return false;
        }

        return priority >= minimumVerbosity;
    }

    public static bool MatchesChannelNameFilter(
        byte channelId,
        IReadOnlyDictionary<byte, SessionMetricChannel> channelMap,
        IReadOnlySet<string> channelNameFilters)
    {
        return channelMap.TryGetValue(channelId, out var channel)
               && !string.IsNullOrWhiteSpace(channel.Name)
               && channelNameFilters.Contains(channel.Name.Trim());
    }

    public static string ResolveTelemetryType(byte channelId, IReadOnlyDictionary<byte, SessionMetricChannel> channelMap)
        => MetricChannelClassification.ResolveTelemetryType(channelId, channelMap);

    public static JsonObject BuildSessionAnnotationPayload(SessionAnnotation annotation)
    {
        var effectiveEndUtc = annotation.EndUtc ?? annotation.StartUtc;
        return new JsonObject
        {
            ["annotationId"] = annotation.AnnotationId,
            ["label"] = annotation.Label,
            ["source"] = annotation.Source,
            ["notes"] = annotation.Notes,
            ["status"] = annotation.Status,
            ["captureGroupId"] = annotation.CaptureGroupId,
            ["customData"] = annotation.CustomData?.DeepClone(),
            ["hookFailures"] = CreateJsonArray(annotation.HookFailures.Select(failure => (JsonNode?)failure)),
            ["evidence"] = CreateJsonArray(annotation.Evidence.Select(item => (JsonNode?)new JsonObject
            {
                ["id"] = item.Id,
                ["kind"] = item.Kind,
                ["status"] = item.Status,
                ["reason"] = item.Reason,
                ["capturedAtUtc"] = item.CapturedAtUtc,
                ["sizeBytes"] = item.SizeBytes,
                ["truncated"] = item.Truncated
            })),
            ["startUtc"] = annotation.StartUtc,
            ["endUtc"] = annotation.EndUtc,
            ["effectiveEndUtc"] = effectiveEndUtc,
            ["isTimespan"] = annotation.EndUtc.HasValue && annotation.EndUtc.Value > annotation.StartUtc,
            ["geometryCount"] = annotation.Geometry.Count,
            ["target"] = annotation.Target is null ? null : BuildSessionAnnotationTargetPayload(annotation.Target),
            ["geometries"] = CreateJsonArray(annotation.Geometry
                .OrderBy(geometry => geometry.CapturedAtUtc)
                .ThenBy(geometry => geometry.GeometryId, StringComparer.Ordinal)
                .Select(geometry => (JsonNode?)BuildSessionAnnotationGeometryPayload(geometry)))
        };
    }

    public static JsonObject BuildSessionAnnotationTargetPayload(SessionAnnotationTarget target)
    {
        return new JsonObject
        {
            ["kind"] = target.Kind,
            ["source"] = target.Source,
            ["targetId"] = target.TargetId,
            ["visualTreeSnapshotId"] = target.VisualTreeSnapshotId,
            ["type"] = target.Type,
            ["elementKind"] = target.ElementKind,
            ["label"] = target.Label,
            ["automationId"] = target.AutomationId,
            ["depth"] = target.Depth,
            ["childCount"] = target.ChildCount,
            ["absoluteBounds"] = target.AbsoluteBounds is null ? null : BuildSessionAnnotationTargetBoundsPayload(target.AbsoluteBounds),
            ["normalizedBounds"] = target.NormalizedBounds is null ? null : BuildSessionAnnotationTargetBoundsPayload(target.NormalizedBounds)
        };
    }

    public static JsonObject BuildSessionAnnotationTargetBoundsPayload(SessionAnnotationTargetBounds bounds)
    {
        return new JsonObject
        {
            ["x"] = bounds.X,
            ["y"] = bounds.Y,
            ["width"] = bounds.Width,
            ["height"] = bounds.Height
        };
    }

    public static JsonObject BuildSessionAnnotationGeometryPayload(SessionAnnotationGeometry geometry)
    {
        return new JsonObject
        {
            ["geometryId"] = geometry.GeometryId,
            ["frameId"] = geometry.FrameId,
            ["capturedAtUtc"] = geometry.CapturedAtUtc,
            ["kind"] = geometry.Kind == SessionAnnotationGeometryKind.FreeDraw
                ? "freeDraw"
                : geometry.Kind.ToString().ToLowerInvariant(),
            ["x"] = geometry.X,
            ["y"] = geometry.Y,
            ["width"] = geometry.Width,
            ["height"] = geometry.Height,
            ["points"] = CreateJsonArray(geometry.Points.Select(point => (JsonNode?)new JsonObject
            {
                ["x"] = point.X,
                ["y"] = point.Y
            })),
            ["text"] = geometry.Text,
            ["strokeColor"] = geometry.StrokeColor,
            ["strokeWidth"] = geometry.StrokeWidth,
            ["inferredShape"] = BuildInferredShapePayload(geometry)
        };
    }

    private static JsonObject? BuildInferredShapePayload(SessionAnnotationGeometry geometry)
    {
        const double maximumInferenceError = 0.12d;
        const double maximumClosureDistance = 0.35d;

        if (geometry.Kind != SessionAnnotationGeometryKind.FreeDraw || geometry.Points.Count < 2)
        {
            return null;
        }

        var x = geometry.Points.Min(point => point.X);
        var y = geometry.Points.Min(point => point.Y);
        var width = geometry.Points.Max(point => point.X) - x;
        var height = geometry.Points.Max(point => point.Y) - y;
        if (width <= 0d && height <= 0d)
        {
            return null;
        }

        var arrowInference = TryBuildArrowInferencePayload(geometry.Points, x, y, width, height);
        if (arrowInference is not null)
        {
            return arrowInference;
        }

        var lineInference = TryBuildLineInferencePayload(geometry.Points, x, y, width, height);
        if (lineInference is not null)
        {
            return lineInference;
        }

        if (geometry.Points.Count < 5 || width <= 0d || height <= 0d)
        {
            return null;
        }

        var first = geometry.Points[0];
        var last = geometry.Points[^1];
        var closureX = (last.X - first.X) / width;
        var closureY = (last.Y - first.Y) / height;
        if (Math.Sqrt((closureX * closureX) + (closureY * closureY)) > maximumClosureDistance)
        {
            return null;
        }

        var rectangleError = 0d;
        var ellipseError = 0d;
        foreach (var point in geometry.Points)
        {
            var normalizedX = Math.Clamp((point.X - x) / width, 0d, 1d);
            var normalizedY = Math.Clamp((point.Y - y) / height, 0d, 1d);
            rectangleError += Math.Min(
                Math.Min(normalizedX, 1d - normalizedX),
                Math.Min(normalizedY, 1d - normalizedY));

            var ellipseX = (normalizedX - 0.5d) * 2d;
            var ellipseY = (normalizedY - 0.5d) * 2d;
            ellipseError += Math.Abs(Math.Sqrt((ellipseX * ellipseX) + (ellipseY * ellipseY)) - 1d) * 0.5d;
        }

        rectangleError /= geometry.Points.Count;
        ellipseError /= geometry.Points.Count;
        var inferredKind = ellipseError < rectangleError ? "oval" : "rectangle";
        var inferenceError = Math.Min(rectangleError, ellipseError);
        if (inferenceError > maximumInferenceError)
        {
            return null;
        }

        return CreateInferredShapePayload(inferredKind, x, y, width, height, Math.Clamp(
            1d - (inferenceError / maximumInferenceError),
            0d,
            1d));
    }

    private static JsonObject? TryBuildLineInferencePayload(
        IReadOnlyList<SessionAnnotationGeometryPoint> points,
        double x,
        double y,
        double width,
        double height)
    {
        const double minimumLineLength = 0.03d;
        const double maximumAverageDeviation = 0.08d;
        const double maximumPointDeviation = 0.16d;
        const double maximumPathStretch = 0.2d;

        var first = points[0];
        var last = points[^1];
        var lineLength = CalculateDistance(first, last);
        if (lineLength < minimumLineLength)
        {
            return null;
        }

        var pathLength = 0d;
        var totalDeviation = 0d;
        var greatestDeviation = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            if (index > 0)
            {
                pathLength += CalculateDistance(points[index - 1], points[index]);
            }

            var deviation = CalculateDistanceToSegment(points[index], first, last) / lineLength;
            totalDeviation += deviation;
            greatestDeviation = Math.Max(greatestDeviation, deviation);
        }

        var averageDeviation = totalDeviation / points.Count;
        var pathStretch = Math.Max(0d, (pathLength / lineLength) - 1d);
        if (averageDeviation > maximumAverageDeviation
            || greatestDeviation > maximumPointDeviation
            || pathStretch > maximumPathStretch)
        {
            return null;
        }

        var inferenceError = Math.Max(
            averageDeviation / maximumAverageDeviation,
            Math.Max(
                greatestDeviation / maximumPointDeviation,
                pathStretch / maximumPathStretch));
        return CreateInferredShapePayload(
            "line",
            x,
            y,
            width,
            height,
            Math.Clamp(1d - inferenceError, 0d, 1d));
    }

    private static JsonObject? TryBuildArrowInferencePayload(
        IReadOnlyList<SessionAnnotationGeometryPoint> points,
        double x,
        double y,
        double width,
        double height)
    {
        if (points.Count < 5)
        {
            return null;
        }

        var forwardInference = FindArrowInference(points);
        var reverseInference = FindArrowInference(points.Reverse().ToArray());
        var inference = forwardInference is null || reverseInference?.Confidence > forwardInference.Confidence
            ? reverseInference
            : forwardInference;
        if (inference is null)
        {
            return null;
        }

        return CreateInferredShapePayload(
            "arrow",
            x,
            y,
            width,
            height,
            inference.Confidence,
            inference.FocalX,
            inference.FocalY);
    }

    private static ArrowInference? FindArrowInference(IReadOnlyList<SessionAnnotationGeometryPoint> points)
    {
        ArrowInference? bestInference = null;
        for (var focalIndex = 1; focalIndex <= points.Count - 3; focalIndex++)
        {
            var candidate = EvaluateArrowInference(points, focalIndex);
            if (candidate is not null && candidate.Confidence > (bestInference?.Confidence ?? -1d))
            {
                bestInference = candidate;
            }
        }

        return bestInference;
    }

    private static ArrowInference? EvaluateArrowInference(
        IReadOnlyList<SessionAnnotationGeometryPoint> points,
        int focalIndex)
    {
        const double minimumShaftLength = 0.05d;
        const double maximumAverageShaftDeviation = 0.06d;
        const double maximumShaftPointDeviation = 0.12d;
        const double maximumShaftStretch = 0.2d;
        const double minimumWingLengthRatio = 0.06d;
        const double maximumWingLengthRatio = 0.5d;
        const double minimumBackwardProjectionRatio = 0.2d;
        const double maximumBackwardProjectionRatio = 0.98d;
        const double minimumLateralRatio = 0.2d;
        const double minimumWingSymmetry = 0.35d;

        var shaftStart = points[0];
        var focalPoint = points[focalIndex];
        var shaftX = focalPoint.X - shaftStart.X;
        var shaftY = focalPoint.Y - shaftStart.Y;
        var shaftLength = Math.Sqrt((shaftX * shaftX) + (shaftY * shaftY));
        if (shaftLength < minimumShaftLength)
        {
            return null;
        }

        var unitX = shaftX / shaftLength;
        var unitY = shaftY / shaftLength;
        var shaftPathLength = 0d;
        var totalShaftDeviation = 0d;
        var greatestShaftDeviation = 0d;
        var previousProgress = 0d;
        for (var index = 0; index <= focalIndex; index++)
        {
            var point = points[index];
            if (index > 0)
            {
                shaftPathLength += CalculateDistance(points[index - 1], point);
            }

            var pointX = point.X - shaftStart.X;
            var pointY = point.Y - shaftStart.Y;
            var progress = ((pointX * unitX) + (pointY * unitY)) / shaftLength;
            if (progress < -0.05d || progress > 1.05d || progress + 0.08d < previousProgress)
            {
                return null;
            }

            previousProgress = Math.Max(previousProgress, progress);
            var deviation = Math.Abs((unitX * pointY) - (unitY * pointX)) / shaftLength;
            totalShaftDeviation += deviation;
            greatestShaftDeviation = Math.Max(greatestShaftDeviation, deviation);
        }

        var averageShaftDeviation = totalShaftDeviation / (focalIndex + 1);
        var shaftStretch = Math.Max(0d, (shaftPathLength / shaftLength) - 1d);
        if (averageShaftDeviation > maximumAverageShaftDeviation
            || greatestShaftDeviation > maximumShaftPointDeviation
            || shaftStretch > maximumShaftStretch)
        {
            return null;
        }

        var positiveWingLength = 0d;
        var negativeWingLength = 0d;
        var invalidHeadPointCount = 0;
        for (var index = focalIndex + 1; index < points.Count; index++)
        {
            var pointX = points[index].X - focalPoint.X;
            var pointY = points[index].Y - focalPoint.Y;
            var pointLength = Math.Sqrt((pointX * pointX) + (pointY * pointY));
            var pointLengthRatio = pointLength / shaftLength;
            if (pointLengthRatio < minimumWingLengthRatio)
            {
                continue;
            }

            if (pointLengthRatio > maximumWingLengthRatio)
            {
                invalidHeadPointCount++;
                continue;
            }

            var backwardProjectionRatio = -((pointX * unitX) + (pointY * unitY)) / pointLength;
            var lateral = (unitX * pointY) - (unitY * pointX);
            var lateralRatio = Math.Abs(lateral) / pointLength;
            if (backwardProjectionRatio < minimumBackwardProjectionRatio
                || backwardProjectionRatio > maximumBackwardProjectionRatio
                || lateralRatio < minimumLateralRatio)
            {
                invalidHeadPointCount++;
                continue;
            }

            if (lateral > 0d)
            {
                positiveWingLength = Math.Max(positiveWingLength, pointLength);
            }
            else
            {
                negativeWingLength = Math.Max(negativeWingLength, pointLength);
            }
        }

        var headPointCount = points.Count - focalIndex - 1;
        if (positiveWingLength <= 0d
            || negativeWingLength <= 0d
            || invalidHeadPointCount > Math.Max(2, headPointCount / 3))
        {
            return null;
        }

        var wingSymmetry = Math.Min(positiveWingLength, negativeWingLength)
                           / Math.Max(positiveWingLength, negativeWingLength);
        if (wingSymmetry < minimumWingSymmetry)
        {
            return null;
        }

        var shaftError = Math.Max(
            averageShaftDeviation / maximumAverageShaftDeviation,
            Math.Max(
                greatestShaftDeviation / maximumShaftPointDeviation,
                shaftStretch / maximumShaftStretch));
        var invalidHeadFraction = (double)invalidHeadPointCount / headPointCount;
        return new ArrowInference
        {
            FocalX = focalPoint.X,
            FocalY = focalPoint.Y,
            Confidence = Math.Clamp(
                1d - ((shaftError * 0.55d)
                      + ((1d - wingSymmetry) * 0.25d)
                      + (invalidHeadFraction * 0.2d)),
                0d,
                1d)
        };
    }

    private static JsonObject CreateInferredShapePayload(
        string kind,
        double x,
        double y,
        double width,
        double height,
        double confidence,
        double? focalX = null,
        double? focalY = null)
    {
        var payload = new JsonObject
        {
            ["kind"] = kind,
            ["bounds"] = new JsonObject
            {
                ["x"] = x,
                ["y"] = y,
                ["width"] = width,
                ["height"] = height
            },
            ["confidence"] = confidence
        };

        if (focalX.HasValue && focalY.HasValue)
        {
            payload["focalPoint"] = new JsonObject
            {
                ["x"] = focalX.Value,
                ["y"] = focalY.Value
            };
        }

        return payload;
    }

    private static double CalculateDistance(
        SessionAnnotationGeometryPoint first,
        SessionAnnotationGeometryPoint second)
    {
        var x = second.X - first.X;
        var y = second.Y - first.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static double CalculateDistanceToSegment(
        SessionAnnotationGeometryPoint point,
        SessionAnnotationGeometryPoint segmentStart,
        SessionAnnotationGeometryPoint segmentEnd)
    {
        var segmentX = segmentEnd.X - segmentStart.X;
        var segmentY = segmentEnd.Y - segmentStart.Y;
        var segmentLengthSquared = (segmentX * segmentX) + (segmentY * segmentY);
        if (segmentLengthSquared <= 0d)
        {
            return CalculateDistance(point, segmentStart);
        }

        var pointX = point.X - segmentStart.X;
        var pointY = point.Y - segmentStart.Y;
        var projection = Math.Clamp(
            ((pointX * segmentX) + (pointY * segmentY)) / segmentLengthSquared,
            0d,
            1d);
        var closestX = segmentStart.X + (projection * segmentX);
        var closestY = segmentStart.Y + (projection * segmentY);
        var distanceX = point.X - closestX;
        var distanceY = point.Y - closestY;
        return Math.Sqrt((distanceX * distanceX) + (distanceY * distanceY));
    }

    private sealed class ArrowInference
    {
        public required double FocalX { get; init; }

        public required double FocalY { get; init; }

        public required double Confidence { get; init; }
    }

    private static string SerializeAppLifecycleState(AppLifecycleState state)
    {
        return state switch
        {
            AppLifecycleState.Foreground => "foreground",
            AppLifecycleState.Background => "background",
            _ => "unknown"
        };
    }

}
