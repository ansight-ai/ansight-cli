using System.Threading.Channels;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task StreamTimelineTrimAsync(
        HttpListenerResponse response,
        string sessionId,
        SessionTimelineTrimRequest request,
        SessionTimelineTrimMode mode,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/x-ndjson";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-store";
        var events = Channel.CreateBounded<object>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        void Report(SessionTimelineTrimProgress progress)
            => events.Writer.TryWrite(new { status = "running", progress });

        Report(new("Preparing session trim…"));
        // Keep the response writer free to flush progress while filtering and disk writes run.
        // Once trimming starts, finish persistence even if the viewer disconnects.
        var work = Task.Run(() =>
        {
            try
            {
                var result = runtime.SessionEditing.TrimTimeline(sessionId, request.StartUtc, request.EndUtc, mode, Report);
                if (result.IsSuccess)
                    events.Writer.TryWrite(new { status = "success", result });
                else
                    events.Writer.TryWrite(new { status = "error", message = result.Message });
            }
            catch (Exception exception)
            {
                events.Writer.TryWrite(new { status = "error", message = exception.Message });
            }
            finally { events.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, jsonOptions) + "\n");
                await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await work.ConfigureAwait(false);
            response.Close();
        }
    }
}
