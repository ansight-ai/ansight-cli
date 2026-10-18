namespace Ansight.Host.Activity;

public sealed class SessionActivityService
{
    private static readonly ConditionalWeakTable<IApplicationPaths, SessionActivityService> services = new();
    private readonly SessionActivityStore store;
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");
    public string? PersistenceError => store.PersistenceError;

    internal SessionActivityService(SessionActivityStore store) => this.store = store;
    internal static SessionActivityService For(IApplicationPaths paths)
        => services.GetValue(paths, static paths => new(new SessionActivityStore(paths.ApplicationDataPath)));

    public SessionActivityPage Read(string? sessionId, string? cursor = null, int limit = 200)
        => store.Read(sessionId, cursor, limit);

    internal SessionActivityOperation Begin(string operation, string category, string? sessionId = null,
        string? appId = null, string? requestId = null)
        => new(this, operation, category, sessionId, appId, requestId);

    internal bool Append(SessionActivityRecord record) => store.Append(record with
    {
        Operation = ActivityCaller.Limit(record.Operation) ?? "unknown", Category = ActivityCaller.Limit(record.Category) ?? "unknown",
        RequestId = ActivityCaller.Limit(record.RequestId), RunId = ActivityCaller.Limit(record.RunId), BatchRunId = ActivityCaller.Limit(record.BatchRunId),
        Outcome = ActivityCaller.Limit(record.Outcome), ErrorType = ActivityCaller.Limit(record.ErrorType), EvidenceStatus = ActivityCaller.Limit(record.EvidenceStatus),
        EvidenceIds = (record.EvidenceIds ?? []).Take(16).Select(id => ActivityCaller.Limit(id)).OfType<string>().ToArray()
    });

    internal async Task<T> RecordAsync<T>(string operation, string category, string? sessionId,
        Func<Task<T>> execute, Func<T, string> outcome, string? requestId = null, string? appId = null)
    {
        using var activity = Begin(operation, category, sessionId, appId, requestId);
        try
        {
            var result = await execute().ConfigureAwait(false);
            activity.Complete(outcome(result));
            return result;
        }
        catch (OperationCanceledException) { activity.Complete("cancelled"); throw; }
        catch (Exception exception) { activity.Complete(exception is ArgumentException ? "rejected" : "failed", errorType: exception.GetType().Name); throw; }
    }

    internal T Record<T>(string operation, string category, string? sessionId, Func<T> execute, Func<T, string> outcome)
    {
        using var activity = Begin(operation, category, sessionId);
        try
        {
            var result = execute();
            activity.Complete(outcome(result));
            return result;
        }
        catch (OperationCanceledException) { activity.Complete("cancelled"); throw; }
        catch (Exception exception) { activity.Complete(exception is ArgumentException ? "rejected" : "failed", errorType: exception.GetType().Name); throw; }
    }
}
