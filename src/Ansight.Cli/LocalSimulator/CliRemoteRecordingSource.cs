using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Utilities;
using Ansight.RemoteSimulator.Core.Recording;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliRemoteRecordingSource : IRemoteRecordingSource
{
    private readonly RuntimeCoordinator runtime;

    public CliRemoteRecordingSource(RuntimeCoordinator runtime)
    {
        this.runtime = runtime;
    }

    public Task<IReadOnlyList<RemoteRecordingSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RemoteRecordingSummary> recordings = runtime.Sessions.GetSummaries()
            .OrderByDescending(session => session.CreatedUtc)
            .Select(Summarize)
            .ToArray();

        return Task.FromResult(recordings);
    }

    public async Task<RemoteRecordingDetails?> GetAsync(string recordingId, CancellationToken cancellationToken = default)
    {
        var session = await runtime.Sessions.LoadSnapshotAsync(recordingId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return null;
        }

        return new RemoteRecordingDetails(
            Summarize(session),
            session.Images.OrderBy(frame => frame.CapturedAtUtc).Select(frame => new RemoteRecordingFrameSummary(
                frame.FrameId, frame.CapturedAtUtc, frame.Width, frame.Height, ContentType(frame.Format), frame.ByteCount)).ToArray(),
            session.LogStreams.Select(stream => new RemoteRecordingLogStreamSummary(
                stream.StreamId, stream.Kind, stream.DisplayName, stream.Status, Math.Max(stream.TotalEntryCount, stream.Entries.Count))).ToArray(),
            session.MetricChannels.Select(Channel).ToArray());
    }

    public async Task<RemoteRecordingFrame?> GetFrameAsync(string recordingId, string frameId, CancellationToken cancellationToken = default)
    {
        var session = await runtime.Sessions.LoadSnapshotAsync(recordingId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var frame = session?.Images.FirstOrDefault(frame => frame.FrameId == frameId);

        if (session is null || frame is null)
        {
            return null;
        }

        return await ReadImageAsync(SessionFileLocator.ResolveScreenshotPath(runtime.ApplicationPaths, session, frame),
            ContentType(frame.Format), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRecordingFrame?> GetAppIconAsync(string recordingId, CancellationToken cancellationToken = default)
    {
        var session = await runtime.Sessions.LoadSnapshotAsync(recordingId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (session?.AppIcon is not { } icon)
        {
            return null;
        }

        var directory = SessionFileLocator.ResolveSessionDirectoryPath(runtime.ApplicationPaths, session);

        return await ReadImageAsync(SessionAppIconArtifactPath.ResolveSessionIconPath(directory, icon),
            ContentType(icon.Format), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRecordingEvidenceSlice?> GetEvidenceAsync(string recordingId, DateTimeOffset startUtc,
        DateTimeOffset endUtc, int maximumLogCount, int maximumMetricSampleCount, CancellationToken cancellationToken = default)
    {
        var session = await runtime.Sessions.LoadSnapshotAsync(recordingId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return null;
        }

        var logs = session.Logs.Where(log => log.TimestampUtc >= startUtc && log.TimestampUtc <= endUtc)
            .OrderBy(log => log.TimestampUtc).ToArray();
        var metrics = session.Metrics.Where(sample => sample.CapturedAtUtc >= startUtc && sample.CapturedAtUtc <= endUtc)
            .OrderBy(sample => sample.CapturedAtUtc).ToArray();
        var logLimit = Math.Clamp(maximumLogCount, 1, 1000);
        var metricLimit = Math.Clamp(maximumMetricSampleCount, 1, 10000);

        return new RemoteRecordingEvidenceSlice(startUtc, endUtc,
            logs.Take(logLimit).Select(log => new RemoteRecordingLogEntry(log.TimestampUtc, log.StreamId,
                log.Priority.ToString().ToLowerInvariant(), log.Source, log.Tag, log.Message, log.EventId)).ToArray(),
            session.Annotations.Where(annotation => annotation.StartUtc <= endUtc && (annotation.EndUtc ?? annotation.StartUtc) >= startUtc)
                .Select(Annotation).ToArray(),
            session.MetricChannels.Select(Channel).ToArray(),
            metrics.Take(metricLimit).Select(sample => new RemoteRecordingMetricSample(sample.ChannelId,
                (long)Math.Round(sample.Value), sample.CapturedAtUtc, sample.SegmentId)).ToArray(),
            logs.Length > logLimit, metrics.Length > metricLimit)
        {
            Touches = session.Touches.Where(touch => touch.CapturedAtUtc >= startUtc && touch.CapturedAtUtc <= endUtc)
                .Select(touch => new RemoteRecordingTouchInput(touch.Id, touch.Action, touch.CapturedAtUtc,
                    touch.PointerId, touch.PointerIndex, touch.PointerCount, touch.X, touch.Y, touch.NormalizedX, touch.NormalizedY,
                    touch.SurfaceWidth, touch.SurfaceHeight, touch.CoordinateSpace, touch.CoordinateUnit, touch.SurfaceScale)).ToArray()
        };
    }

    private RemoteRecordingSummary Summarize(AppSessionSnapshot session)
    {
        return new RemoteRecordingSummary(session.SessionId, session.Name ?? session.ClientName, session.ClientName,
            session.DeviceProfile?.Device?.Model ?? "", session.DeviceProfile?.Device?.OsName ?? "", session.Status,
            session.CreatedUtc, session.LastUpdatedUtc, Math.Max(0, (long)(session.LastUpdatedUtc - session.CreatedUtc).TotalMilliseconds),
            Math.Max(session.TotalImageCount, session.Images.Count), runtime.AppTools.IsConnected(session.SessionId),
            Math.Max(session.TotalLogCount, session.Logs.Count), Math.Max(session.TotalAnnotationCount, session.Annotations.Count),
            Math.Max(session.TotalMetricChannelCount, session.MetricChannels.Count), Math.Max(session.TotalMetricSampleCount, session.Metrics.Count))
        {
            AppId = session.AppId,
            AppIconCacheKey = session.AppIcon is null ? null : $"{session.SessionId}:{session.AppIcon.ByteCount}"
        };
    }

    private static RemoteRecordingMetricChannel Channel(SessionMetricChannel channel)
    {
        return new RemoteRecordingMetricChannel(channel.ChannelId, channel.Name, channel.ColorHex,
            channel.Unit, channel.Type, channel.Source, channel.Group, channel.Kind);
    }

    private static RemoteRecordingAnnotation Annotation(SessionAnnotation annotation)
    {
        return new RemoteRecordingAnnotation(annotation.AnnotationId, annotation.StartUtc, annotation.EndUtc,
            annotation.Label, annotation.Source, annotation.Notes, annotation.CaptureGroupId,
            annotation.Geometry.Select(geometry => new RemoteRecordingAnnotationGeometry(geometry.GeometryId, geometry.FrameId,
                geometry.CapturedAtUtc, geometry.Kind.ToString().ToLowerInvariant(), geometry.X, geometry.Y, geometry.Width, geometry.Height,
                geometry.Points.Select(point => new RemoteRecordingAnnotationPoint(point.X, point.Y)).ToArray(),
                geometry.Text, geometry.StrokeColor, geometry.StrokeWidth)).ToArray());
    }

    private static string ContentType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" or "image/jpeg" => "image/jpeg",
            "webp" or "image/webp" => "image/webp",
            _ => "image/png"
        };
    }

    private static async Task<RemoteRecordingFrame?> ReadImageAsync(string path, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);

            if (stream.Length is <= 0 or > 20 * 1024 * 1024)
            {
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

            return new RemoteRecordingFrame(contentType, bytes);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
