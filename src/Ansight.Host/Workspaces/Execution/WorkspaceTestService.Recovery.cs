namespace Ansight.Host.Workspaces.Execution;

public sealed partial class WorkspaceTestService
{
    // Called before startup accepts new work. A saved running marker cannot resume
    // its execution after the host process exits, even if its capture was still live.
    internal int RecoverInterruptedSessionRuns()
    {
        var candidates = new List<InterruptedSessionRun>();
        foreach (var session in runtime.Sessions.GetSummaries())
        {
            if (!session.IsHistorical || runtime.IsSessionLive(session.SessionId)) continue;
            lock (sessionClaimGate)
            {
                if (claimedSessionIds.Contains(session.SessionId)) continue;
            }

            try
            {
                var run = session.CustomProperties?[SessionUiTestRun.PropertyGroup]?.Deserialize<SessionUiTestRun>(JsonUtil.Compact);
                if (run is not null && !string.IsNullOrWhiteSpace(run.RunId)
                    && string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(new InterruptedSessionRun(session.SessionId, run));
            }
            catch (JsonException)
            {
                // Unrelated or malformed imported metadata must not block host startup.
            }
        }

        if (candidates.Count == 0) return 0;
        var audits = simulatorAgent.ListRunHistory();
        foreach (var candidate in candidates)
        {
            var run = candidate.Run;
            var audit = audits.FirstOrDefault(entry => entry.Audit.RunId == run.RunId
                && entry.Audit.SessionId == candidate.SessionId)?.Audit;
            runtime.SessionEditing.SetUiTestRun(candidate.SessionId, run with
            {
                Status = audit?.Status.ToString().ToLowerInvariant() ?? "cancelled",
                CompletedUtc = audit?.CompletedUtc ?? DateTimeOffset.UtcNow
            });
        }
        return candidates.Count;
    }

    private sealed record InterruptedSessionRun(string SessionId, SessionUiTestRun Run);
}
