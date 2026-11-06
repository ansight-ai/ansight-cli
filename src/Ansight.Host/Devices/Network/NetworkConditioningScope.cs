using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Devices.Network;

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

    public void Register(Func<Task> action)
    {
        lock (cleanup)
        {
            cleanup.Add(action);
        }
    }

    public ValueTask DisposeAsync()
    {
        // Restore the caller's execution context synchronously, before entering
        // an async method (whose AsyncLocal writes do not flow back to callers).
        current.Value = previous;

        Func<Task>[] actions;

        lock (cleanup)
        {
            actions = cleanup.ToArray();
            cleanup.Clear();
        }

        return RunCleanupAsync(actions);
    }

    private static async ValueTask RunCleanupAsync(Func<Task>[] actions)
    {
        List<Exception> errors = [];

        foreach (var action in actions.Reverse())
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("Network conditioning cleanup failed.", errors);
        }
    }
}
