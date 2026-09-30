using System.Text;
using System.Xml;

namespace Ansight.Host.Devices.Location;

public sealed class DeviceLocationPlaybackService : IAsyncDisposable
{
    private readonly IDeviceLocationService devices;
    private readonly Lock stateGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private CancellationTokenSource? playbackCancellation;
    private Task? playbackTask;
    private DeviceLocationPlaybackSnapshot snapshot = CreateIdleSnapshot();
    private bool disposed;

    public DeviceLocationPlaybackService(IDeviceLocationService devices)
    {
        this.devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    public DeviceLocationPlaybackSnapshot GetSnapshot()
    {
        lock (stateGate)
        {
            return snapshot;
        }
    }

    public async Task<DeviceLocationPlaybackStartResult> StartAsync(
        DeviceLocationPlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        DeviceLocationRoute route;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.RouteContent));
            route = DeviceLocationRouteParser.Parse(request.SourceFileName, stream);
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            return new DeviceLocationPlaybackStartResult(false, exception.Message, GetSnapshot());
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopActivePlaybackAsync().ConfigureAwait(false);
            var firstPoint = route.Points[0];
            var firstResult = await devices.SetLocationAsync(
                    request.Platform,
                    request.DeviceIdentifier,
                    firstPoint.Latitude,
                    firstPoint.Longitude,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!firstResult.IsSuccess)
            {
                return new DeviceLocationPlaybackStartResult(false, firstResult.Message, GetSnapshot());
            }

            var runId = Guid.NewGuid().ToString("N");
            var startedUtc = DateTimeOffset.UtcNow;
            var cancellation = new CancellationTokenSource();
            lock (stateGate)
            {
                snapshot = new DeviceLocationPlaybackSnapshot(
                    runId,
                    true,
                    "playing",
                    $"Replaying {route.SourceFileName}.",
                    request.Platform,
                    request.DeviceIdentifier,
                    route.SourceFileName,
                    route.Points.Count,
                    0,
                    route.DistanceMeters,
                    route.RecordedDuration,
                    ResolveMode(request, route),
                    request.PlaybackSpeedMultiplier,
                    request.FixedSpeedKph,
                    request.Loop,
                    firstPoint,
                    startedUtc,
                    null);
                playbackCancellation = cancellation;
                playbackTask = RunPlaybackAsync(runId, route, request, cancellation);
            }

