using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class SessionTimelineEvent
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required int Sequence { get; init; }

    public required JsonObject Payload { get; init; }
}
