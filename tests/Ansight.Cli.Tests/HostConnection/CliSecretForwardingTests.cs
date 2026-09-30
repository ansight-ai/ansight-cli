namespace Ansight.Cli.Tests.HostConnection;

public sealed class CliSecretForwardingTests
{
    [Fact]
    public void WorkspaceRunsForwardOnlyEnvironmentValuesDeclaredBySelectedTests()
    {
        var workspacePath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-cli-secret-forwarding-{Guid.NewGuid():N}");
        var testsPath = Path.Combine(workspacePath, "ansight", "tests");
        Directory.CreateDirectory(testsPath);
        try
        {
            WriteTest(testsPath, "alpha", "ENV_ALPHA");
            WriteTest(testsPath, "beta", "ENV_BETA");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ENV_ALPHA"] = "alpha-value",
                ["ENV_BETA"] = "beta-value",
                ["UNDECLARED_SECRET"] = "not-forwarded"
            };

            var singleValues = ControlClient.CollectForwardedSecretValues(
                CliArguments.Parse(["test", "run", workspacePath, "alpha"]),
                environment.GetValueOrDefault);
            var batchValues = ControlClient.CollectForwardedSecretValues(
                CliArguments.Parse(["test", "run-all", workspacePath, "--test", "beta"]),
                environment.GetValueOrDefault);

            Assert.Equal("alpha-value", Assert.Single(singleValues).Value);
            Assert.Equal("beta-value", Assert.Single(batchValues).Value);
            Assert.DoesNotContain("UNDECLARED_SECRET", singleValues.Keys);
            Assert.DoesNotContain("UNDECLARED_SECRET", batchValues.Keys);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public void InlineRunsForwardOnlyExplicitSecretAliases()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["INLINE_PASSWORD"] = "inline-value",
            ["UNDECLARED_SECRET"] = "not-forwarded"
        };

        var values = ControlClient.CollectForwardedSecretValues(
            CliArguments.Parse(
                ["test", "run-inline", "--secret", "INLINE_PASSWORD", "--session-id", "session-1"]),
            environment.GetValueOrDefault);

        var secret = Assert.Single(values);
        Assert.Equal("INLINE_PASSWORD", secret.Key);
        Assert.Equal("inline-value", secret.Value);
    }

    [Fact]
    public void AppExecutionsForwardOnlyExplicitSecretAliases()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["APP_PASSWORD"] = "app-value",
            ["UNDECLARED_SECRET"] = "not-forwarded"
        };

        var values = ControlClient.CollectForwardedSecretValues(
            CliArguments.Parse(
            [
                "app",
                "execute",
                "session-1",
                "--prompt",
                "Sign in and open settings.",
                "--secret",
                "APP_PASSWORD"
            ]),
            environment.GetValueOrDefault);

        var secret = Assert.Single(values);
        Assert.Equal("APP_PASSWORD", secret.Key);
        Assert.Equal("app-value", secret.Value);
    }

    private static void WriteTest(string testsPath, string id, string secretAlias)
    {
        File.WriteAllText(
            Path.Combine(testsPath, $"{id}.json"),
            $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "name": "{{id}}",
              "appId": "com.example.app",
              "prompt": "Run {{id}}.",
              "validation": { "assertions": ["The test passes"] },
              "requiredSecrets": ["{{secretAlias}}"]
            }
            """);
    }
}
