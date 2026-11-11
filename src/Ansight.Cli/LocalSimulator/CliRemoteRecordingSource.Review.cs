using System.Net;
using System.Text.Json;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.RemoteSimulator.Core.Recording;

namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteRecordingSource
{
    private static readonly JsonSerializerOptions reviewJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RemoteRecordingReviewResult> ReviewAsync(
        string operation, string? recordingId, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ReviewError(HttpStatusCode.BadRequest, "The review arguments must be an object.");
        }

        try
        {
            if (operation == "trends.history")
            {
                var history = runtime.Trends.ListHistory(ReadText(arguments, "appId"), limit: 500, limitPerSeries: true);
                return ReviewJson(new
                {
                    metrics = history.Metrics.Take(5000).Select(metric => new
                    {
                        metric.EvaluationId, metric.RunId, metric.SessionId, metric.AppId, metric.AppVersion,
                        metric.BuildNumber, metric.Platform, metric.DeviceModel, metric.TrendsId,
                        metric.InstanceIndex, metric.MetricId, metric.MetricKey, metric.Value, metric.Unit, metric.SpanGroup,
                        status = metric.Status.ToString(), metric.EvaluatedAtUtc
                    }),
                    history = history.History.Take(5000).Select(entry => new
                    {
                        entry.EvaluationId, entry.SessionId, entry.AppId, entry.DecisionId, entry.MetricKey,
                        entry.DefinitionHash,
                        status = entry.Status.ToString(), entry.CurrentValue, entry.BaselineValue,
                        entry.AbsoluteDelta, entry.RelativeDeltaPercent, entry.Message, entry.Unit,
                        entry.EvaluatedAtUtc, entry.AppVersion, entry.SpanGroup
                    }),
                    charts = history.Charts.Select(chart => new
                    {
                        chart.DecisionId,
                        chart.MetricKey,
                        chart.Title,
                        chart.DefinitionHash
                    }),
                    isTruncated = history.Metrics.Count > 5000 || history.History.Count > 5000
                });
            }

            if (string.IsNullOrWhiteSpace(recordingId))
            {
                return ReviewError(HttpStatusCode.BadRequest, "A recording is required.");
            }

            var session = await runtime.Sessions.LoadSnapshotAsync(recordingId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return ReviewError(HttpStatusCode.NotFound, "The recording was not found.");
            }

            switch (operation)
            {
                case "network.list":
                    return ReviewJson(new
                    {
                        requests = session.NetworkRequests.OrderBy(request => request.StartedAtUtc).Take(10000).Select(request => new
                        {
                            request.Id, request.Source, request.StartedAtUtc, request.CompletedAtUtc,
                            request.DurationMilliseconds, request.Method, request.Url, request.StatusCode,
                            request.RequestBodySizeBytes, request.ResponseBodySizeBytes, request.ErrorMessage
                        }),
                        isTruncated = session.NetworkRequests.Count > 10000
                    });
                case "network.details":
                    var network = session.NetworkRequests.FirstOrDefault(request => request.Id == ReadText(arguments, "id"));
                    return network is null
                        ? ReviewError(HttpStatusCode.NotFound, "The recorded request was not found.")
                        : ReviewJson(new
                        {
                            network.Id, network.Method, network.Url, network.StatusCode, network.ReasonPhrase,
                            network.DurationMilliseconds, network.Protocol, network.Source,
                            network.RequestHeaders, network.ResponseHeaders, network.ErrorMessage,
                            requestBody = ReviewBody(network.RequestBody), responseBody = ReviewBody(network.ResponseBody)
                        });
                case "artifacts.list":
                    return ReviewJson(session.ArtifactSnapshots.OrderBy(artifact => artifact.CapturedAtUtc).Select(artifact => new
                    {
                        id = artifact.SnapshotId, artifact.Name, artifact.Kind, artifact.CapturedAtUtc, artifact.ByteCount,
                        entries = artifact.Entries.Where(entry => entry.Kind != "directory").Select(entry => new
                        {
                            entry.Name, rootAlias = artifact.SnapshotId, relativePath = entry.SnapshotRelativePath,
                            entry.Kind, entry.SizeBytes, entry.FileExtension, entry.MimeType, entry.LastModifiedUtc
                        })
                    }));
                case "artifacts.download":
                    return await DownloadRecordingArtifactAsync(session, arguments, cancellationToken).ConfigureAwait(false);
                default:
                    return ReviewError(HttpStatusCode.BadRequest, "This recording view is not supported.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            return ReviewError(HttpStatusCode.BadRequest, "The recording review arguments are invalid.");
        }
    }

    private async Task<RemoteRecordingReviewResult> DownloadRecordingArtifactAsync(
        AppSessionSnapshot session, JsonElement arguments, CancellationToken cancellationToken)
    {
        var artifact = session.ArtifactSnapshots.FirstOrDefault(item => item.SnapshotId == ReadText(arguments, "root"));
        var entry = artifact?.Entries.FirstOrDefault(item => item.SnapshotRelativePath == ReadText(arguments, "path"));
        var canRead = artifact is not null
            && entry is not null
            && entry.Kind != "directory";

        if (!canRead || !SessionFileLocator.TryResolveArtifactEntryPath(runtime.ApplicationPaths, session, artifact!, entry!, out var path))
        {
            return ReviewError(HttpStatusCode.NotFound, "The retained artifact file was not found.");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var offset = arguments.TryGetProperty("offsetBytes", out var offsetValue) ? offsetValue.GetInt64() : 0;
            var maximum = arguments.TryGetProperty("maxBytes", out var maximumValue) ? maximumValue.GetInt32() : 262144;
            maximum = Math.Clamp(maximum, 1, 1048576);
            var version = $"{stream.Length}:{File.GetLastWriteTimeUtc(path).Ticks}";
            var expectedVersion = ReadText(arguments, "expectedVersion");

            if (offset < 0 || offset > stream.Length)
            {
                return ReviewError(HttpStatusCode.BadRequest, "The file offset is invalid.");
            }

            if (expectedVersion is not null && expectedVersion != version)
            {
                return ReviewError(HttpStatusCode.Conflict, "The artifact changed during download. Try again.");
            }

            stream.Position = offset;
            var bytes = new byte[(int)Math.Min(maximum, stream.Length - offset)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return ReviewJson(new
            {
                fileName = entry!.Name, entry.MimeType, sizeBytes = stream.Length,
                base64 = Convert.ToBase64String(bytes), bytesRead = bytes.Length,
                hasMore = stream.Position < stream.Length, nextOffsetBytes = stream.Position, version
            });
        }
        catch (IOException)
        {
            return ReviewError(HttpStatusCode.NotFound, "The retained file is no longer available.");
        }
    }

    private static object? ReviewBody(SessionNetworkBody? body)
    {
        if (body is null)
        {
            return null;
        }

        const int maximumCharacters = 32768;
        return new
        {
            body.ContentType, body.Encoding,
            data = body.Data[..Math.Min(body.Data.Length, maximumCharacters)],
            body.CapturedBytes, body.TotalBytes,
            truncated = body.Truncated || body.Data.Length > maximumCharacters
        };
    }

    private static string? ReadText(JsonElement arguments, string name)
    {
        return arguments.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static RemoteRecordingReviewResult ReviewJson<T>(T value)
    {
        return new RemoteRecordingReviewResult(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(value, reviewJsonOptions));
    }

    private static RemoteRecordingReviewResult ReviewError(HttpStatusCode statusCode, string message)
    {
        return new RemoteRecordingReviewResult(statusCode, JsonSerializer.SerializeToUtf8Bytes(new { message }, reviewJsonOptions));
    }
}
