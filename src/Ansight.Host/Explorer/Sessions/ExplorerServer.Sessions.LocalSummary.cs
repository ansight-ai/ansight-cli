using System.Threading.Channels;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task StreamLocalSummaryAsync(
        HttpListenerResponse response,
        string sessionId,
        AppSessionSnapshot snapshot,
        Guid? teamId,
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
        void Report(string message) => events.Writer.TryWrite(new { status = "running", progress = new { message } });

        var work = Task.Run(async () =>
        {
            try
            {
                var analysis = await LocalSessionSummaryRunner.RunAsync(
                    runtime, snapshot, teamId, cancellationToken, reportProgress: Report).ConfigureAwait(false);
                Report("Saving summary…");
                runtime.SessionEditing.AddAnalysis(sessionId, analysis);
                events.Writer.TryWrite(new
                {
                    status = "success",
                    result = new { isSuccess = true, message = "Local session summary saved.", analysisId = analysis.AnalysisId }
                });
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
