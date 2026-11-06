namespace Ansight.Host.Telemetry;

using Ansight.Host;

public static class LocalSessionCaptureAnalytics
{
    // Only captured evidence establishes a new capture; annotations/AI review do not.
    public static bool HasCapturedEvidence(AppSessionSnapshot snapshot)
    {
        var hasTelemetry = Math.Max(snapshot.TotalLogCount, snapshot.Logs.Count) > 0
            || Math.Max(snapshot.TotalImageCount, snapshot.Images.Count) > 0
            || Math.Max(snapshot.TotalMetricSampleCount, snapshot.Metrics.Count) > 0
            || Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count) > 0
            || Math.Max(snapshot.TotalApplicationEventCount, snapshot.ApplicationEvents.Count) > 0;

        if (hasTelemetry)
        {
            return true;
        }

        var hasInteractionEvidence = snapshot.Touches.Count > 0
            || snapshot.VisualTreeSnapshots.Count > 0
            || snapshot.ArtifactSnapshots.Count > 0;

        if (hasInteractionEvidence)
        {
            return true;
        }

        return snapshot.VideoRecordings.Any(recording => recording.ByteSize > 0 && recording.DurationUs > 0);
    }

    public const string CompletedEventName = "local_session_capture_completed";

    public static bool ShouldReportCompletion(string? previousStatus, AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsHistorical || !IsCaptureCompletionStatus(snapshot.Status))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(previousStatus) || IsRecordingStatus(previousStatus);
    }

    public static bool ShouldReportActivation(string? previousStatus, AppSessionSnapshot snapshot)
        => ShouldReportRecordedCapture(previousStatus, snapshot)
            && CreateCompletionProperties(snapshot)["hasEvidence"] is true;

    // A saved recording may end through a transport failure. Count the observed
    // live-to-historical transition, while excluding imports and connection attempts.
    public static bool ShouldReportRecordedCapture(string? previousStatus, AppSessionSnapshot snapshot)
    {
        var wasLiveSession = previousStatus is "Connected" or "WebSocket Open";

        if (!wasLiveSession || !snapshot.IsHistorical)
        {
            return false;
        }

        var hasEndedCapture = IsCaptureCompletionStatus(snapshot.Status)
            || snapshot.Status is "WebSocket Error"
            || snapshot.Status is "WebSocket Timeout"
            || snapshot.Status is "Sign In Required";

        return hasEndedCapture;
    }

    public static string CaptureUsageOutcome(AppSessionSnapshot snapshot)
        => snapshot.Status is "WebSocket Error" or "WebSocket Timeout" ? "failed"
            : snapshot.Status == "Sign In Required" ? "blocked" : "succeeded";

    public static IReadOnlyDictionary<string, object?> CreateCompletionProperties(AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var logCount = Math.Max(snapshot.TotalLogCount, snapshot.Logs.Count);
        var annotationCount = Math.Max(snapshot.TotalAnnotationCount, snapshot.Annotations.Count);
        var imageCount = Math.Max(snapshot.TotalImageCount, snapshot.Images.Count);
        var metricSampleCount = Math.Max(snapshot.TotalMetricSampleCount, snapshot.Metrics.Count);
        var duration = snapshot.LastUpdatedUtc - snapshot.CreatedUtc;
        var durationSeconds = Math.Max(0d, duration.TotalSeconds);
        var touchCount = snapshot.Touches.Count;
        var visualTreeCount = snapshot.VisualTreeSnapshots.Count;
        var artifactCount = snapshot.ArtifactSnapshots.Count;
        var analysisCount = snapshot.Analyses.Count;
        var hasEvidence = Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count) > 0
            || logCount > 0
            || annotationCount > 0
            || imageCount > 0
            || metricSampleCount > 0
            || touchCount > 0
            || visualTreeCount > 0
            || artifactCount > 0
            || analysisCount > 0;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["captureScope"] = "local",
            ["completionOutcome"] = ResolveCompletionOutcome(snapshot.Status),
            ["status"] = snapshot.Status,
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["processSessionId"] = snapshot.ProcessSessionId,
            ["sdkVersion"] = snapshot.SdkVersion,
            ["durationSeconds"] = durationSeconds,
            ["logCount"] = logCount,
            ["annotationCount"] = annotationCount,
            ["imageCount"] = imageCount,
            ["metricSampleCount"] = metricSampleCount,
            ["touchCount"] = touchCount,
            ["visualTreeCount"] = visualTreeCount,
            ["artifactCount"] = artifactCount,
            ["analysisCount"] = analysisCount,
            ["hasEvidence"] = hasEvidence
        };
    }

    private static bool IsRecordingStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var normalizedStatus = status.Trim();

        return string.Equals(normalizedStatus, "Connected", StringComparison.OrdinalIgnoreCase)
            || normalizedStatus.Contains("open", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaptureCompletionStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || status.Contains("closed", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCompletionOutcome(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "unknown";
        }

        if (status.Contains("complete", StringComparison.OrdinalIgnoreCase))
        {
            return "completed";
        }

        if (status.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "timed_out";
        }

        var hasFailureStatus = status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Contains("sign in required", StringComparison.OrdinalIgnoreCase)
            || status.Contains("rejected", StringComparison.OrdinalIgnoreCase);

        if (hasFailureStatus)
        {
            return "failed";
        }

        if (status.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            return "disconnected";
        }

        return "terminal";
    }
}
