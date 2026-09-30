using Ansight.Adb;
using Ansight.Host;
using Ansight.SimCtl;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Simulator.Android;
using Ansight.RemoteSimulator.Core.Simulator.Apple;
using Ansight.RemoteSimulator.Core.Streaming;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliLocalSimulatorHost : IAsyncDisposable
{
    private readonly RuntimeCoordinator runtime;
    private readonly RuntimeOptions options;
    private readonly Action<string> reportStatus;
    private readonly List<IAsyncDisposable> trackedRuntimes = [];
    private readonly List<IDisposable> frameResources = [];
    private HeadlessHostDeviceDriver? deviceDriver;
    private RoutedSimulatorInputSink? inputSink;
    private RemoteControlServer? server;
    private readonly IHostedSimulatorAccess? hostedAccess;
    private bool disposed;

    private CliLocalSimulatorHost(
        RuntimeCoordinator runtime,
        RuntimeOptions options,
        Action<string> reportStatus, CompanionAccessMode mode)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));
        hostedAccess = mode == CompanionAccessMode.Disabled ? null
            : Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IHostedSimulatorAccess>("Simulator.CreateHostedAccess", [runtime, mode]);
    }

    public CompanionAccessStatus AccessStatus => runtime.ActiveCompanion?.GetAccessStatus()
        ?? new(CompanionAccessMode.Disabled, false, true, "Local simulator control is ready.", 0);

    public static async Task<CliLocalSimulatorHost> StartAsync(
        RuntimeCoordinator runtime,
        RuntimeOptions options,
        CompanionAccessMode mode,
        Action<string> reportStatus,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "CLI companion hosting currently requires macOS for simulator video and input.");
        }

        var host = new CliLocalSimulatorHost(runtime, options, reportStatus, mode);
        try
        {
            await host.StartCoreAsync(mode, cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (hostedAccess is not null) await hostedAccess.DisposeAsync().ConfigureAwait(false);
        runtime.LocalSimulatorControl = null;

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            server = null;
        }

        if (inputSink is not null)
        {
            await inputSink.DisposeAsync().ConfigureAwait(false);
            inputSink = null;
        }

        foreach (var trackedRuntime in trackedRuntimes.AsEnumerable().Reverse())
        {
            await trackedRuntime.DisposeAsync().ConfigureAwait(false);
        }
        trackedRuntimes.Clear();

        foreach (var frameResource in frameResources.AsEnumerable().Reverse())
        {
            frameResource.Dispose();
        }
        frameResources.Clear();

        deviceDriver?.Dispose();
        deviceDriver = null;
    }

    private async Task StartCoreAsync(
        CompanionAccessMode mode,
        CancellationToken cancellationToken)
    {
        var runtimeSources = new List<IRemoteRuntimeSource>();
        var frameSources = new Dictionary<string, ISimulatorFrameSource>(StringComparer.Ordinal);
        var inputSinks = new Dictionary<string, ISimulatorInputSink>(StringComparer.Ordinal);
        ISimulatorWebRtcSessionFactory? iosWebRtcFactory = null;
        ISimulatorWebRtcSessionFactory? androidWebRtcFactory = null;
        var startupMessages = new List<string>();

        var simCtlResolution = await SimCtlToolLocator.ResolveAsync(
            options.XcodePath,
            cancellationToken).ConfigureAwait(false);
        if (simCtlResolution.IsFound)
        {
            var simulatorTracker = new SimulatorTracker(new SimCtlClient(simCtlResolution));
            await simulatorTracker.StartAsync(cancellationToken).ConfigureAwait(false);
            trackedRuntimes.Add(simulatorTracker);
            runtimeSources.Add(new SimulatorRuntimeSource(simulatorTracker));

            var simulatorFrameSource = new SimCtlFrameSource(simCtlResolution);
            frameResources.Add(simulatorFrameSource);
            frameSources[DevicePlatforms.Ios] = simulatorFrameSource;
            inputSinks[DevicePlatforms.Ios] = new CliSimulatorHidInputSink(
                simCtlResolution.DeveloperDirectory);
            iosWebRtcFactory = new CliSimulatorWebRtcSessionFactory(
                simCtlResolution.DeveloperDirectory);
            startupMessages.Add("iOS Simulator WebRTC is ready.");
        }
        else
        {
            startupMessages.Add($"iOS unavailable: {simCtlResolution.Message}");
        }

        var adbResolution = AdbToolLocator.Resolve(options.AdbPath);
        if (adbResolution.IsFound)
        {
            var adbClient = new AdbClient(adbResolution.AdbPath);
            var emulatorClient = new AdbAndroidEmulatorClient(adbClient);
            var emulatorTracker = new AndroidEmulatorTracker(emulatorClient);
            await emulatorTracker.StartAsync(cancellationToken).ConfigureAwait(false);
            trackedRuntimes.Add(emulatorTracker);
            runtimeSources.Add(emulatorTracker);

            var emulatorController = new AndroidEmulatorGrpcController();
            var androidFrameSource = new AndroidEmulatorFrameSource(
                emulatorClient,
                emulatorController);
            frameResources.Add(androidFrameSource);
            frameSources[DevicePlatforms.Android] = androidFrameSource;
            inputSinks[DevicePlatforms.Android] = new AndroidEmulatorInputSink(
                emulatorClient,
                emulatorTracker.FindDevice,
                emulatorController);

            var scrcpyResolution = await ScrcpyToolLocator.ResolveAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (scrcpyResolution.IsFound)
            {
                androidWebRtcFactory = new CliAndroidWebRtcSessionFactory(
                    adbClient,
                    scrcpyResolution);
                startupMessages.Add($"Android Emulator WebRTC is ready through scrcpy {scrcpyResolution.Version}.");
            }
            else
            {
                startupMessages.Add($"Android video unavailable: {scrcpyResolution.Message}");
            }
        }
        else
        {
            startupMessages.Add($"Android unavailable: {adbResolution.Message}");
        }

        if (runtimeSources.Count == 0)
        {
            throw new InvalidOperationException(string.Join(' ', startupMessages));
        }
        if (iosWebRtcFactory is null && androidWebRtcFactory is null)
        {
            throw new InvalidOperationException(
                "No companion WebRTC video backend is available. " + string.Join(' ', startupMessages));
        }

        var runtimeSource = runtimeSources.Count == 1
            ? runtimeSources[0]
            : new CompositeRemoteRuntimeSource(runtimeSources);
        var frameSource = new RoutedSimulatorFrameSource(runtimeSource, frameSources);
        inputSink = new RoutedSimulatorInputSink(runtimeSource, inputSinks);
        var webRtcFactory = new SelectiveSimulatorWebRtcSessionFactory(
            runtimeSource,
            iosWebRtcFactory,
            androidWebRtcFactory);
        deviceDriver = new HeadlessHostDeviceDriver(runtime.Devices, options);
        var lifecycleSource = new CliRemoteDeviceLifecycleSource(deviceDriver);
        var annotationSource = new CliRemoteAnnotationSource(runtime);
        var locationSource = new CliRemoteDeviceLocationSource(runtime.Devices, runtimeSource);

        server = new RemoteControlServer(
            runtimeSource,
            frameSource,
            inputSink,
            requestedPort: 0,
            webRtcSessionFactory: webRtcFactory,
            accessAuthorizer: hostedAccess?.Authorizer,
            allowUnauthenticatedLoopback: true,
            deviceLifecycleSource: lifecycleSource,
            annotationSource: annotationSource,
            deviceLocationSource: locationSource);
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        reportStatus($"Companion simulator host is ready on local port {server.Port}. {string.Join(' ', startupMessages)}");

        runtime.LocalSimulatorControl = server;
        if (hostedAccess is not null)
        {
            var access = await hostedAccess.StartAsync(server, cancellationToken).ConfigureAwait(false);
            reportStatus($"Companion access ({FormatMode(access.Mode)}): {access.Status}");
        }

    }

    private static string FormatMode(CompanionAccessMode mode)
        => mode switch
        {
            CompanionAccessMode.Session => "session",
            CompanionAccessMode.Always => "always",
            _ => "disabled"
        };
}
