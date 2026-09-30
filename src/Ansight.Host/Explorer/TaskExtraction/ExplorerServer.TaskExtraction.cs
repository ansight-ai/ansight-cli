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
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer : IAsyncDisposable
{
    private sealed record MaestroPreviewRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Title);

    private sealed record MaestroSaveRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Title,
        string Source,
        bool Force = false);

    private sealed record MaestroRefineRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Title,
        string Source,
        string Reasoning = AgentReasoningModes.Fast,
        string Model = "");

    private sealed record MaestroSavedFlow(string FilePath);

    private sealed record AppiumPreviewRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Title);

    private sealed record AppiumSaveRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Title,
        string Source,
        bool Force = false);

    private async Task<bool> TryHandleTaskExtractionGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/task-extractions" when isExplorer:
                await WriteJsonAsync(response, taskExtractions.List(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/task-extractions/capabilities" when isExplorer:
                await WriteJsonAsync(response, taskExtractions.GetCapabilities(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/task-extractions/references" when isExplorer:
                {
                    var sessionId = request.QueryString["sessionId"];
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        await WriteJsonAsync(response, OperationResult.Failure("Session ID is required."), HttpStatusCode.BadRequest, isHead, cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    var catalog = await taskExtractions.GetAuthoringReferencesAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, catalog, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }

    private async Task<bool> TryHandleTaskExtractionPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/task-extractions/start" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalTaskExtractionStartRequest>(request, cancellationToken).ConfigureAwait(false);
                    var extraction = taskExtractions.Start(body);
                    await WriteJsonAsync(response, extraction, HttpStatusCode.Accepted, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/external-validate" when isExplorer:
            case "api/task-extractions/external-test" when isExplorer:
                {
                    var body = await ReadJsonAsync<ExternalDraftRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = await ReviewExternalDraftAsync(body, route.EndsWith("external-test", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, result, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/maestro-preview" when isExplorer:
                {
                    var body = await ReadJsonAsync<MaestroPreviewRequest>(request, cancellationToken).ConfigureAwait(false);
                    var extraction = await BuildMaestroPreviewAsync(body, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, extraction, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/appium-preview" when isExplorer:
                {
                    var body = await ReadJsonAsync<AppiumPreviewRequest>(request, cancellationToken).ConfigureAwait(false);
                    var extraction = await BuildAppiumPreviewAsync(body, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, extraction, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/appium-save" when isExplorer:
                {
                    var body = await ReadJsonAsync<AppiumSaveRequest>(request, cancellationToken).ConfigureAwait(false);
                    var extraction = await BuildAppiumPreviewAsync(
                        new AppiumPreviewRequest(body.SessionId, body.StartUtc, body.EndUtc, body.Title),
                        cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body.Source) || body.Source.Length > 256_000)
                    {
                        throw new InvalidDataException("Appium source must contain 1 to 256,000 characters.");
                    }

                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    var workspacePath = runtime.Apps.Get(snapshot.AppId)?.CodebasePath;
                    if (string.IsNullOrWhiteSpace(workspacePath))
                    {
                        throw new InvalidDataException($"App '{snapshot.AppId}' must be linked to a workspace before saving an Appium script.");
                    }

                    var directoryPath = Path.Combine(workspacePath, "appium");
                    Directory.CreateDirectory(directoryPath);
                    var destinationPath = Path.Combine(directoryPath, extraction.SuggestedName + ".test.mjs");
                    if (File.Exists(destinationPath) && !body.Force)
                    {
                        throw new InvalidDataException($"Appium script already exists at '{destinationPath}'. Choose another title or explicitly replace it.");
                    }

                    var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await File.WriteAllTextAsync(temporaryPath, body.Source, new UTF8Encoding(false), cancellationToken)
                            .ConfigureAwait(false);
                        File.Move(temporaryPath, destinationPath, overwrite: body.Force);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }

                    await WriteJsonAsync(response, new MaestroSavedFlow(destinationPath), HttpStatusCode.OK, false, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/maestro-save" when isExplorer:
                {
                    var body = await ReadJsonAsync<MaestroSaveRequest>(request, cancellationToken).ConfigureAwait(false);
                    var extraction = await BuildMaestroPreviewAsync(
                        new MaestroPreviewRequest(body.SessionId, body.StartUtc, body.EndUtc, body.Title),
                        cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body.Source) || body.Source.Length > 256_000)
                    {
                        throw new InvalidDataException("Maestro source must contain 1 to 256,000 characters.");
                    }

                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    MaestroFlowRefiner.ValidateSource(body.Source, snapshot.AppId);
                    var workspacePath = runtime.Apps.Get(snapshot.AppId)?.CodebasePath;
                    if (string.IsNullOrWhiteSpace(workspacePath))
                    {
                        throw new InvalidDataException($"App '{snapshot.AppId}' must be linked to a workspace before saving a Maestro flow.");
                    }

                    var directoryPath = Path.Combine(workspacePath, ".maestro");
                    Directory.CreateDirectory(directoryPath);
                    var destinationPath = Path.Combine(directoryPath, extraction.SuggestedName + ".yaml");
                    if (File.Exists(destinationPath) && !body.Force)
                    {
                        throw new InvalidDataException($"Maestro flow already exists at '{destinationPath}'. Choose another title or explicitly replace it.");
                    }

                    var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await File.WriteAllTextAsync(temporaryPath, body.Source, new UTF8Encoding(false), cancellationToken)
                            .ConfigureAwait(false);
                        File.Move(temporaryPath, destinationPath, overwrite: body.Force);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }

                    await WriteJsonAsync(response, new MaestroSavedFlow(destinationPath), HttpStatusCode.OK, false, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/maestro-refine" when isExplorer:
                {
                    var body = await ReadJsonAsync<MaestroRefineRequest>(request, cancellationToken).ConfigureAwait(false);
                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    var refinement = await MaestroFlowRefiner.RefineAsync(
                        runtime,
                        snapshot,
                        body.StartUtc,
                        body.EndUtc,
                        body.Title,
                        body.Source,
                        body.Reasoning,
                        body.Model,
                        workspaceOverride: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, refinement, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }

    private async Task<MaestroFlowExtraction> BuildMaestroPreviewAsync(
        MaestroPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        if (request.EndUtc <= request.StartUtc)
        {
            throw new InvalidDataException("Select a timeline period with a positive duration.");
        }

        var snapshot = await runtime.Sessions.LoadSnapshotAsync(request.SessionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Session '{request.SessionId}' was not found.");
        return MaestroFlowExtractor.Extract(snapshot, request.StartUtc, request.EndUtc, request.Title);
    }

    private async Task<AppiumScriptExtraction> BuildAppiumPreviewAsync(
        AppiumPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        if (request.EndUtc <= request.StartUtc)
        {
            throw new InvalidDataException("Select a timeline period with a positive duration.");
        }

        var snapshot = await runtime.Sessions.LoadSnapshotAsync(request.SessionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Session '{request.SessionId}' was not found.");
        return AppiumScriptExtractor.Extract(snapshot, request.StartUtc, request.EndUtc, request.Title);
    }
}
