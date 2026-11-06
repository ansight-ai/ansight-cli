namespace Ansight.Host.Tests.TestSupport;

internal sealed class ManualCredentialTimeProvider : TimeProvider
{
    public ManualCredentialTimeProvider(DateTimeOffset now)
    {
        this.Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
