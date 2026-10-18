namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task WriteSessionActivityAsync(string sessionId, HttpListenerRequest request,
        HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (!isExplorer && !string.Equals(sessionId, InitialSessionId, StringComparison.Ordinal))
        {
            await WriteJsonAsync(response, OperationResult.Failure("Session not found."), HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }
        var limit = request.QueryString["limit"] is { } text && int.TryParse(text, out var parsed) ? parsed : 200;
        var page = runtime.Activity.Read(sessionId, request.QueryString["cursor"], limit);
        if (request.QueryString["format"] == "jsonl")
        {
            if (page.NextCursor is not null) response.Headers["X-Ansight-Next-Cursor"] = page.NextCursor;
            response.Headers["X-Ansight-Activity-Incomplete"] = (page.RetentionLimited || page.CursorExpired || page.SkippedRecords > 0
                || page.IncompleteTail || page.PersistenceError is not null || page.ImportedHistoryIncomplete) ? "true" : "false";
            response.ContentType = "application/x-ndjson; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            var bytes = Encoding.UTF8.GetBytes(string.Concat(page.Records.Select(record => JsonSerializer.Serialize(record, SessionActivityStore.JsonOptions) + "\n")));
            response.ContentLength64 = bytes.Length;
            if (!isHead) await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            response.Close();
            return;
        }
        await WriteJsonAsync(response, page, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
    }
}
