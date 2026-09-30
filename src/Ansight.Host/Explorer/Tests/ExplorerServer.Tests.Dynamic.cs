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
    private async Task<bool> TryHandleTestsDynamicGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "tests" && segments[2] == "executions")
        {
            var executionId = Uri.UnescapeDataString(segments[3]);
            var execution = testExecutions.Get(executionId);
            await WriteJsonAsync(response, (object?)execution ?? new OperationResult(false, $"Test execution '{executionId}' was not found."), execution is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleTestsDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 5 && segments[0] == "api" && segments[1] == "tests" && segments[2] == "executions" && segments[4] == "cancel")
        {
            var executionId = Uri.UnescapeDataString(segments[3]);
            var execution = testExecutions.Cancel(executionId);
            await WriteJsonAsync(response, (object?)execution ?? OperationResult.Failure($"Test execution '{executionId}' was not found."), execution is null ? HttpStatusCode.NotFound : HttpStatusCode.Accepted, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
