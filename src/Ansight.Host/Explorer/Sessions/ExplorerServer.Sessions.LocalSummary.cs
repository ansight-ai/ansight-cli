using System.Threading.Channels;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.SimulatorAgent;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private sealed record AnnotationSummaryRequest(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Reasoning = null);

    private static bool AcceptsSummaryStream(HttpListenerRequest request)
        => request.AcceptTypes?.Any(type =>
            System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.TryParse(type, out var mediaType)
            && string.Equals(mediaType.MediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase)
            && mediaType.Quality is not 0) == true;

    private async Task WriteAnnotationSummaryAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!isExplorer && !string.Equals(sessionId, InitialSessionId, StringComparison.Ordinal))
        {
            await WriteJsonAsync(response, new { isSuccess = false, message = "Session not found." }, HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = await ReadJsonAsync<AnnotationSummaryRequest>(request, cancellationToken).ConfigureAwait(false);
        var reasoning = AgentReasoningModes.Normalize(body.Reasoning);
        async Task<object> Generate(Action<SessionSummaryProgress> reportProgress)
        {
            var snapshot = await LoadReplaySnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Session not found.");
            reportProgress(new("loading", "Selecting evidence within the annotation's time range…"));
            var section = LocalSessionSummaryRunner.SelectSection(snapshot, body.StartUtc, body.EndUtc);
            var analysis = await LocalSessionSummaryRunner.RunAsync(
                runtime, section, teamId: null, cancellationToken, reasoningMode: reasoning,
                reportProgress: reportProgress, isSection: true).ConfigureAwait(false);
            return new { isSuccess = true, comment = analysis.FinalResponse };
        }

        if (AcceptsSummaryStream(request))
        {
            await StreamSummaryOperationAsync(response, Generate, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await WriteJsonAsync(response, await Generate(_ => { }).ConfigureAwait(false), HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or HttpRequestException or IOException or JsonException)
        {
            await WriteJsonAsync(response, new { isSuccess = false, message = exception.Message }, exception is ArgumentException ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task StreamLocalSummaryAsync(
        HttpListenerResponse response,
        string sessionId,
        Guid? teamId,
        CancellationToken cancellationToken)
        => StreamSummaryOperationAsync(response, async reportProgress =>
        {
            var snapshot = await LoadReplaySnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Session not found.");
            var analysis = await LocalSessionSummaryRunner.RunAsync(
                runtime, snapshot, teamId, cancellationToken, reportProgress: reportProgress).ConfigureAwait(false);
            reportProgress(new("formatting", "Saving summary…"));
            runtime.SessionEditing.AddAnalysis(sessionId, analysis);
            return new { isSuccess = true, message = "Local session summary saved.", analysisId = analysis.AnalysisId };
        }, cancellationToken);

    private sealed record SummaryStreamEvent(string Status, SessionSummaryProgress? Progress = null, object? Result = null, string? Message = null);

    internal static async Task StreamSummaryOperationAsync(
        HttpListenerResponse response,
        Func<Action<SessionSummaryProgress>, Task<object>> generate,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/x-ndjson";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-store";
        async Task WriteEvent(SummaryStreamEvent item)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, jsonOptions) + "\n");
            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Send headers and the first stage before loading a potentially large capture.
        var progress = new SessionSummaryProgress("loading", "Loading session evidence…");
        await WriteEvent(new("running", Progress: progress)).ConfigureAwait(false);
        var events = Channel.CreateBounded<SummaryStreamEvent>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        void Report(SessionSummaryProgress update) => events.Writer.TryWrite(new("running", Progress: update));

        var work = Task.Run(async () =>
        {
            try
            {
                var result = await generate(Report).ConfigureAwait(false);
                events.Writer.TryWrite(new("success", Result: result));
            }
            catch (Exception exception)
            {
                events.Writer.TryWrite(new("error", Message: exception.Message));
            }
            finally { events.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var heartbeat = heartbeatTimer.WaitForNextTickAsync(cancellationToken).AsTask();
            var available = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
            while (true)
            {
                if (await Task.WhenAny(available, heartbeat).ConfigureAwait(false) == heartbeat)
                {
                    await heartbeat.ConfigureAwait(false);
                    await WriteEvent(new("running", Progress: progress)).ConfigureAwait(false);
                    heartbeat = heartbeatTimer.WaitForNextTickAsync(cancellationToken).AsTask();
                    continue;
                }

                if (!await available.ConfigureAwait(false)) break;
                while (events.Reader.TryRead(out var item))
                {
                    if (item.Progress is not null) progress = item.Progress;
                    await WriteEvent(item).ConfigureAwait(false);
                }
                available = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
            }
        }
        finally
        {
            await work.ConfigureAwait(false);
            response.Close();
        }
    }
}
