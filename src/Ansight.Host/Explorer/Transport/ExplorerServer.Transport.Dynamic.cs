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
    private async Task<bool> TryHandleTransportDynamicGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "health" && segments[2] == "logs" && segments[3] == "content")
        {
            await WriteHostLogAsync(response, request.QueryString["file"], isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "test-history" && segments[3] == "evidence")
        {
            var runId = Uri.UnescapeDataString(segments[2]);
            var relativePath = request.QueryString["path"]?.Trim();
            var evidencePath = string.IsNullOrWhiteSpace(relativePath) ? null : runtime.WorkspaceTests.History.ResolveTraceEvidencePath(runId, relativePath, request.QueryString["appId"]);
            if (evidencePath is null)
            {
                await WriteTextAsync(response, "Trace evidence was not found for this run.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await WriteTraceEvidenceFileAsync(response, evidencePath, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "test-history" && segments[3] == "export")
        {
            var runId = Uri.UnescapeDataString(segments[2]);
            var fileName = $"{FileNameUtil.Sanitize(runId)}-evaluation.zip";
            var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-{Guid.NewGuid():N}-{fileName}");
            try
            {
                var export = await runtime.WorkspaceTests.TraceExports.ExportAsync(runId, temporaryPath, request.QueryString["appId"], cancellationToken).ConfigureAwait(false);
                if (!export.IsSuccess)
                {
                    await WriteJsonAsync(response, export, HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                await WriteDownloadFileAsync(response, temporaryPath, fileName, "application/zip", isHead, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The operating system can clean up an interrupted local export later.
                }
            }
        }

        if (isExplorer && segments.Length == 3 && segments[0] == "api" && segments[1] == "test-history")
        {
            var runId = Uri.UnescapeDataString(segments[2]);
            var inspection = runtime.WorkspaceTests.History.Inspect(runId, request.QueryString["appId"]);
            if (inspection is null)
            {
                await WriteTextAsync(response, $"Test run '{runId}' was not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await WriteJsonAsync(response, inspection, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleTransportDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "test-history" && segments[3] == "reveal")
        {
            var runId = Uri.UnescapeDataString(segments[2]);
            var inspection = runtime.WorkspaceTests.History.Inspect(runId, request.QueryString["appId"]);
            var entry = inspection?.Runs.FirstOrDefault(candidate => string.Equals(candidate.Audit.RunId, runId, StringComparison.Ordinal));
            if (entry is null || !File.Exists(entry.FilePath))
            {
                await WriteTextAsync(response, "The saved trace was not found for this run.", HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await ArtifactDesktopActions.RevealAsync(entry.FilePath, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, new { message = "Trace selected in its folder." }, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "app-graph-recordings" && Guid.TryParse(Uri.UnescapeDataString(segments[2]), out var recordingId))
        {
            var operation = segments[3];
            LocalAppGraphRecordingOperationResult result;
            if (operation is "capture-destination" or "arm-transition" or "complete-transition")
            {
                var recording = runtime.LocalAppGraphs.GetRecording(recordingId);
                if (recording is null)
                {
                    result = LocalAppGraphRecordingOperationResult.Failure("Walkthrough recording was not found.");
                }
                else
                {
                    _ = await runtime.LiveVisualTrees.CaptureLiveVisualTreeAsync(recording.SessionId, cancellationToken).ConfigureAwait(false);
                    if (!runtime.Sessions.TryGetLiveContentSnapshot(recording.SessionId, out var snapshot) || snapshot is null)
                    {
                        result = LocalAppGraphRecordingOperationResult.Failure("The walkthrough's live session is no longer connected.");
                    }
                    else if (operation == "capture-destination")
                    {
                        var body = await ReadJsonAsync<LocalAppGraphRecordingDestinationRequest>(request, cancellationToken).ConfigureAwait(false);
                        result = runtime.LocalAppGraphs.CaptureRecordingDestination(recordingId, body, snapshot);
                    }
                    else if (operation == "arm-transition")
                    {
                        var body = await ReadJsonAsync<LocalAppGraphRecordingArmTransitionRequest>(request, cancellationToken).ConfigureAwait(false);
                        result = runtime.LocalAppGraphs.ArmRecordingTransition(recordingId, body, snapshot);
                    }
                    else
                    {
                        var body = await ReadJsonAsync<LocalAppGraphRecordingCompleteTransitionRequest>(request, cancellationToken).ConfigureAwait(false);
                        result = runtime.LocalAppGraphs.CompleteRecordingTransition(recordingId, body, snapshot);
                    }
                }
            }
            else if (operation == "navigation-host")
            {
                var body = await ReadJsonAsync<LocalAppGraphRecordingNavigationHostRequest>(request, cancellationToken).ConfigureAwait(false);
                result = runtime.LocalAppGraphs.UpsertRecordingNavigationHost(recordingId, body);
            }
            else if (operation == "tab-group")
            {
                var body = await ReadJsonAsync<LocalAppGraphRecordingTabGroupRequest>(request, cancellationToken).ConfigureAwait(false);
                result = runtime.LocalAppGraphs.UpsertRecordingTabGroup(recordingId, body);
            }
            else
            {
                result = operation switch
                {
                    "stop" => runtime.LocalAppGraphs.StopRecording(recordingId),
                    "resume" => runtime.LocalAppGraphs.ResumeRecording(recordingId),
                    "commit" => runtime.LocalAppGraphs.CommitRecording(recordingId),
                    "cancel" => runtime.LocalAppGraphs.CancelRecording(recordingId),
                    _ => LocalAppGraphRecordingOperationResult.Failure($"Unknown walkthrough operation '{operation}'.")
                };
            }

            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 5 && segments[0] == "api" && segments[1] == "enrollment" && segments[2] == "invites" && segments[4] == "revoke")
        {
            var result = runtime.Pairing.Revoke(Uri.UnescapeDataString(segments[3]));
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
