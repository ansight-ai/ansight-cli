using System.Buffers.Binary;
using System.IO.Compression;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipOutputStream = ICSharpCode.SharpZipLib.Zip.ZipOutputStream;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task StartAndStopAsync_EmitsLifecycleEvents()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var lifecycleEvents = new ConcurrentQueue<RuntimeLifecycleEvent>();
        runtime.LifecycleEventOccurred += (_, runtimeEvent) => lifecycleEvents.Enqueue(runtimeEvent);

        await runtime.StartAsync();
        await runtime.StopAsync();

        Assert.Contains(lifecycleEvents, runtimeEvent => runtimeEvent.Kind == RuntimeLifecycleEventKind.Started);
        Assert.Contains(lifecycleEvents, runtimeEvent => runtimeEvent.Kind == RuntimeLifecycleEventKind.Stopped);
    }

    [Fact(Timeout = 60000)]
    public async Task StartAsync_WhenUdpPortIsTaken_ReportsFriendlyWarningInsteadOfSharingPort()
    {
        using var environment = new TestEnvironment();
        using var occupiedSocket = new Socket(
            Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        if (occupiedSocket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            occupiedSocket.DualMode = true;
        }

        occupiedSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        occupiedSocket.Bind(new IPEndPoint(
            occupiedSocket.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any,
            ProtocolDefaults.DiscoveryPort));

        using var runtime = environment.CreateRuntime();
        var exception = await Record.ExceptionAsync(() => runtime.StartAsync());

        Assert.Null(exception);
        var warning = Assert.Single(runtime.StartupWarnings);
        Assert.Contains("enrollment listener", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"UDP port {ProtocolDefaults.DiscoveryPort}", warning, StringComparison.Ordinal);
        Assert.Contains("another app is already using that port", warning, StringComparison.OrdinalIgnoreCase);
        Assert.True(runtime.IsRunning);
    }
}
