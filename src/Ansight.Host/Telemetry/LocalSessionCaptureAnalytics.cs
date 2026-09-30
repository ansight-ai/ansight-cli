namespace Ansight.Host.Telemetry;

using Ansight.Host;

public static class LocalSessionCaptureAnalytics
{
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
        => previousStatus is "Connected" or "WebSocket Open"
           && snapshot.IsHistorical
           && (IsCaptureCompletionStatus(snapshot.Status)
               || snapshot.Status is "WebSocket Error" or "WebSocket Timeout" or "Sign In Required");

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
            ["hasEvidence"] = Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count) > 0
                              || logCount > 0
                              || annotationCount > 0
                              || imageCount > 0
                              || metricSampleCount > 0
                              || touchCount > 0
                              || visualTreeCount > 0
                              || artifactCount > 0
                              || analysisCount > 0
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

        if (status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Contains("sign in required", StringComparison.OrdinalIgnoreCase)
            || status.Contains("rejected", StringComparison.OrdinalIgnoreCase))
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
