namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentWebSocketOptionsTests
{
    [Fact]
    public void Resolve_DefaultsToTokenCompactionWithoutAnExtraWarmupRoundTrip()
        => Assert.Equal(new SimulatorAgentWebSocketOptions(32_000, false),
            SimulatorAgentWebSocketOptions.Resolve(null, _ => null));

    [Fact]
    public void Resolve_UsesEnvironmentTuningUnlessExplicitOptionsAreSupplied()
    {
        static string? ReadEnvironment(string name) => name switch
        {
            SimulatorAgentWebSocketOptions.CompactThresholdEnvironmentVariable => "64000",
            SimulatorAgentWebSocketOptions.WarmupEnvironmentVariable => "true",
            _ => null
        };
        Assert.Equal(new SimulatorAgentWebSocketOptions(64_000, true),
            SimulatorAgentWebSocketOptions.Resolve(null, ReadEnvironment));
        var configured = new SimulatorAgentWebSocketOptions(16_000, false);
        Assert.Equal(configured, SimulatorAgentWebSocketOptions.Resolve(configured, ReadEnvironment));
    }

    [Theory]
    [InlineData("ANSIGHT_OPENAI_COMPACT_THRESHOLD_TOKENS", "zero")]
    [InlineData("ANSIGHT_OPENAI_COMPACT_THRESHOLD_TOKENS", "0")]
    [InlineData("ANSIGHT_OPENAI_WEBSOCKET_WARMUP", "maybe")]
    public void Resolve_RejectsInvalidTuning(string variable, string value)
        => Assert.ThrowsAny<ArgumentException>(() =>
            SimulatorAgentWebSocketOptions.Resolve(null, name => name == variable ? value : null));
}
