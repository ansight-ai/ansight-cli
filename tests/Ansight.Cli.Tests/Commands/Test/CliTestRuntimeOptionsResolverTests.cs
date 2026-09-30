using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Test;

public sealed class CliTestRuntimeOptionsResolverTests
{
    [Fact]
    public void WithAvailablePorts_AllocatesDistinctPortsOutsideSessionRange()
    {
        var result = CliTestRuntimeOptionsResolver.WithAvailablePorts(CreateOptions());

        Assert.NotNull(result.DiscoveryPort);
        Assert.NotNull(result.WebSocketPort);
        Assert.Equal(2, new[]
        {
            result.DiscoveryPort.Value,
            result.WebSocketPort.Value
        }.Distinct().Count());
        Assert.DoesNotContain(
            result.WebSocketPort.Value,
            Enumerable.Range(
                ProtocolDefaults.WebSocketSessionPortRangeStart,
                ProtocolDefaults.WebSocketSessionPortRangeEnd
                - ProtocolDefaults.WebSocketSessionPortRangeStart
                + 1));
    }

    [Fact]
    public void WithAvailablePorts_PreservesConfiguredPorts()
    {
        var options = CreateOptions() with
        {
            DiscoveryPort = 46_123,
            WebSocketPort = 46_124
        };

        var result = CliTestRuntimeOptionsResolver.WithAvailablePorts(options);

        Assert.Equal(options, result);
    }

    [Fact]
    public void WithAvailablePorts_DoesNotReuseConfiguredPort()
    {
        var options = CreateOptions() with { DiscoveryPort = 46_123 };

        var result = CliTestRuntimeOptionsResolver.WithAvailablePorts(options);

        Assert.Equal(46_123, result.DiscoveryPort);
        Assert.NotEqual(result.DiscoveryPort, result.WebSocketPort);
    }

    private static CliRuntimeOptions CreateOptions()
        => new(
            DataDirectory: "/tmp/ansight-cli-test",
            AdbPath: null,
            XcodePath: null,
            SecureStorageFilePath: null,
            SecureStorageKeyFilePath: null,
            DiscoveryPort: null,
            WebSocketPort: null,
            EnableRepositoryAutomations: false,
            AutomationRepositoryPaths: Array.Empty<string>(),
            JavaScriptExecutablePath: "node");
}
