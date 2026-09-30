using Ansight.Adb;
using Ansight.SimulatorRtc.Mac;
using Ansight.RemoteSimulator.Core.Simulator.Android;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliAndroidWebRtcSessionFactory : ISimulatorWebRtcSessionFactory
{
    private readonly AdbClient adbClient;
    private readonly ScrcpyToolResolution toolResolution;
    private string? lastStreamFailure;

    public CliAndroidWebRtcSessionFactory(
        AdbClient adbClient,
        ScrcpyToolResolution toolResolution)
    {
        this.adbClient = adbClient ?? throw new ArgumentNullException(nameof(adbClient));
        this.toolResolution = toolResolution ?? throw new ArgumentNullException(nameof(toolResolution));
    }

    public string BackendName => "scrcpy-mediacodec-h264-webrtc-cli";

    public string Status => Volatile.Read(ref lastStreamFailure) is { Length: > 0 } failure
        ? $"The last Android companion stream failed: {failure}"
        : $"scrcpy {toolResolution.Version} H.264 WebRTC transport is ready.";

    public bool SupportsDevice(string deviceUdid)
        => toolResolution.IsFound && !string.IsNullOrWhiteSpace(deviceUdid);

    public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
    {
        Volatile.Write(ref lastStreamFailure, null);
        return new CliAndroidWebRtcSession(
            adbClient,
            toolResolution,
            deviceUdid,
            framesPerSecond,
            failure => Volatile.Write(ref lastStreamFailure, failure));
    }
}

internal sealed class CliAndroidWebRtcSession : ISimulatorWebRtcSession
{
    private readonly ExternalH264RtcSession rtcSession;
    private readonly ScrcpyVideoStream videoStream;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly Lock stateGate = new();
    private readonly Action<string> reportFailure;
    private Task? videoTask;
    private bool disposed;

    public CliAndroidWebRtcSession(
        AdbClient adbClient,
        ScrcpyToolResolution toolResolution,
        string deviceUdid,
        int framesPerSecond,
        Action<string> reportFailure)
    {
        DeviceUdid = deviceUdid;
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        rtcSession = new ExternalH264RtcSession(deviceUdid, framesPerSecond);
        rtcSession.InputReceived += HandleInputReceived;
        videoStream = new ScrcpyVideoStream(
            adbClient,
            toolResolution,
            deviceUdid,
            framesPerSecond);
    }

    public event EventHandler<WebRtcInputMessageEventArgs>? InputReceived;

    public string DeviceUdid { get; }

    public async Task<WebRtcSessionDescription> CreateAnswerAsync(
        WebRtcSessionDescription offer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var answer = await rtcSession.CreateAnswerAsync(
            new SimulatorRtcDescription(offer.Type, offer.Sdp),
            cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            videoTask ??= RunVideoAsync(lifetimeCancellation.Token);
        }
        return new WebRtcSessionDescription(answer.Type, answer.Sdp);
    }

    public bool TrySendMessage(string message) => rtcSession.TrySendMessage(message);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        rtcSession.InputReceived -= HandleInputReceived;
        await lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        await videoStream.DisposeAsync().ConfigureAwait(false);
        Task? activeVideoTask;
        lock (stateGate)
        {
            activeVideoTask = videoTask;
        }

        if (activeVideoTask is not null)
        {
            try
            {
                await activeVideoTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal shutdown path.
            }
        }

        rtcSession.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task RunVideoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await videoStream.RunAsync(SendPacketAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            reportFailure(exception.Message);
            rtcSession.Dispose();
        }
    }

    private async Task SendPacketAsync(
        ScrcpyVideoPacket packet,
        CancellationToken cancellationToken)
    {
        if (!packet.IsKeyFrame)
        {
            rtcSession.TrySendAccessUnit(
                packet.AccessUnit,
                packet.PresentationTimestampMicroseconds,
                isKeyFrame: false);
            return;
        }

        using var readyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        while (!rtcSession.TrySendAccessUnit(
                   packet.AccessUnit,
                   packet.PresentationTimestampMicroseconds,
                   isKeyFrame: true))
        {
            await Task.Delay(10, readyCancellation.Token).ConfigureAwait(false);
        }
    }

    private void HandleInputReceived(object? sender, SimulatorRtcInputEventArgs args)
        => InputReceived?.Invoke(this, new WebRtcInputMessageEventArgs(args.Message));
}
