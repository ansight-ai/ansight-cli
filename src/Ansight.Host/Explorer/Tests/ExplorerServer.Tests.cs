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

internal sealed partial class ExplorerServer : IAsyncDisposable
{
    private async Task<bool> TryHandleTestsGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/test-history" when isExplorer:
                await WriteJsonAsync(response, runtime.WorkspaceTests.History.List(request.QueryString["appId"], request.QueryString["workspace"], ReadBoundedPositiveQueryInteger(request, "limit", 100, 10_000), request.QueryString["sessionId"], request.QueryString["batchRunId"]), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/tests/executions" when isExplorer:
                await WriteJsonAsync(response, testExecutions.List(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
        }

        return false;
    }

    private async Task<bool> TryHandleTestsPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/tests/validate" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalWorkspaceTestExecutionRequest>(request, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, runtime.WorkspaceTests.List(body.WorkspacePath, cancellationToken), HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/tests/run" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalWorkspaceTestExecutionRequest>(request, cancellationToken).ConfigureAwait(false);
                    var execution = testExecutions.Start(body);
                    await WriteJsonAsync(response, execution, HttpStatusCode.Accepted, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }
}
