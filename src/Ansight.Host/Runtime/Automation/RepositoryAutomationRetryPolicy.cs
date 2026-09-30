namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationRetryPolicy(
    int MaximumAttempts,
    TimeSpan InitialDelay,
    double BackoffMultiplier,
    TimeSpan MaximumDelay)
{
    public static RepositoryAutomationRetryPolicy None { get; } = new(
        1,
        TimeSpan.Zero,
        1,
        TimeSpan.Zero);

    public TimeSpan GetDelayBeforeAttempt(int nextAttemptNumber)
    {
        if (nextAttemptNumber <= 1 || MaximumAttempts <= 1)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Max(0, nextAttemptNumber - 2);
        var delayMilliseconds = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, MaximumDelay.TotalMilliseconds));
    }
}
