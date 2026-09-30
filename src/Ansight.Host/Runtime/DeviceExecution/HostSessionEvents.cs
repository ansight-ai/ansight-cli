namespace Ansight.Host.Runtime.DeviceExecution;

internal static class HostSessionEvents
{
    private static readonly ConditionalWeakTable<IRuntimeState, object> locks = new();

    internal static byte AllocateChannel(IRuntimeState state, string sessionId, string name, string type,
        string unit, string source, string kind)
    {
        lock (locks.GetValue(state, static _ => new object()))
        {
            if (!state.TryGetSessionSnapshot(sessionId, out var snapshot)) throw new InvalidOperationException("Session is unavailable.");
            var existing = snapshot!.MetricChannels.FirstOrDefault(channel => channel.Source == source && channel.Kind == kind && channel.Name == name);
            if (existing is not null) return existing.ChannelId;
            var used = snapshot.MetricChannels.Select(channel => channel.ChannelId).ToHashSet();
            var id = Enumerable.Range(1, 255).Select(value => (byte)value).FirstOrDefault(value => !used.Contains(value));
            if (id == 0) throw new InvalidOperationException("The session has no free telemetry channels.");
            state.UpdateSessionMetricChannels(sessionId, [new SessionMetricChannel
            {
                ChannelId = id, Name = name, Type = type, Unit = unit, Source = source,
                Kind = kind, Group = "External process", ColorHex = "#6C83FF"
            }]);
            return id;
        }
    }

    internal static void Publish(IRuntimeState state, string sessionId, string label, string eventType, string group = "")
    {
        if (!state.TryGetSessionSnapshot(sessionId, out var session)) return;
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        // EventType records host provenance; channel zero is the unassigned event channel.
        state.AddSessionApplicationEvents(sessionId, [new SessionApplicationEvent(id, label, eventType, group, now, 0)]);
        state.PublishRuntimeEvent(new RuntimeAppEvent(now, id, sessionId, session!.AppId, session.ClientName,
            label, eventType, group, 0));
    }
}
