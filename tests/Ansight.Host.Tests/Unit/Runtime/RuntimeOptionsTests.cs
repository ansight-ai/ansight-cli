using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RuntimeOptionsTests
{
    [Fact]
    public void Constructor_WithCompletePortProfile_AppliesAndRestoresHostEndpoints()
    {
        using var environment = new TestEnvironment();
        var original = CaptureProtocolPorts();
        RuntimeCoordinator? runtime = null;

        try
        {
            runtime = new RuntimeCoordinator(new RuntimeOptions
            {
                BaseFolderPath = environment.RootPath,
                SecureStorageFilePath = environment.SecureStorageFilePath,
                SecureStorageKeyFilePath = environment.SecureStorageKeyFilePath,
                DiscoveryPort = 46123,
                WebSocketPort = 46124,
                WebSocketSessionPortRangeStart = 56600,
                WebSocketSessionPortRangeEnd = 56699
            });

            Assert.Equal(46123, ProtocolDefaults.DiscoveryPort);
            Assert.Equal(46124, ProtocolDefaults.WebSocketPort);
            Assert.Equal(56600, ProtocolDefaults.WebSocketSessionPortRangeStart);
            Assert.Equal(56699, ProtocolDefaults.WebSocketSessionPortRangeEnd);
        }
        finally
        {
            runtime?.Dispose();
        }

        Assert.Equal(original, CaptureProtocolPorts());
    }

    [Fact]
    public void Constructor_WithOnlyOneSessionRangeBoundary_RejectsTheProfile()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RuntimeCoordinator(new RuntimeOptions
        {
            WebSocketSessionPortRangeStart = 56600
        }));

        Assert.Contains("both a start and an end", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithOverlappingFixedAndSessionPorts_RejectsTheProfile()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RuntimeCoordinator(new RuntimeOptions
        {
            WebSocketPort = 56625,
            WebSocketSessionPortRangeStart = 56600,
            WebSocketSessionPortRangeEnd = 56699
        }));

        Assert.Contains("must not overlap", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Constructor_WithInvalidPort_RejectsTheProfile(int invalidPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeCoordinator(new RuntimeOptions
        {
            DiscoveryPort = invalidPort
        }));
    }

    private static ProtocolPortSnapshot CaptureProtocolPorts()
    {
        return new ProtocolPortSnapshot(
            ProtocolDefaults.DiscoveryPort,
            ProtocolDefaults.WebSocketPort,
            ProtocolDefaults.WebSocketSessionPortRangeStart,
            ProtocolDefaults.WebSocketSessionPortRangeEnd);
    }

    private sealed record ProtocolPortSnapshot(
        int DiscoveryPort,
        int WebSocketPort,
        int WebSocketSessionPortRangeStart,
        int WebSocketSessionPortRangeEnd);
}
