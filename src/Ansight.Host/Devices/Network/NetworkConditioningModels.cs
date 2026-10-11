using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices.Network;

internal sealed record NetworkConditionProfile
{
    public int UploadKbps { get; init; }
    public int DownloadKbps { get; init; }
    public int LatencyMs { get; init; }
    public int JitterMs { get; init; }
    public int Seed { get; init; }
    public bool Stall { get; init; }
    public bool Blocked { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static NetworkConditionProfile Read(JsonNode? value)
    {
        var profile = value?.Deserialize<NetworkConditionProfile>(JsonOptions)
            ?? throw new ArgumentException("profile is required.");
        if (profile.UploadKbps is < 0 or > 1_000_000 || profile.DownloadKbps is < 0 or > 1_000_000
            || profile.LatencyMs is < 0 or > 10_000 || profile.JitterMs is < 0 or > 10_000)
            throw new ArgumentException("Rates must be 0–1,000,000 kbps (0 = unlimited); delay and jitter must be 0–10,000 ms.");
        if (profile.Stall && profile.Blocked) throw new ArgumentException("Choose stall or blocked, not both.");
        return profile;
    }
}

internal sealed record NetworkConditionStep(NetworkConditionProfile Profile, int DurationMs);
internal sealed record NetworkConditionTarget(string DeviceId, string AppId, string? SessionId);

/// <summary>Task-owned cleanup also runs when Node exits before its JavaScript finally.</summary>
internal sealed class NetworkConditioningScope : IAsyncDisposable
{
    private static readonly AsyncLocal<NetworkConditioningScope?> current = new();
    private readonly NetworkConditioningScope? previous;
    private readonly List<Func<Task>> cleanup = [];
    public static NetworkConditioningScope? Current => current.Value;
    public string OwnerId { get; }

    private NetworkConditioningScope(string ownerId)
    {
        OwnerId = ownerId;
        previous = current.Value;
        current.Value = this;
    }

    public static NetworkConditioningScope Begin(string ownerId) => new(ownerId);
    public void Register(Func<Task> action) { lock (cleanup) cleanup.Add(action); }

    public ValueTask DisposeAsync()
    {
        // Restore the caller's execution context synchronously, before entering
        // an async method (whose AsyncLocal writes do not flow back to callers).
        current.Value = previous;
        Func<Task>[] actions;
        lock (cleanup) { actions = cleanup.ToArray(); cleanup.Clear(); }
        return RunCleanupAsync(actions);
    }

    private static async ValueTask RunCleanupAsync(Func<Task>[] actions)
    {
        List<Exception> errors = [];
        foreach (var action in actions.Reverse())
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Network conditioning cleanup failed.", errors);
    }
}
