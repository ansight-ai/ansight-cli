namespace Ansight.Cli.Tests.HostConnection;

public sealed class CliRunnerSecretScopeTests
{
    [Fact]
    public void SecretScopeIsLimitedToTheCurrentExecution()
    {
        const string alias = "ansight.test.runner.scope.secret";
        var original = Environment.GetEnvironmentVariable(alias);
        try
        {
            Environment.SetEnvironmentVariable(alias, null);
            using (CliCommandContext.PushSecretValues(new Dictionary<string, string> { [alias] = "one-run-value" }))
            {
                Assert.Equal("one-run-value", CliCommandContext.ResolveScopedSecret(alias));
                var forwarded = ControlClient.CollectForwardedSecretValues(
                    CliArguments.Parse(["app", "execute", "--secret", alias]),
                    CliCommandContext.ResolveScopedSecret);
                Assert.Equal("one-run-value", forwarded[alias]);
                var taskForwarded = ControlClient.CollectForwardedSecretValues(
                    CliArguments.Parse(["task", "run", "login", "--secret", alias]),
                    CliCommandContext.ResolveScopedSecret);
                Assert.Equal("one-run-value", taskForwarded[alias]);
            }
            Assert.Null(CliCommandContext.ResolveScopedSecret(alias));
        }
        finally { Environment.SetEnvironmentVariable(alias, original); }
    }
}
