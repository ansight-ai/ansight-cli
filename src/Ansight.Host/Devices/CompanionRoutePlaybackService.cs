using System.Text.Json.Nodes;
using System.Xml;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Host.Devices;

internal sealed class CompanionRoutePlaybackService
{
    private readonly DeviceLocationPlaybackService playback;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock;
    private RouteLease? active;
    private CancellationTokenSource? monitorCancellation;

    public CompanionRoutePlaybackService(DeviceLocationPlaybackService playback, TimeProvider? clock = null)
    {
        this.playback = playback;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<RemoteDeviceTestingResult> ExecuteAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (request.Action == "route-start")
            {
                return await StartAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var matches = active is not null
                && active.RunId == request.RunId
                && active.DeviceId == request.DeviceUdid;

            if (!matches)
            {
                return new RemoteDeviceTestingResult(false, "No matching companion route is active.");
            }

            if (request.Action == "route-stop")
            {
                await StopActiveAsync(cancellationToken).ConfigureAwait(false);

                return new RemoteDeviceTestingResult(true, "Route playback stopped. The device remains at its last simulated location.");
            }

            active = active! with
            {
                LastHeartbeat = clock.GetUtcNow()
            };

            return Describe();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemoteDeviceTestingResult> StartAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken)
    {
        var valid = request.Platform is "ios" or "android"
            && !string.IsNullOrWhiteSpace(request.RouteContent)
            && Encoding.UTF8.GetByteCount(request.RouteContent) <= 40_000;

        if (!valid)
        {
            return new RemoteDeviceTestingResult(false, "Choose a simulator or emulator and a GPX route smaller than 40 KB.");
        }

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.RouteContent!));
            var route = DeviceLocationRouteParser.Parse("companion-route.gpx", stream);

            if (route.Points.Count > 500)
            {
                return new RemoteDeviceTestingResult(false, "Companion routes support up to 500 points.");
            }

            var result = await playback.StartAsync(new DeviceLocationPlaybackRequest(
                request.Platform!, request.DeviceUdid, "companion-route.gpx", request.RouteContent!,
                request.RecordedTiming ? DeviceLocationPlaybackMode.RecordedTiming : DeviceLocationPlaybackMode.FixedSpeed,
                FixedSpeedKph: request.SpeedKph), cancellationToken, replaceActive: false).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return new RemoteDeviceTestingResult(false, result.Message);
            }

            monitorCancellation?.Cancel();
            active = new RouteLease(playback.GetSnapshot().RunId!, request.DeviceUdid,
                clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(15));

            var cancellation = new CancellationTokenSource();
            monitorCancellation = cancellation;
            _ = MonitorAsync(cancellation, active.RunId);

            return Describe();
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or XmlException)
        {
            return new RemoteDeviceTestingResult(false, error.Message);
        }
    }

    private RemoteDeviceTestingResult Describe()
    {
        var snapshot = playback.GetSnapshot();

        if (active is not null && snapshot.RunId != active.RunId)
        {
            return new RemoteDeviceTestingResult(true, "This companion route has ended.", new JsonObject
            {
                ["runId"] = active.RunId,
                ["isPlaying"] = false,
                ["status"] = "stopped"
            });
        }

        return new RemoteDeviceTestingResult(true, snapshot.Message,
            JsonSerializer.SerializeToNode(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject());
    }

    internal async Task CheckTimeoutAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (active is null || monitorCancellation is null)
            {
                return;
            }

            var now = clock.GetUtcNow();
            var shouldStop = now >= active.ExpiresAt
                || now - active.LastHeartbeat >= TimeSpan.FromSeconds(15)
                || !playback.GetSnapshot().IsPlaying;

            if (shouldStop)
            {
                await StopActiveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopActiveAsync(CancellationToken cancellationToken)
    {
        if (active is not null)
        {
            await playback.StopAsync(cancellationToken, expectedRunId: active.RunId).ConfigureAwait(false);
        }

        monitorCancellation?.Cancel();
        monitorCancellation = null;
    }

    private async Task MonitorAsync(CancellationTokenSource cancellation, string runId)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellation.Token).ConfigureAwait(false);
                await CheckTimeoutAsync(cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Companion route monitor stopped: {error.Message}");

            try
            {
                await playback.StopAsync(expectedRunId: runId).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Host shutdown already disposes and stops playback.
            }
        }
        finally
        {
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (ReferenceEquals(monitorCancellation, cancellation))
                {
                    monitorCancellation = null;
                }
            }
            finally
            {
                gate.Release();
                cancellation.Dispose();
            }
        }
    }

    private sealed record RouteLease(string RunId, string DeviceId, DateTimeOffset LastHeartbeat, DateTimeOffset ExpiresAt);
}
