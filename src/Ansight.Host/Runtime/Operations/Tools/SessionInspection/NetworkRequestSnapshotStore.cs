using System.Globalization;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class NetworkRequestSnapshotStore
{
    internal const long MaximumCachedCharacters = 16 * 1024 * 1024;
    internal const int MaximumSnapshots = 32;
    internal static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5);
    private readonly object sync = new();
    private readonly Dictionary<string, NetworkRequestSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private long cachedCharacters;

    public NetworkRequestSnapshotStore(TimeProvider? timeProvider = null)
        => this.timeProvider = timeProvider ?? TimeProvider.System;

    public NetworkRequestSnapshot Create(AppSessionSnapshot session, NetworkInspectionFilters filters)
    {
        var summaries = new List<string>();
        long characterCount = filters.ToJson().ToJsonString().Length + session.SessionId.Length + session.AppId.Length;
        if (characterCount > MaximumCachedCharacters)
        {
            throw new ArgumentException("The network query filters exceed the cursor memory budget. Narrow the query filters.");
        }
        foreach (var request in session.NetworkRequests.Where(filters.Matches)
                     .OrderByDescending(request => request.StartedAtUtc)
                     .ThenBy(request => request.Id, StringComparer.Ordinal))
        {
            var summary = NetworkInspectionPayload.Summary(request).ToJsonString();
            if (summary.Length > NetworkInspectionPayload.MaximumSerializedCharacters - 2048)
            {
                throw new ArgumentException("A network request summary exceeds the serialized output budget.");
            }

            characterCount += summary.Length;
            if (characterCount > MaximumCachedCharacters)
            {
                throw new ArgumentException("The matching network snapshot exceeds the cursor memory budget. Narrow the query filters.");
            }

            summaries.Add(summary);
        }

        return new NetworkRequestSnapshot(Guid.NewGuid().ToString("N"), session.SessionId, session.AppId,
            filters, summaries, characterCount, timeProvider.GetUtcNow());
    }

    public void Retain(NetworkRequestSnapshot snapshot)
    {
        lock (sync)
        {
            RemoveExpired();
            while (snapshots.Count >= MaximumSnapshots || cachedCharacters + snapshot.CharacterCount > MaximumCachedCharacters)
            {
                Remove(snapshots.Values.MinBy(item => item.CreatedUtc)!);
            }

            snapshots.Add(snapshot.Id, snapshot);
            cachedCharacters += snapshot.CharacterCount;
        }
    }

    public NetworkRequestSnapshot Resolve(string cursor, string sessionId, out int offset)
    {
        var separator = cursor.IndexOf(':');
        if (separator != 32 || !Guid.TryParseExact(cursor[..separator], "N", out _)
            || !int.TryParse(cursor.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            || offset < 1)
        {
            throw new ArgumentException("The network cursor is malformed.");
        }

        lock (sync)
        {
            RemoveExpired();
            if (!snapshots.TryGetValue(cursor[..separator], out var snapshot))
            {
                throw new ArgumentException("The network cursor is invalid or expired. Start a new query without a cursor.");
            }

            if (!string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The network cursor belongs to a different session.");
            }

            if (offset >= snapshot.Summaries.Count)
            {
                throw new ArgumentException("The network cursor offset is invalid.");
            }

            return snapshot;
        }
    }

    public static string Cursor(NetworkRequestSnapshot snapshot, int offset)
        => $"{snapshot.Id}:{offset.ToString(CultureInfo.InvariantCulture)}";

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in snapshots.Values.Where(snapshot => now - snapshot.CreatedUtc >= SnapshotLifetime).ToArray())
        {
            Remove(expired);
        }
    }

    private void Remove(NetworkRequestSnapshot snapshot)
    {
        snapshots.Remove(snapshot.Id);
        cachedCharacters -= snapshot.CharacterCount;
    }
}
