using Ansight.RemoteSimulator.Core.Server;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class RemoteControlServerSdpTests
{
    [Theory]
    [InlineData("v=0\r\na=ice-pwd:value", "v=0\r\na=ice-pwd:value\r\n")]
    [InlineData("v=0\r\n", "v=0\r\n")]
    [InlineData("v=0\n", "v=0\r\n")]
    public void TerminateSdp_ProducesBrowserCompatibleCrLfTermination(string input, string expected)
        => Assert.Equal(expected, RemoteControlServer.TerminateSdp(input));
}