            return new DeviceLocationPlaybackStartResult(true, firstResult.Message, GetSnapshot());
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<OperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var wasPlaying = GetSnapshot().IsPlaying;
            await StopActivePlaybackAsync().ConfigureAwait(false);
            return wasPlaying
                ? OperationResult.Success("Location route replay stopped.")
                : OperationResult.Failure("No location route is currently playing.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await StopActivePlaybackAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
            operationGate.Dispose();
        }
    }

    private async Task RunPlaybackAsync(
        string runId,
        DeviceLocationRoute route,
        DeviceLocationPlaybackRequest request,
        CancellationTokenSource cancellation)
    {
        try
        {
            do
            {
                for (var index = 1; index < route.Points.Count; index++)
                {
                    var start = route.Points[index - 1];
                    var end = route.Points[index];
                    var duration = ResolveSegmentDuration(start, end, request, route);
                    var stepCount = (int)Math.Clamp(Math.Ceiling(duration.TotalSeconds), 1d, int.MaxValue);
                    var stepDuration = TimeSpan.FromTicks(Math.Max(1L, duration.Ticks / stepCount));
                    for (var step = 1; step <= stepCount; step++)
                    {
                        await Task.Delay(stepDuration, cancellation.Token).ConfigureAwait(false);
                        var location = DeviceLocationMath.Interpolate(start, end, (double)step / stepCount);
                        var result = await devices.SetLocationAsync(
                                request.Platform,
                                request.DeviceIdentifier,
                                location.Latitude,
                                location.Longitude,
                                cancellation.Token)
                            .ConfigureAwait(false);
                        if (!result.IsSuccess)
                        {
                            throw new InvalidOperationException(result.Message);
                        }

                        UpdateProgress(runId, index, location);
                    }
                }
            }
            while (request.Loop && !cancellation.IsCancellationRequested);

            Complete(runId, "completed", "Location route replay complete.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Complete(runId, "stopped", "Location route replay stopped.");
        }
        catch (Exception exception)
        {
            Complete(runId, "failed", exception.Message);
        }
        finally
        {
            lock (stateGate)
            {
                if (string.Equals(snapshot.RunId, runId, StringComparison.Ordinal))
                {
                    playbackCancellation = null;
                    playbackTask = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task StopActivePlaybackAsync()
    {
        CancellationTokenSource? cancellation;
        Task? activeTask;
        lock (stateGate)
        {
            cancellation = playbackCancellation;
            activeTask = playbackTask;
        }

        if (cancellation is null || activeTask is null)
        {
            return;
        }

        cancellation.Cancel();
        await activeTask.ConfigureAwait(false);
    }

    private void UpdateProgress(string runId, int pointIndex, DeviceLocationPoint location)
    {
        lock (stateGate)
        {
            if (!string.Equals(snapshot.RunId, runId, StringComparison.Ordinal))
            {
                return;
            }

            snapshot = snapshot with
            {
                CurrentPointIndex = pointIndex,
                CurrentLocation = location
            };
        }
    }

    private void Complete(string runId, string status, string message)
    {
        lock (stateGate)
        {
            if (!string.Equals(snapshot.RunId, runId, StringComparison.Ordinal))
            {
                return;
            }

            snapshot = snapshot with
            {
                IsPlaying = false,
                Status = status,
                Message = message,
                CompletedUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private static TimeSpan ResolveSegmentDuration(
        DeviceLocationPoint start,
        DeviceLocationPoint end,
        DeviceLocationPlaybackRequest request,
        DeviceLocationRoute route)
    {
        if (ResolveMode(request, route) == DeviceLocationPlaybackMode.RecordedTiming
            && start.Timestamp is { } startTime
            && end.Timestamp is { } endTime
            && endTime > startTime)
        {
            return TimeSpan.FromTicks((long)((endTime - startTime).Ticks / request.PlaybackSpeedMultiplier));
        }

        var speedMetersPerSecond = request.FixedSpeedKph / 3.6d;
        var seconds = DeviceLocationMath.DistanceMeters(start, end) / speedMetersPerSecond;
        return TimeSpan.FromSeconds(Math.Max(0.05d, seconds));
    }

    private static DeviceLocationPlaybackMode ResolveMode(
        DeviceLocationPlaybackRequest request,
        DeviceLocationRoute route)
        => request.Mode == DeviceLocationPlaybackMode.RecordedTiming && route.HasRecordedTiming
            ? DeviceLocationPlaybackMode.RecordedTiming
            : DeviceLocationPlaybackMode.FixedSpeed;

    private static void ValidateRequest(DeviceLocationPlaybackRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RouteContent);
        if (!double.IsFinite(request.PlaybackSpeedMultiplier)
            || request.PlaybackSpeedMultiplier is < 0.1d or > 100d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Playback speed multiplier must be between 0.1 and 100.");
        }

        if (!double.IsFinite(request.FixedSpeedKph)
            || request.FixedSpeedKph is < 0.5d or > 500d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Fixed speed must be between 0.5 and 500 km/h.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static DeviceLocationPlaybackSnapshot CreateIdleSnapshot()
        => new(
            null,
            false,
            "idle",
            "No location route is playing.",
            null,
            null,
            null,
            0,
            0,
            0d,
            null,
            DeviceLocationPlaybackMode.RecordedTiming,
            1d,
            30d,
            false,
            null,
            null,
            null);
}
