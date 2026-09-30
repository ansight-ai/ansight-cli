namespace Ansight.Host.Tests.TestSupport;

internal sealed class ManualCredentialTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
