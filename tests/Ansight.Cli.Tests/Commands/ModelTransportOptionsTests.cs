using Ansight.Cli.Commands;

namespace Ansight.Cli.Tests.Commands;

public sealed class ModelTransportOptionsTests
{
    public static IEnumerable<object[]> AgentCommands()
    {
        yield return ["app execute"];
        yield return ["test run /workspace sample-test"];
        yield return ["test run-inline"];
        yield return ["test run-all /workspace"];
        yield return ["tests run-all /workspace"];
        yield return ["replay ansight session-1"];
        yield return ["app-graph run graph-1 session-1"];
        yield return ["app-graph explore com.example.app"];
    }

    [Theory]
    [InlineData("auto", SimulatorAgentOpenAiProtocol.Auto)]
    [InlineData("http", SimulatorAgentOpenAiProtocol.Http)]
    [InlineData("websocket", SimulatorAgentOpenAiProtocol.WebSocket)]
    [InlineData("ws", SimulatorAgentOpenAiProtocol.WebSocket)]
    public void ResolveModelTransport_ParsesSupportedValues(
        string value,
        SimulatorAgentOpenAiProtocol expected)
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "--model-transport", value]);

        Assert.Equal(expected, ModelTransportOptions.ResolveModelTransport(arguments));
    }

    [Fact]
    public void ResolveModelTransport_DefaultsToAuto()
    {
        var arguments = CliArguments.Parse(["app", "execute"]);

        Assert.Equal(
            SimulatorAgentOpenAiProtocol.Auto,
            ModelTransportOptions.ResolveModelTransport(arguments));
    }

    [Fact]
    public void ResolveModelTransport_RejectsUnknownValue()
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "--model-transport", "invalid"]);

        var exception = Assert.Throws<CliUsageException>(() =>
            ModelTransportOptions.ResolveModelTransport(arguments));

        Assert.Contains("--model-transport must be auto, websocket, or http", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AgentCommands))]
    public async Task InvalidAgentOptionsAreRejectedBeforeForwarding(string command)
    {
        foreach (var option in new[]
                 {
                     "--openai-transport=websocket",
                     "--model-transport=invalid",
                     "--model-transport=",
                     "--model-transport"
                 })
        {
            using var output = new StringWriter();
            var forwarded = false;
            var result = await CliApplication.RunParsedAsync(
                CliArguments.Parse([.. command.Split(' '), option, "--json"]),
                new CliOutput(true, output, output),
                CancellationToken.None,
                allowResidentHostForwarding: true,
                accessAuthorizer: TestAccessAuthorizer.Allow,
                residentHostForwarder: (_, _, _) =>
                {
                    forwarded = true;
                    return Task.FromResult<int?>(CliExitCodes.Success);
                });

            Assert.Equal(CliExitCodes.Usage, result);
            Assert.False(forwarded);
            Assert.Contains(option.Split('=')[0], output.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(AgentCommands))]
    public async Task ModelTransportIsForwardedToResidentHost(string command)
    {
        using var output = new StringWriter();
        CliArguments? forwarded = null;
        var result = await CliApplication.RunParsedAsync(
            CliArguments.Parse([.. command.Split(' '), "--model-transport", "websocket", "--json"]),
            new CliOutput(true, output, output),
            CancellationToken.None,
            allowResidentHostForwarding: true,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            residentHostForwarder: (arguments, _, _) =>
            {
                forwarded = arguments;
                return Task.FromResult<int?>(CliExitCodes.Success);
            });

        Assert.Equal(CliExitCodes.Success, result);
        Assert.NotNull(forwarded);
        Assert.Equal(SimulatorAgentOpenAiProtocol.WebSocket, ModelTransportOptions.ResolveModelTransport(forwarded));
    }
}
