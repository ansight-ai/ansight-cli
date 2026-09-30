using Ansight.Host.Trends;

namespace Ansight.Host.Tests.Unit.Trends;

public sealed class WorkspaceTrendsSpanResolverTests
{
    [Fact]
    public void Resolve_GroupsAndCorrelatesCompletedSpansByEventDetails()
    {
        var startedUtc = DateTimeOffset.Parse("2026-08-20T01:00:00Z");
        var snapshot = CreateSnapshot(
            Event("garden-start-1", "guide.started", "Secret Garden", startedUtc),
            Event("seaside-start", "guide.started", "Seaside", startedUtc.AddSeconds(1)),
            Event("seaside-end", "guide.completed", "Seaside", startedUtc.AddSeconds(2)),
            Event("garden-end-1", "guide.completed", "Secret Garden", startedUtc.AddSeconds(3)),
            Event("garden-start-2", "guide.started", "Secret Garden", startedUtc.AddSeconds(4)),
            Event("garden-end-2", "guide.completed", "Secret Garden", startedUtc.AddSeconds(5)));
        var span = CreateSpan(WorkspaceTrendsSpanSelection.LastCompleted);

        var result = WorkspaceTrendsSpanResolver.Resolve(snapshot, span);

        Assert.Equal(WorkspaceTrendsStatus.Passed, result.Status);
        Assert.Collection(
            result.Instances,
            seaside =>
            {
                Assert.Equal("Seaside", seaside.Group);
                Assert.Equal("seaside-start", seaside.StartEventId);
                Assert.Equal("seaside-end", seaside.EndEventId);
                Assert.Equal(0, seaside.InstanceIndex);
            },
            garden =>
            {
                Assert.Equal("Secret Garden", garden.Group);
                Assert.Equal("garden-start-2", garden.StartEventId);
                Assert.Equal("garden-end-2", garden.EndEventId);
                Assert.Equal(1, garden.InstanceIndex);
            });
    }

    [Fact]
    public void Resolve_LeavesSpanUngroupedWhenSdkEventDetailsAreEmpty()
    {
        var startedUtc = DateTimeOffset.Parse("2026-08-20T01:00:00Z");
        var snapshot = CreateSnapshot(
            Event("start", "guide.started", string.Empty, startedUtc),
            Event("end", "guide.completed", string.Empty, startedUtc.AddSeconds(1)));

        var result = WorkspaceTrendsSpanResolver.Resolve(snapshot, CreateSpan(WorkspaceTrendsSpanSelection.All));

        Assert.Equal(WorkspaceTrendsStatus.Passed, result.Status);
        Assert.Null(Assert.Single(result.Instances).Group);
    }

    private static WorkspaceTrendsSpanDefinition CreateSpan(WorkspaceTrendsSpanSelection selection)
        => new(
            new WorkspaceEventAnchor("guide.started"),
            new WorkspaceEventAnchor("guide.completed"),
            selection,
            TimeSpan.FromSeconds(30));

    private static SessionApplicationEvent Event(
        string eventId,
        string label,
        string details,
        DateTimeOffset capturedAtUtc)
        => new(eventId, label, "span", details, capturedAtUtc, 0);

    private static AppSessionSnapshot CreateSnapshot(params SessionApplicationEvent[] events)
        => new()
        {
            SessionId = "session-grouped-span",
            AppId = "com.example.app",
            ClientName = "Test app",
            RemoteAddress = "local",
            CreatedUtc = events.Min(static appEvent => appEvent.CapturedAtUtc),
            ConfigId = null,
            Status = "connected",
            LastUpdatedUtc = events.Max(static appEvent => appEvent.CapturedAtUtc),
            IsHistorical = false,
            ApplicationEvents = events,
            MetricChannels = [],
            Metrics = []
        };
}
