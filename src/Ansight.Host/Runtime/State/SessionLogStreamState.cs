namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;

internal sealed class SessionLogStreamState
{
    public required string StreamId { get; init; }
    public required string Kind { get; set; }
    public required string DisplayName { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public string? StatusMessage { get; set; }
    public IReadOnlyDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public List<LogEntry> Entries { get; } = [];
    public int TotalEntryCount { get; set; }

    public static SessionLogStreamState FromSnapshot(SessionLogStream stream)
    {
        var state = new SessionLogStreamState
        {
            StreamId = stream.StreamId,
            Kind = stream.Kind,
            DisplayName = stream.DisplayName,
            Status = stream.Status,
            StartedUtc = stream.StartedUtc,
            EndedUtc = stream.EndedUtc,
            StatusMessage = stream.StatusMessage,
            Metadata = new Dictionary<string, string>(stream.Metadata, StringComparer.Ordinal),
            TotalEntryCount = Math.Max(stream.TotalEntryCount, stream.Entries.Count)
        };
        state.Entries.AddRange(stream.Entries);
        return state;
    }

    public SessionLogStream ToSnapshot(bool includeEntries = true)
        => new()
        {
            StreamId = StreamId,
            Kind = Kind,
            DisplayName = DisplayName,
            Status = Status,
            StartedUtc = StartedUtc,
            EndedUtc = EndedUtc,
            StatusMessage = StatusMessage,
            Metadata = new Dictionary<string, string>(Metadata, StringComparer.Ordinal),
            Entries = includeEntries ? Entries.ToArray() : Array.Empty<LogEntry>(),
            TotalEntryCount = TotalEntryCount,
            RetainedEntryStartIndex = Math.Max(0, TotalEntryCount - Entries.Count)
        };
}
