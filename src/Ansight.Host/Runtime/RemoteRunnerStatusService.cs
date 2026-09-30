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

public sealed class RemoteRunnerStatusService
{
    private readonly Lock gate = new();
    private RemoteRunnerStatusSnapshot snapshot = new(
        false,
        "disabled",
        "Remote runner processing is not enabled for this host.",
        UpdatedAt: DateTimeOffset.UtcNow);

    public event EventHandler<RemoteRunnerStatusSnapshot>? StatusChanged;

    public RemoteRunnerStatusSnapshot GetSnapshot()
    {
        lock (gate) return snapshot;
    }

    public void Update(RemoteRunnerStatusSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var updated = value with { UpdatedAt = value.UpdatedAt ?? DateTimeOffset.UtcNow };
        lock (gate) snapshot = updated;
        StatusChanged?.Invoke(this, updated);
    }
}
