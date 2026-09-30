using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.RemoteSimulator.Core.WebRtc;

public sealed class SelectiveSimulatorWebRtcSessionFactory : ISimulatorWebRtcSessionFactory
{
    private readonly IRemoteRuntimeSource runtimeSource;
    private readonly ISimulatorWebRtcSessionFactory? simulatorFactory;
    private readonly ISimulatorWebRtcSessionFactory? androidFactory;
    private readonly ISimulatorWebRtcSessionFactory? macFactory;

    public SelectiveSimulatorWebRtcSessionFactory(
        IRemoteRuntimeSource runtimeSource,
        ISimulatorWebRtcSessionFactory simulatorFactory)
        : this(runtimeSource, simulatorFactory, androidFactory: null, macFactory: null)
    {
    }

    public SelectiveSimulatorWebRtcSessionFactory(
        IRemoteRuntimeSource runtimeSource,
        ISimulatorWebRtcSessionFactory? simulatorFactory,
        ISimulatorWebRtcSessionFactory? androidFactory)
        : this(runtimeSource, simulatorFactory, androidFactory, macFactory: null)
    {
    }

    public SelectiveSimulatorWebRtcSessionFactory(
        IRemoteRuntimeSource runtimeSource,
        ISimulatorWebRtcSessionFactory? simulatorFactory,
        ISimulatorWebRtcSessionFactory? androidFactory,
        ISimulatorWebRtcSessionFactory? macFactory)
    {
        this.runtimeSource = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
        if (simulatorFactory is null && androidFactory is null && macFactory is null)
        {
            throw new ArgumentException("At least one WebRTC runtime factory is required.");
        }

        this.simulatorFactory = simulatorFactory;
        this.androidFactory = androidFactory;
        this.macFactory = macFactory;
    }

    public string BackendName => string.Join(
        '+',
        new[] { simulatorFactory?.BackendName, androidFactory?.BackendName, macFactory?.BackendName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public string Status => string.Join(
        ' ',
        new[]
        {
            simulatorFactory is null ? null : $"iOS: {simulatorFactory.Status}",
            androidFactory is null ? null : $"Android: {androidFactory.Status}",
            macFactory is null ? null : $"macOS: {macFactory.Status}",
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public bool SupportsDevice(string deviceUdid)
        => ResolveFactory(deviceUdid)?.SupportsDevice(deviceUdid) == true;

    public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
    {
        var factory = ResolveFactory(deviceUdid);
        return factory?.SupportsDevice(deviceUdid) == true
            ? factory.Create(deviceUdid, framesPerSecond)
            : throw new NotSupportedException("WebRTC video is not available for this runtime.");
    }

    private ISimulatorWebRtcSessionFactory? ResolveFactory(string deviceUdid)
    {
        var device = runtimeSource.Current.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.Identifier, deviceUdid, StringComparison.OrdinalIgnoreCase));
        return device?.Platform switch
        {
            "ios" => simulatorFactory,
            "android" => androidFactory,
            "macos" => macFactory,
            _ => null,
        };
    }
}
