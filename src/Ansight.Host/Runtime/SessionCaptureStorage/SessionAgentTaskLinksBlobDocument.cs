namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Host;

internal sealed class SessionAgentTaskLinksBlobDocument
{
    public required DateTimeOffset SavedAtUtc { get; init; }

    public required IReadOnlyList<SessionAgentTaskLink> Tasks { get; init; }
}
