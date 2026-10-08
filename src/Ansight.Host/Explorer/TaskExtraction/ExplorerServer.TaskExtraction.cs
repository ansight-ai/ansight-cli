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
using Ansight.Host.Workspaces.Catalog;
using Ansight.Host.Replay;
using Ansight.Host.Explorer.Tests;

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

    private sealed record WorkspaceTestPreviewRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Title,
        IReadOnlyList<string>? Assertions,
        string? ValidationPrompt,
        IReadOnlyList<string>? TaskSectionIds,
        string Reasoning = AgentReasoningModes.Fast,
        string? GenerationNotes = null,
        bool HasExplicitTitle = false);

    private sealed record WorkspaceTestSaveRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Title,
        string Source,
        bool Force = false);

    private sealed record WorkspaceTestValidateRequest(string SessionId, string Source);

    private sealed record WorkspaceTestDraftRunRequest(
        string SessionId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Source,
        IReadOnlyList<string>? TaskSectionIds,
        IReadOnlyList<string>? TaskExtractionIds,
        string? Platform,
        string? DeviceIdentifier,
        string? DeviceKind,
        string? ApplicationPath,
        string Reasoning = AgentReasoningModes.Fast,
        bool CaptureTrace = true);

    private sealed record TaskDraftRestoreRequest(IReadOnlyList<LocalTaskExtractionSnapshot> Extractions);
    private sealed record WorkspaceTestDraftDiscardRequest(string SessionId, string DraftId);

    private async Task<bool> TryHandleTaskExtractionGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/task-extractions" when isExplorer:
                await WriteJsonAsync(response, taskExtractions.List(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/task-extractions/test-drafts" when isExplorer:
                {
                    var sessionId = request.QueryString["sessionId"];
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        await WriteJsonAsync(response, OperationResult.Failure("Session ID is required."), HttpStatusCode.BadRequest, isHead, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                    var drafts = await workspaceTestDrafts.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, drafts, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/capabilities" when isExplorer:
                await WriteJsonAsync(response, taskExtractions.GetCapabilities(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/task-extractions/test-selectors" when isExplorer:
                {
                    var sessionId = request.QueryString["sessionId"]?.Trim();
                    if (string.IsNullOrWhiteSpace(sessionId)
                        || !DateTimeOffset.TryParse(request.QueryString["startUtc"], out var startUtc)
                        || !DateTimeOffset.TryParse(request.QueryString["endUtc"], out var endUtc)
                        || endUtc < startUtc)
                    {
                        await WriteJsonAsync(response, new OperationResult(false, "A session and valid startUtc/endUtc range are required."), HttpStatusCode.BadRequest, isHead, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (snapshot is null)
                    {
                        await WriteJsonAsync(response, new OperationResult(false, "The source session was not found."), HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                    var ids = LocalTaskSelectorEvidence.Create(snapshot.VisualTreeSnapshots
                        .Where(tree => tree.CapturedAtUtc >= startUtc && tree.CapturedAtUtc <= endUtc)).AutomationIds;
                    await WriteJsonAsync(response, ids, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                    return true;
                }
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
            case "api/task-extractions/restore-drafts" when isExplorer:
                {
                    var body = await ReadJsonAsync<TaskDraftRestoreRequest>(request, cancellationToken).ConfigureAwait(false);
                    var restored = await taskExtractions.RestoreDraftsAsync(body.Extractions, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, restored, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/test-drafts/save" when isExplorer:
                {
                    var body = await ReadJsonAsync<WorkspaceTestDraftSaveRequest>(request, cancellationToken).ConfigureAwait(false);
                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    var draft = await workspaceTestDrafts.SaveAsync(snapshot.AppId, body, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, draft, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/test-drafts/discard" when isExplorer:
                {
                    var body = await ReadJsonAsync<WorkspaceTestDraftDiscardRequest>(request, cancellationToken).ConfigureAwait(false);
                    var discarded = await workspaceTestDrafts.DiscardAsync(body.SessionId, body.DraftId, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response,
                        discarded ? OperationResult.Success("YAML test draft discarded.") : OperationResult.Failure("YAML test draft was not found."),
                        discarded ? HttpStatusCode.OK : HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
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
            case "api/task-extractions/test-preview" when isExplorer:
                {
                    var body = await ReadJsonAsync<WorkspaceTestPreviewRequest>(request, cancellationToken).ConfigureAwait(false);
                    if (request.AcceptTypes?.Any(type => string.Equals(type.Trim(), "application/x-ndjson", StringComparison.OrdinalIgnoreCase)) == true)
                    {
                        await StreamWorkspaceTestPreviewAsync(response, body, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                    var seed = await BuildWorkspaceTestPreviewAsync(body, cancellationToken).ConfigureAwait(false);
                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    var extraction = await WorkspaceTestRefiner.RefineAsync(
                        runtime, snapshot, body.StartUtc, body.EndUtc, body.Title, seed,
                        body.TaskSectionIds, body.Reasoning, modelOverride: null,
                        workspaceOverride: null, cancellationToken, generationNotes: body.GenerationNotes,
                        hasExplicitTitle: body.HasExplicitTitle).ConfigureAwait(false);
                    await WriteJsonAsync(response, extraction, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/test-validate" when isExplorer:
                {
                    var body = await ReadJsonAsync<WorkspaceTestValidateRequest>(request, cancellationToken).ConfigureAwait(false);
                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    var definition = ValidateWorkspaceTestSource(body.Source, snapshot.AppId);
                    var validationWorkspacePath = runtime.Apps.Get(snapshot.AppId)?.CodebasePath;
                    if (!string.IsNullOrWhiteSpace(validationWorkspacePath))
                    {
                        ValidateWorkspaceTestIdAvailable(definition.TestId, validationWorkspacePath);
                    }
                    await WriteJsonAsync(response, new ExternalDraftResult("passed", "Ansight workspace test YAML is valid.", ""), HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            case "api/task-extractions/test-save" when isExplorer:
                {
                    var body = await ReadJsonAsync<WorkspaceTestSaveRequest>(request, cancellationToken).ConfigureAwait(false);
                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    var definition = ValidateWorkspaceTestSource(body.Source, snapshot.AppId);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(definition.TestId, "^[a-z0-9]+(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    {
                        throw new InvalidDataException("The test ID must use lowercase letters, numbers, and hyphens before it can be saved.");
                    }
                    var workspacePath = runtime.Apps.Get(snapshot.AppId)?.CodebasePath;
                    if (string.IsNullOrWhiteSpace(workspacePath))
                    {
                        throw new InvalidDataException($"App '{snapshot.AppId}' must be linked to a workspace before saving an Ansight test.");
                    }
                    if (!body.Force) ValidateWorkspaceTestIdAvailable(definition.TestId, workspacePath);

                    var directoryPath = Path.Combine(workspacePath, "ansight", "tests");
                    Directory.CreateDirectory(directoryPath);
                    var destinationPath = Path.Combine(directoryPath, definition.TestId + ".yaml");
                    if (File.Exists(destinationPath) && !body.Force)
                    {
                        throw new InvalidDataException($"Ansight test already exists at '{destinationPath}'. Choose another title or explicitly replace it.");
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
            case "api/task-extractions/test-draft-run" when isExplorer:
                {
                    var body = await ReadJsonAsync<WorkspaceTestDraftRunRequest>(request, cancellationToken).ConfigureAwait(false);
                    var snapshot = await runtime.Sessions.LoadSnapshotAsync(body.SessionId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Session '{body.SessionId}' was not found.");
                    var definition = ValidateWorkspaceTestSource(body.Source, snapshot.AppId);
                    var workspacePath = runtime.Apps.Get(snapshot.AppId)?.CodebasePath;
                    if (string.IsNullOrWhiteSpace(workspacePath))
                    {
                        throw new InvalidDataException($"App '{snapshot.AppId}' must be linked to a workspace before running a test.");
                    }
                    var draftIds = body.TaskExtractionIds?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
                    var selectedSections = snapshot.Annotations
                        .Where(annotation => body.TaskSectionIds?.Contains(annotation.AnnotationId) == true
                            && annotation.EndUtc is not null
                            && annotation.StartUtc >= body.StartUtc
                            && annotation.EndUtc <= body.EndUtc)
                        .ToArray();
                    var drafts = draftIds.Select(id => taskExtractions.Get(id)
                        ?? throw new InvalidDataException($"Task draft '{id}' is no longer available.")).ToArray();
                    foreach (var draft in drafts)
                    {
                        if (!string.Equals(draft.AppId, snapshot.AppId, StringComparison.Ordinal)
                            || !string.Equals(draft.SessionId, body.SessionId, StringComparison.Ordinal)
                            || draft.Status != "ready" || draft.Draft is null
                            || !selectedSections.Any(section => section.StartUtc == draft.StartUtc
                                && section.EndUtc == draft.EndUtc))
                        {
                            throw new InvalidDataException($"Task draft '{draft.ExtractionId}' is not a ready draft from this test's selected sections.");
                        }
                    }
                    var taskRoot = drafts.Length == 0 ? null : CreateDraftTaskWorkspace(workspacePath, drafts);
                    LocalTestExecutionSnapshot execution;
                    try
                    {
                        var run = new LocalWorkspaceTestExecutionRequest(
                            workspacePath, [definition.TestId],
                            Platform: body.Platform,
                            DeviceIdentifier: body.DeviceIdentifier,
                            DeviceKind: body.DeviceKind,
                            ApplicationPath: body.ApplicationPath,
                            CaptureTrace: body.CaptureTrace)
                        {
                            Reasoning = body.Reasoning,
                            DraftSource = body.Source,
                            DraftTaskRootPath = taskRoot,
                            PreferredTaskIds = drafts.Select(draft => draft.Draft!.TaskId).ToArray()
                        };
                        execution = testExecutions.Start(run);
                    }
                    catch
                    {
                        if (taskRoot is not null && Directory.Exists(taskRoot)) Directory.Delete(taskRoot, recursive: true);
                        throw;
                    }
                    await WriteJsonAsync(response, execution, HttpStatusCode.Accepted, false, cancellationToken).ConfigureAwait(false);
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

    private async Task<WorkspaceTestExtraction> BuildWorkspaceTestPreviewAsync(
        WorkspaceTestPreviewRequest request,
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
        return WorkspaceTestExtractor.Extract(
            snapshot, request.StartUtc, request.EndUtc, request.Title, request.Assertions, request.ValidationPrompt, request.TaskSectionIds);
    }

    private async Task StreamWorkspaceTestPreviewAsync(
        HttpListenerResponse response,
        WorkspaceTestPreviewRequest request,
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
        var currentStage = "Reading the selected replay";
        void Report(string message)
        {
            currentStage = message.TrimEnd('…', '.');
            events.Writer.TryWrite(new { status = "running", progress = new { message } });
        }

        Report("Reading the selected replay…");
        var work = Task.Run(async () =>
        {
            try
            {
                var seed = await BuildWorkspaceTestPreviewAsync(request, cancellationToken).ConfigureAwait(false);
                Report($"Replay loaded: {seed.GeneratedActionCount} recorded action(s). Loading session evidence…");
                var snapshot = await runtime.Sessions.LoadSnapshotAsync(request.SessionId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Session '{request.SessionId}' was not found.");
                var extraction = await WorkspaceTestRefiner.RefineAsync(
                    runtime, snapshot, request.StartUtc, request.EndUtc, request.Title, seed,
                    request.TaskSectionIds, request.Reasoning, modelOverride: null,
                    workspaceOverride: null, cancellationToken, reportProgress: Report,
                    generationNotes: request.GenerationNotes,
                    hasExplicitTitle: request.HasExplicitTitle).ConfigureAwait(false);
                events.Writer.TryWrite(new { status = "success", result = extraction });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                events.Writer.TryWrite(new { status = "error", message = $"Generation stopped while {currentStage}: the Ansight host is shutting down." });
            }
            catch (OperationCanceledException)
            {
                events.Writer.TryWrite(new { status = "error", message = $"The AI operation was canceled while {currentStage}. The host is still running; no YAML draft was returned. Retry generation. If this repeats, check the model provider connection." });
            }
            catch (Exception exception)
            {
                events.Writer.TryWrite(new { status = "error", message = $"Generation failed while {currentStage}: {exception.GetBaseException().Message}" });
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

    private WorkspaceTestDefinition ValidateWorkspaceTestSource(string source, string appId)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256_000)
        {
            throw new InvalidDataException("Ansight test source must contain 1 to 256,000 characters.");
        }

        var definition = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/draft.yaml", source);
        if (!string.Equals(definition.AppId, appId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The test appId must match the source session app '{appId}'.");
        }
        var references = definition.GetReferencedTaskIds();
        if (references.Count > 0)
        {
            var workspacePath = runtime.Apps.Get(appId)?.CodebasePath;
            var tasks = string.IsNullOrWhiteSpace(workspacePath) ? [] : runtime.InspectRepositoryTasks(appId, workspacePath).Tasks;
            var unavailable = references.Where(id => !tasks.Any(task => task.Enabled && task.TaskId == id)).ToArray();
            if (unavailable.Length > 0)
                throw new InvalidDataException($"Unknown or disabled prompt task reference(s): {string.Join(", ", unavailable.Select(id => "@task/" + id))}.");
        }
        return definition;
    }

    private static void ValidateWorkspaceTestIdAvailable(string testId, string workspacePath)
    {
        var existing = WorkspaceTestCatalog.Load(workspacePath).Tests.FirstOrDefault(test =>
            string.Equals(test.TestId, testId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            throw new InvalidDataException($"Test ID '{testId}' is already used by '{existing.Name}' at '{existing.FilePath}'. Choose a unique ID.");
        }
    }

    private static string CreateDraftTaskWorkspace(string workspacePath, IReadOnlyList<LocalTaskExtractionSnapshot> drafts)
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-draft-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "ansight", "tasks");
        Directory.CreateDirectory(target);
        try
        {
            CopyTaskModules(Path.Combine(workspacePath, "ansight", "tasks"), target);
            foreach (var draft in drafts)
            {
                CopyTaskModules(Path.Combine(draft.Draft!.DraftRootPath, "ansight", "tasks"), target);
            }
            return root;
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static void CopyTaskModules(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory)) return;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(source);
            if (extension is not (".ts" or ".js" or ".mjs" or ".cjs" or ".json")) continue;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
            if (new FileInfo(source).Length > 1_048_576) continue;
            var destination = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }
}
