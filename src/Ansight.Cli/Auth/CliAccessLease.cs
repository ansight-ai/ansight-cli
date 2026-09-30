namespace Ansight.Cli.Auth;

/// <summary>
/// A bounded, renewable permission lifetime. The deadline timer is independent of network
/// revalidation, so a hung request cannot carry a command past its grant/cache deadline.
/// </summary>
internal sealed class CliAccessLease : IAsyncDisposable
{
    internal static readonly TimeSpan MaximumValidity = TimeSpan.FromMinutes(5);
    // Leave 30 seconds for a slow verification before the five-minute hard deadline.
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(4.5);
    internal static readonly TimeSpan LocalRecheckInterval = TimeSpan.FromSeconds(15);
    private readonly ICliAccessAuthorizer authorizer;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource monitorShutdown;
    private readonly CancellationTokenSource accessDeadline;
    private readonly CancellationTokenSource commandCancellation;
    private readonly Task monitor;
    private readonly Task localMonitor;
    private AccessDecision decision;
    private AccessDecision deadlineFailure;

    private CliAccessLease(ICliAccessAuthorizer authorizer, AccessDecision decision, TimeSpan validity,
        CancellationToken cancellationToken, TimeProvider timeProvider)
    {
        this.authorizer = authorizer;
        this.decision = decision;
        deadlineFailure = GetDeadlineFailure(decision, validity);
        this.timeProvider = timeProvider;
        monitorShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        accessDeadline = new CancellationTokenSource(validity, timeProvider);
        commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accessDeadline.Token);
        Token = commandCancellation.Token;
        monitor = MonitorAsync();
        localMonitor = MonitorLocalAsync();
    }

    private CliAccessLease(CancellationToken cancellationToken, TimeProvider timeProvider)
    {
        authorizer = CliLocalAccessAuthorizer.Instance;
        decision = new AccessDecision(true, "local");
        deadlineFailure = AccessDecision.Unavailable;
        this.timeProvider = timeProvider;
        monitorShutdown = new CancellationTokenSource();
        accessDeadline = new CancellationTokenSource();
        commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Token = commandCancellation.Token;
        monitor = Task.CompletedTask;
        localMonitor = Task.CompletedTask;
    }

    public bool IsLocal => Decision.Reason == "local";

    public static CliAccessLease CreateLocal(CancellationToken cancellationToken, TimeProvider? timeProvider = null)
        => new(cancellationToken, timeProvider ?? TimeProvider.System);

    public CancellationToken Token { get; }
    public bool IsDenied => accessDeadline.IsCancellationRequested;
    public AccessDecision Decision => Volatile.Read(ref decision);
    public AccessDecision Failure => !Decision.IsAuthorized ? Decision : Volatile.Read(ref deadlineFailure);

    public static async Task<CliAccessLease> CreateAsync(ICliAccessAuthorizer authorizer,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        if (authorizer is CliLocalAccessAuthorizer)
            return CreateLocal(cancellationToken, timeProvider);
        var startedAt = timeProvider.GetTimestamp();
        var decision = await authorizer.CheckAsync(cancellationToken).ConfigureAwait(false);
        var validity = GetValidity(decision, timeProvider.GetElapsedTime(startedAt));
        if (validity <= TimeSpan.Zero)
            throw new CliAccessDeniedException(decision.IsAuthorized ? AccessDecision.ProductAccessRequired : decision);
        return new CliAccessLease(authorizer, decision, validity, cancellationToken, timeProvider);
    }

    private async Task MonitorAsync()
    {
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(monitorShutdown.Token, accessDeadline.Token);
        try
        {
            while (true)
            {
                await Task.Delay(RecheckInterval, timeProvider, monitoring.Token).ConfigureAwait(false);
                var startedAt = timeProvider.GetTimestamp();
                var next = await authorizer.CheckAsync(monitoring.Token).ConfigureAwait(false);
                // Never let switching accounts renew an operation started by another identity.
                if (next.UserId != Decision.UserId && next.IsAuthorized)
                    next = AccessDecision.AuthenticationRequired;
                var validity = GetValidity(next, timeProvider.GetElapsedTime(startedAt));
                if (validity <= TimeSpan.Zero && next.IsAuthorized)
                    next = AccessDecision.ProductAccessRequired;
                if (accessDeadline.IsCancellationRequested) return;
                Volatile.Write(ref decision, next);
                if (validity <= TimeSpan.Zero)
                {
                    await accessDeadline.CancelAsync().ConfigureAwait(false);
                    return;
                }
                if (accessDeadline.IsCancellationRequested) return;
                Volatile.Write(ref deadlineFailure, GetDeadlineFailure(next, validity));
                accessDeadline.CancelAfter(validity);
            }
        }
        catch (OperationCanceledException) when (monitoring.IsCancellationRequested)
        {
        }
        catch
        {
            Volatile.Write(ref decision, AccessDecision.Unavailable);
            await accessDeadline.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task MonitorLocalAsync()
    {
        if (authorizer is not ICliLocalAccessProbe localProbe) return;
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(monitorShutdown.Token, accessDeadline.Token);
        try
        {
            while (true)
            {
                await Task.Delay(LocalRecheckInterval, timeProvider, monitoring.Token).ConfigureAwait(false);
                var failure = await localProbe.CheckLocalAsync(Decision.UserId, monitoring.Token).ConfigureAwait(false);
                if (failure is null) continue;
                Volatile.Write(ref decision, failure);
                await accessDeadline.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (monitoring.IsCancellationRequested)
        {
        }
        catch
        {
            Volatile.Write(ref decision, AccessDecision.Unavailable);
            await accessDeadline.CancelAsync().ConfigureAwait(false);
        }
    }

    private static TimeSpan GetValidity(AccessDecision decision, TimeSpan requestDuration)
    {
        if (!decision.IsAuthorized || decision.EvaluatedAt is not { } evaluatedAt
            || decision.AuthorizedUntil is not { } authorizedUntil)
            return TimeSpan.Zero;
        var remaining = authorizedUntil - evaluatedAt - requestDuration;
        return remaining < MaximumValidity ? remaining : MaximumValidity;
    }

    public async ValueTask DisposeAsync()
    {
        await commandCancellation.CancelAsync().ConfigureAwait(false);
        await monitorShutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(monitor, localMonitor).ConfigureAwait(false);
        monitorShutdown.Dispose();
        commandCancellation.Dispose();
        accessDeadline.Dispose();
    }

    private static AccessDecision GetDeadlineFailure(AccessDecision decision, TimeSpan validity)
        => validity == MaximumValidity && decision.AuthorizedUntil - decision.EvaluatedAt > MaximumValidity
            ? AccessDecision.Unavailable
            : AccessDecision.ProductAccessRequired;
}

internal sealed class CliAccessDeniedException(AccessDecision decision) : Exception(decision.Message)
{
    public AccessDecision Decision { get; } = decision;
}
