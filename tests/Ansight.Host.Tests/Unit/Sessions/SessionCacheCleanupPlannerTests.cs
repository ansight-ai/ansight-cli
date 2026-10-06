namespace Ansight.Host.Tests.Unit.Sessions;

public sealed class SessionCacheCleanupPlannerTests
{
    [Fact]
    public void BuildPlan_AutomaticModeDeletesOnlyEnoughToMeetLimit()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var plan = SessionCacheCleanupPlanner.BuildPlan(
            [
                CreateCandidate("oldest", nowUtc.AddDays(-100), 60),
                CreateCandidate("older", nowUtc.AddDays(-90), 40),
                CreateCandidate("recent", nowUtc.AddDays(-1), 20)
            ],
            retentionDays: 30,
            maximumCacheSizeBytes: 100,
            nowUtc,
            includeRetentionCandidates: false);

        Assert.Equal("oldest", Assert.Single(plan.Items).SessionId);
        Assert.Equal(60, plan.ProjectedCacheSizeBytes);
        Assert.Equal(0, plan.RetentionCandidateCount);
    }

    [Fact]
    public void BuildPlan_ProtectsPinnedLiveAndExplicitlyProtectedSessions()
    {
        var nowUtc = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            CreateCandidate("old", nowUtc.AddDays(-40), 100),
            CreateCandidate("pinned", nowUtc.AddDays(-50), 200, isPinned: true),
            CreateCandidate("live", nowUtc.AddDays(-60), 300, isLive: true),
            CreateCandidate("selected", nowUtc.AddDays(-70), 400)
        };

        var plan = SessionCacheCleanupPlanner.BuildPlan(
            candidates,
            retentionDays: 28,
            maximumCacheSizeBytes: 10_000,
            nowUtc,
            new HashSet<string>(StringComparer.Ordinal) { "selected" });

        var item = Assert.Single(plan.Items);
        Assert.Equal("old", item.SessionId);
        Assert.Equal("retention", item.Reason);
        Assert.Equal(1, plan.PinnedSessionCount);
        Assert.Equal(1, plan.LiveSessionCount);
        Assert.Equal(900, plan.ProjectedCacheSizeBytes);
    }

    [Fact]
    public void BuildPlan_AppliesRetentionBeforeOldestFirstCacheLimit()
    {
        var nowUtc = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            CreateCandidate("expired", nowUtc.AddDays(-40), 300),
            CreateCandidate("oldest", nowUtc.AddDays(-20), 400),
            CreateCandidate("newest", nowUtc.AddDays(-10), 500)
        };

        var plan = SessionCacheCleanupPlanner.BuildPlan(
            candidates,
            retentionDays: 28,
            maximumCacheSizeBytes: 500,
            nowUtc);

        Assert.Collection(
            plan.Items,
            item =>
            {
                Assert.Equal("expired", item.SessionId);
                Assert.Equal("retention", item.Reason);
            },
            item =>
            {
                Assert.Equal("oldest", item.SessionId);
                Assert.Equal("cache-limit", item.Reason);
            });
        Assert.Equal(500, plan.ProjectedCacheSizeBytes);
        Assert.Equal(1, plan.RetentionCandidateCount);
        Assert.Equal(1, plan.CacheCandidateCount);
    }

    private static SessionCacheCleanupCandidate CreateCandidate(
        string sessionId,
        DateTimeOffset createdUtc,
        long cacheSizeBytes,
        bool isPinned = false,
        bool isLive = false)
    {
        return new SessionCacheCleanupCandidate(
            sessionId,
            "com.example.app",
            sessionId,
            createdUtc,
            createdUtc.AddHours(1),
            cacheSizeBytes,
            isPinned,
            isLive);
    }
}
