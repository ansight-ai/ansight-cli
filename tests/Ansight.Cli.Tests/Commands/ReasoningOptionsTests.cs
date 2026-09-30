using Ansight.Cli.Commands;

namespace Ansight.Cli.Tests.Commands;

public sealed class ReasoningOptionsTests
{
    [Theory]
    [InlineData("fast", "fast")]
    [InlineData("BALANCED", "balanced")]
    [InlineData(" deep ", "deep")]
    public void ResolveAcceptsSharedReasoningModes(string value, string expected)
    {
        var arguments = CliArguments.Parse(["app", "execute", "--reasoning", value]);

        Assert.Equal(expected, ReasoningOptions.Resolve(arguments));
        Assert.Equal(string.Empty, ReasoningOptions.ResolveModelOverride(arguments));
    }

    [Fact]
    public void DefaultLeavesModelSelectionToServer()
    {
        var arguments = CliArguments.Parse(["app", "execute"]);

        Assert.Equal("fast", ReasoningOptions.Resolve(arguments));
        Assert.Equal(string.Empty, ReasoningOptions.ResolveModelOverride(arguments));
    }

    [Fact]
    public void DiagnosticModelOverrideRemainsAvailable()
    {
        var arguments = CliArguments.Parse(["app", "execute", "--model", "custom-model"]);

        Assert.Equal("fast", ReasoningOptions.Resolve(arguments));
        Assert.Equal("custom-model", ReasoningOptions.ResolveModelOverride(arguments));
    }

    [Theory]
    [MemberData(nameof(ModelTransportOptionsTests.AgentCommands), MemberType = typeof(ModelTransportOptionsTests))]
    public async Task InvalidReasoningIsRejectedBeforeForwarding(string command)
    {
        foreach (var options in new string[][]
                 {
                     ["--reasoning=invalid"],
                     ["--reasoning="],
                     ["--reasoning"],
                     ["--reasoning", "fast", "--model", "custom-model"]
                 })
        {
            using var output = new StringWriter();
            var forwarded = false;
            var result = await CliApplication.RunParsedAsync(
                CliArguments.Parse([.. command.Split(' '), .. options, "--json"]),
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
            Assert.Contains("--reasoning", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(ModelTransportOptionsTests.AgentCommands), MemberType = typeof(ModelTransportOptionsTests))]
    public async Task ReasoningIsPreservedWhenForwarding(string command)
    {
        using var output = new StringWriter();
        CliArguments? forwarded = null;
        var result = await CliApplication.RunParsedAsync(
            CliArguments.Parse([.. command.Split(' '), "--reasoning", "deep", "--json"]),
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
        Assert.Equal("deep", ReasoningOptions.Resolve(forwarded));
        Assert.Equal(string.Empty, ReasoningOptions.ResolveModelOverride(forwarded));
    }
}
