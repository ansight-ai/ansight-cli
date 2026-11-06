using Ansight.Cli.Auth;

namespace Ansight.Cli.Tests.TestSupport;

internal sealed class TestAccessAuthorizer : ICliAccessAuthorizer
{
    private readonly Func<CancellationToken, Task<AccessDecision>> check;

    public TestAccessAuthorizer(Func<CancellationToken, Task<AccessDecision>> check)
    {
        this.check = check;
    }

    public static ICliAccessAuthorizer Allow { get; } = new TestAccessAuthorizer(_ => Task.FromResult(Active()));
    public static ICliAccessAuthorizer Deny { get; } = new TestAccessAuthorizer(_ => Task.FromResult(AccessDecision.ProductAccessRequired));

    public static AccessDecision Active(TimeSpan? duration = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new(true, "active", now, now.Add(duration ?? TimeSpan.FromHours(1)), "test-user");
    }

    public Task<AccessDecision> CheckAsync(CancellationToken cancellationToken) => check(cancellationToken);
}
