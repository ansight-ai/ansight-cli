using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ansight.Host.AppGraphs;
using Ansight.Host.Files;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task<bool> TryHandleTaskExtractionDynamicGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 3 && segments[0] == "api" && segments[1] == "task-extractions")
        {
            var extractionId = Uri.UnescapeDataString(segments[2]);
            var extraction = taskExtractions.Get(extractionId);
            await WriteJsonAsync(response, (object?)extraction ?? OperationResult.Failure($"Task extraction '{extractionId}' was not found."), extraction is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleTaskExtractionDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "task-extractions" && segments[3] == "discard")
        {
            var extractionId = Uri.UnescapeDataString(segments[2]);
            var discarded = taskExtractions.Discard(extractionId);
            await WriteJsonAsync(response,
                discarded ? OperationResult.Success("Task draft discarded.") : OperationResult.Failure($"Task extraction '{extractionId}' was not found."),
                discarded ? HttpStatusCode.OK : HttpStatusCode.NotFound,
                false,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "task-extractions" && segments[3] == "debug")
        {
            var extractionId = Uri.UnescapeDataString(segments[2]);
            var result = await taskExtractions.DebugTestFailureAsync(extractionId, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, (object?)result ?? OperationResult.Failure($"Task extraction '{extractionId}' was not found."), result is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "task-extractions" && segments[3] is "draft" or "test" or "commit" or "cancel" or "cancel-test" or "clear-test")
        {
            var extractionId = Uri.UnescapeDataString(segments[2]);
            LocalTaskExtractionSnapshot? extraction;
            if (segments[3] == "draft")
            {
                var body = await ReadJsonAsync<LocalTaskExtractionDraftUpdateRequest>(request, cancellationToken).ConfigureAwait(false);
                extraction = taskExtractions.UpdateDraft(extractionId, body);
            }
            else if (segments[3] == "test")
            {
                var body = await ReadJsonAsync<LocalTaskExtractionTestRequest>(request, cancellationToken).ConfigureAwait(false);
                extraction = taskExtractions.Test(extractionId, body);
            }
            else if (segments[3] == "commit")
            {
                extraction = taskExtractions.Commit(extractionId);
            }
            else if (segments[3] == "cancel-test")
            {
                extraction = await taskExtractions.CancelTestAsync(extractionId).ConfigureAwait(false);
            }
            else if (segments[3] == "clear-test")
            {
                extraction = taskExtractions.ClearTestResult(extractionId);
            }
            else
            {
                extraction = taskExtractions.Cancel(extractionId);
            }

            await WriteJsonAsync(response, (object?)extraction ?? OperationResult.Failure($"Task extraction '{extractionId}' was not found."), extraction is null ? HttpStatusCode.NotFound : segments[3] is "test" or "cancel" or "cancel-test" ? HttpStatusCode.Accepted : HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
