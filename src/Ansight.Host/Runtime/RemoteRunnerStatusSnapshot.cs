namespace Ansight.Host.Runtime;

public sealed record RemoteRunnerStatusSnapshot(
    bool Enabled,
    string State,
    string Message,
    Guid? MachineId = null,
    string? DisplayName = null,
    Guid? TeamId = null,
    Guid? JobId = null,
    string? JobKind = null,
    DateTimeOffset? LastHeartbeatAt = null,
    DateTimeOffset? UpdatedAt = null);
