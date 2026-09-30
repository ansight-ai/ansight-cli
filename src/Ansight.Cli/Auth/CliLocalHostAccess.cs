namespace Ansight.Cli.Auth;

/// <summary>The resident shell survives each bounded, disposable product runtime.</summary>
internal sealed class CliLocalHostAccess
{
    private static readonly TimeSpan unavailableRetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan deniedRetryInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim retry = new(0, 1);
    private HostAccessState state = new(new AccessDecision(false, "checking"));

    public HostAccessState State
    {
        get
        {
            var current = Volatile.Read(ref state);
            return current.Lease is { IsDenied: true } lease
                ? new HostAccessState(lease.Failure)
                : current;
        }
    }

    public void BeginCheck() => Block(new AccessDecision(false, "checking"));

    public void Block(AccessDecision decision) => Volatile.Write(ref state, new HostAccessState(decision));

    public void Allow(Uri? explorerUrl, CliAccessLease lease)
        => Volatile.Write(ref state, new HostAccessState(lease.Decision, explorerUrl, lease));

    public void RequestRetry()
    {
        try { retry.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task WaitForRetryAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A denied grant or machine registration rarely changes without user action. The
        // recovery UI calls RequestRetry immediately after sign-in or a manual recheck.
        var interval = State.Decision.Reason == AccessDecision.Unavailable.Reason
            ? unavailableRetryInterval : deniedRetryInterval;
        var delay = Task.Delay(interval, timeProvider, wait.Token);
        var requested = retry.WaitAsync(wait.Token);
        await Task.WhenAny(delay, requested).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(delay, requested).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal sealed record HostAccessState(AccessDecision Decision, Uri? ExplorerUrl = null, CliAccessLease? Lease = null);
