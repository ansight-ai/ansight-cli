using Ansight.Infrastructure.Analytics;

namespace Ansight.Host.Runtime.SessionCaptureStorage;

internal sealed partial class SessionCaptureStore
{
    internal string AnalyticsDataDirectory => analytics.DataDirectoryPath;

    internal void RegisterLocalCapture(string sessionId, DateTimeOffset createdUtc, string source, string? businessId)
    {
        var key = LocalCaptureUsage.CaptureKey(sessionId, createdUtc);
        localCaptureRegistrations[key] = captureUsage.Register(sessionId, createdUtc, source, businessId);
    }

    private DateTimeOffset? FirstCaptureEvidenceUtc(AppSessionSnapshot snapshot, string summaryPath, DateTimeOffset savedUtc)
    {
        var key = LocalCaptureUsage.CaptureKey(snapshot.SessionId, snapshot.CreatedUtc);

        if (!localCaptureRegistrations.GetOrAdd(key, _ => captureUsage.IsRegistered(snapshot.SessionId, snapshot.CreatedUtc)))
        {
            return null;
        }

        if (firstCaptureEvidenceByKey.TryGetValue(key, out var first))
        {
            return first;
        }

        first = SessionSnapshotReader.TryLoadDocument<SessionCaptureSummaryDocument>(summaryPath)?.Session.FirstCaptureEvidenceUtc;

        if (first is null && LocalSessionCaptureAnalytics.HasCapturedEvidence(snapshot))
        {
            first = savedUtc;
        }

        if (first is not null)
        {
            firstCaptureEvidenceByKey[key] = first;
        }

        return first;
    }

    private void RecordCaptureEvidence(SessionCaptureSummary summary)
    {
        if (summary.FirstCaptureEvidenceUtc is not { } savedUtc)
        {
            return;
        }

        var key = LocalCaptureUsage.CaptureKey(summary.SessionId, summary.CreatedUtc);

        if (queuedCaptureReceipts.ContainsKey(key))
        {
            return;
        }

        if (captureUsage.RecordSavedEvidence(summary.SessionId, summary.CreatedUtc, savedUtc))
        {
            queuedCaptureReceipts.TryAdd(key, true);
        }
    }

    // A crash after saving the summary but before queuing its receipt is recoverable.
    private void RecoverCaptureUsage()
    {
        try
        {
            var registrations = captureUsage.RegisteredCaptureKeys();

            if (registrations.Count == 0)
            {
                return;
            }

            foreach (var snapshot in LoadSummaries())
            {
                var key = LocalCaptureUsage.CaptureKey(snapshot.SessionId, snapshot.CreatedUtc);

                if (!registrations.Contains(key))
                {
                    continue;
                }

                localCaptureRegistrations[key] = true;

                if (!TryGetSummaryFilePath(snapshot.SessionId, reloadIndex: false, out var path))
                {
                    continue;
                }

                var summary = SessionSnapshotReader.TryLoadDocument<SessionCaptureSummaryDocument>(path)?.Session;

                if (summary is not null)
                {
                    RecordCaptureEvidence(summary);
                }
            }
        }
        catch
        {
            // A later save retries without affecting capture startup.
        }
    }
}
