using System.Net;
using Ansight.Host.Agents;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task<bool> TryHandleAnnotationAgentsAsync(HttpListenerRequest request, HttpListenerResponse response,
        string[] segments, bool isHead, CancellationToken cancellationToken)
    {
        if (segments.Length != 5 || segments[0] != "api" || segments[1] != "sessions" || segments[3] != "annotation-agents")
        {
            return false;
        }

        var sessionId = Uri.UnescapeDataString(segments[2]);
        if (!isExplorer && sessionId != InitialSessionId)
        {
            throw new InvalidDataException("Session not found.");
        }

        var source = runtime.LocalAnnotationAgents ?? throw new InvalidDataException("Annotation delivery requires the Ansight CLI host.");
        object result;
        if (request.HttpMethod is "GET" or "HEAD" && segments[4] == "catalog")
        {
            result = await source.CatalogAsync(sessionId, request.QueryString["provider"] ?? "codex", request.QueryString["projectId"], cancellationToken);
        }
        else if (request.HttpMethod is "GET" or "HEAD" && segments[4] == "status")
        {
            result = await source.StatusAsync(sessionId, request.QueryString["submissionId"] ?? "", cancellationToken);
        }
        else if (request.HttpMethod == "POST" && segments[4] is "send" or "queue")
        {
            // Browser origins may not use this endpoint to launch agents on the host.
            if (request.Headers["Origin"] is { } origin && origin != request.Url?.GetLeftPart(UriPartial.Authority))
            {
                throw new InvalidDataException("Annotation delivery must originate from this local UI.");
            }

            var delivery = await ReadJsonAsync<LocalAnnotationSendRequest>(request, cancellationToken);
            result = await source.SendAsync(sessionId, delivery with { QueueAfterCurrent = segments[4] == "queue" || delivery.QueueAfterCurrent }, cancellationToken);
        }
        else
        {
            return false;
        }

        await WriteJsonAsync(response, result, HttpStatusCode.OK, isHead, cancellationToken);
        return true;
    }
}
