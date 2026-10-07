using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Runtime.Sanitization;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.SimulatorAgent;
using Ansight.Host.Workspaces.Execution;
using Ansight.Tools;

namespace Ansight.Host.Explorer.TaskExtraction;

internal sealed class LocalTaskExtractionCoordinator : IDisposable
{
    private const int MaximumRetainedExtractions = 50;
    private const int MaximumAgentTurns = 12;
    private const int MaximumDraftRevisionAttempts = 3;
    private const int MaximumProgressEntries = 100;
    private const int MaximumSourceCharacters = 500_000;
    private const int MaximumTraceContextCharacters = 1_000_000;
    private const int MaximumTracePayloadCharacters = 120_000;
    private const int MaximumOcrEvidenceFrames = 12;
    private const string InspectSelectionToolName = "inspect_selected_period";
    private const string InspectTaskSeedToolName = "inspect_task_seed";
    private const string InspectVisualTreesToolName = "inspect_visual_trees";
    private const string InspectScreenshotOcrToolName = "inspect_screenshot_ocr";
    private const string InspectTaskSupportModulesToolName = "inspect_task_support_modules";
    private const string SubmitDraftToolName = "submit_task_draft";
    private static readonly IReadOnlySet<string> expectStaticMethods = new HashSet<string>(
        ["soft"],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = JsonUtil.MaximumDepth
    };

    private readonly Lock gate = new();
    private readonly RuntimeCoordinator runtime;
    private readonly ISessionScreenshotOcrScanner ocrScanner;
    private readonly Dictionary<string, ActiveTaskExtraction> extractions = new(StringComparer.Ordinal);
    private bool disposed;

    public LocalTaskExtractionCoordinator(RuntimeCoordinator runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ocrScanner = new TesseractSessionScreenshotOcrScanner();
    }

    public LocalTaskExtractionCapabilities GetCapabilities()
        => new(
            "ansight.local-task-extraction-capabilities/v1",
            SupportsHosted: runtime.Extensions.IsAvailable(),
            IsHostedSignedIn: false,
            SupportsDirectWebSocket: true,
            TaskTypeDefinitions: RepositoryModuleContractArtifacts.GetTaskTypeDefinitions())
        {
            CanUseModel = runtime.Extensions.IsAvailable()
        };

    public async Task<LocalTaskAuthoringReferenceCatalog> GetAuthoringReferencesAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        var isLive = runtime.IsSessionLive(normalizedSessionId);
        AppSessionSnapshot? session;
        if (!runtime.Sessions.TryGetSnapshot(normalizedSessionId, out session) || session is null)
        {
            session = await runtime.Sessions.LoadSnapshotAsync(normalizedSessionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (session is null)
        {
            return LocalTaskAuthoringReferenceCatalog.NotFound(normalizedSessionId);
        }

        var app = runtime.Apps.Get(session.AppId);
        var supportModules = app is null || string.IsNullOrWhiteSpace(app.CodebasePath)
            ? Array.Empty<LocalTaskSupportModule>()
            : RepositoryTaskLoader.DiscoverSupportModules(app.CodebasePath)
                .Select(static module => new LocalTaskSupportModule(
                    module.RelativePath,
                    module.Source,
                    module.IsDeclaration))
                .ToArray();

        var catalog = isLive && session.CaptureSource != WorkspaceExecutionModes.Device
            ? await CaptureCurrentAppToolCatalogAsync(normalizedSessionId, cancellationToken).ConfigureAwait(false)
              ?? session.AppToolCatalog
            : session.AppToolCatalog;
        if (catalog is null)
        {
            return new LocalTaskAuthoringReferenceCatalog(
                "ansight.local-task-authoring-references/v1",
                normalizedSessionId,
                isLive,
                null,
                [],
                supportModules,
                session.CaptureSource == WorkspaceExecutionModes.Device
                    ? "External capture supports host tools and portable tasks. " + ExecutionCapabilities.SdkSetupMessage
                    : isLive
                    ? "The connected app has not published a usable tool catalog."
                    : "This recording predates captured app-tool catalogs.");
        }

        return new LocalTaskAuthoringReferenceCatalog(
            "ansight.local-task-authoring-references/v1",
            normalizedSessionId,
            isLive,
            catalog.CapturedAtUtc,
            BuildAuthoringReferences(catalog),
            supportModules,
            isLive
                ? "Showing tools currently published by the connected app."
                : "Showing tools captured when this session was live.");
    }

    private async Task<SessionAppToolCatalogSnapshot?> CaptureCurrentAppToolCatalogAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var toolResponse = await runtime.AppTools.QueryForTaskAuthoringAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!toolResponse.Success
            || toolResponse.Envelope?.Payload is not JsonObject toolCatalog)
        {
            return null;
        }

        JsonObject? artifactCatalog = null;
        if (HasExecutableTool(toolCatalog, "artifacts.query"))
        {
            var artifactResponse = await runtime.AppTools.CallForTaskAuthoringAsync(
                    sessionId,
                    "artifacts.query",
                    new JsonObject(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (artifactResponse.Success
                && artifactResponse.Envelope is not null
                && !string.Equals(artifactResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal)
                && artifactResponse.Envelope.Payload is JsonObject artifactPayload)
            {
                artifactCatalog = artifactPayload.DeepClone().AsObject();
            }
        }

        var snapshot = new SessionAppToolCatalogSnapshot(
            "ansight.session-app-tool-catalog/v1",
            DateTimeOffset.UtcNow,
            toolCatalog.DeepClone().AsObject(),
            artifactCatalog);
        runtime.SessionEditing.SetAppToolCatalog(sessionId, snapshot);
        return snapshot;
    }

    internal static IReadOnlyList<LocalTaskAuthoringReference> BuildAuthoringReferences(
        SessionAppToolCatalogSnapshot catalog)
    {
        var references = new List<LocalTaskAuthoringReference>();
        var seenMentions = new HashSet<string>(StringComparer.Ordinal);
        if (catalog.ToolCatalog["tools"] is JsonArray tools)
        {
            foreach (var tool in tools.OfType<JsonObject>())
            {
                var id = ReadOptionalString(tool, "id");
                if (id is null || !ReadOptionalBoolean(tool, "executable", fallback: true))
                {
                    continue;
                }

                var mention = $"@tool:{id}";
                if (!seenMentions.Add(mention))
                {
                    continue;
                }

                references.Add(new LocalTaskAuthoringReference(
                    "tool",
                    mention,
                    id,
                    ReadOptionalString(tool, "title")
                    ?? ReadOptionalString(tool, "name")
                    ?? id,
                    ReadOptionalString(tool, "description") ?? "App-published device tool.",
                    ReadOptionalString(tool, "policy"),
                    null,
                    null));
            }
        }

        AddArtifactReferences(catalog.ArtifactCatalog, references, seenMentions);
        return references
            .OrderBy(static reference => reference.Kind, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static reference => reference.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddArtifactReferences(
        JsonObject? catalog,
        ICollection<LocalTaskAuthoringReference> references,
        ISet<string> seenMentions)
    {
        var result = catalog?["result"] ?? catalog;
        if (result is not JsonObject resultObject)
        {
            return;
        }

        if (resultObject["providers"] is JsonArray providers)
        {
            foreach (var provider in providers.OfType<JsonObject>())
            {
                var providerId = ReadOptionalString(provider, "providerId")
                                 ?? ReadOptionalString(provider, "id");
                if (providerId is null)
                {
                    continue;
                }

                AddArtifactProviderReference(providerId, provider, references, seenMentions);
                AddArtifactDefinitionReferences(
                    provider["artifacts"] as JsonArray,
                    providerId,
                    references,
                    seenMentions);
            }
        }

        AddArtifactDefinitionReferences(
            resultObject["artifacts"] as JsonArray,
            inheritedProviderId: null,
            references,
            seenMentions);
    }

    private static void AddArtifactDefinitionReferences(
        JsonArray? artifacts,
        string? inheritedProviderId,
        ICollection<LocalTaskAuthoringReference> references,
        ISet<string> seenMentions)
    {
        if (artifacts is null)
        {
            return;
        }

        foreach (var artifact in artifacts.OfType<JsonObject>())
        {
            var providerId = ReadOptionalString(artifact, "providerId") ?? inheritedProviderId;
            var artifactId = ReadOptionalString(artifact, "artifactId")
                             ?? ReadOptionalString(artifact, "id");
            if (providerId is null || artifactId is null)
            {
                continue;
            }

            AddArtifactProviderReference(providerId, artifact, references, seenMentions);
            var mention = $"@artifact:{providerId}/{artifactId}";
            if (!seenMentions.Add(mention))
            {
                continue;
            }

            references.Add(new LocalTaskAuthoringReference(
                "artifact",
                mention,
                artifactId,
                ReadOptionalString(artifact, "name")
                ?? ReadOptionalString(artifact, "title")
                ?? artifactId,
                ReadOptionalString(artifact, "description") ?? "Artifact published by the connected app.",
                ReadOptionalString(artifact, "policy"),
                providerId,
                artifactId));
        }
    }

    private static void AddArtifactProviderReference(
        string providerId,
        JsonObject source,
        ICollection<LocalTaskAuthoringReference> references,
        ISet<string> seenMentions)
    {
        var mention = $"@artifact-provider:{providerId}";
        if (!seenMentions.Add(mention))
        {
            return;
        }

        references.Add(new LocalTaskAuthoringReference(
            "artifactProvider",
            mention,
            providerId,
            ReadOptionalString(source, "providerName")
            ?? ReadOptionalString(source, "name")
            ?? providerId,
            ReadOptionalString(source, "providerDescription")
            ?? ReadOptionalString(source, "description")
            ?? "App-published artifact provider.",
            null,
            providerId,
            null));
    }

    private static bool HasExecutableTool(JsonObject catalog, string toolId)
        => catalog["tools"] is JsonArray tools
           && tools.OfType<JsonObject>().Any(tool =>
               string.Equals(ReadOptionalString(tool, "id"), toolId, StringComparison.Ordinal)
               && ReadOptionalBoolean(tool, "executable", fallback: true));

    private static bool ReadOptionalBoolean(JsonObject source, string propertyName, bool fallback)
        => source[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : fallback;

    public IReadOnlyList<LocalTaskExtractionSnapshot> List()
    {
        lock (gate)
        {
            return extractions.Values
                .Select(static extraction => extraction.Snapshot())
                .OrderByDescending(static extraction => extraction.CreatedAtUtc)
                .ToArray();
        }
    }

    public LocalTaskExtractionSnapshot? Get(string extractionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        lock (gate)
        {
            return extractions.GetValueOrDefault(extractionId.Trim())?.Snapshot();
        }
    }

    public LocalTaskExtractionSnapshot Start(LocalTaskExtractionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateStartRequest(request);

        var session = runtime.Sessions.GetSummaries().FirstOrDefault(candidate => string.Equals(
            candidate.SessionId,
            request.SessionId.Trim(),
            StringComparison.Ordinal));
        if (session is null)
        {
            throw new InvalidDataException($"Session '{request.SessionId.Trim()}' was not found.");
        }

        var app = runtime.Apps.Get(session.AppId);
        if (app is null || string.IsNullOrWhiteSpace(app.CodebasePath))
        {
            throw new InvalidDataException(
                $"App '{session.AppId}' must be linked to a workspace before extracting a task.");
        }

        var mode = NormalizeMode(request.Mode);
        if (!string.IsNullOrWhiteSpace(request.ReplaceExtractionId))
        {
            var previous = Get(request.ReplaceExtractionId)
                ?? throw new InvalidDataException("The draft to regenerate was not found.");
            if (previous.SessionId != session.SessionId
                || previous.AppId != session.AppId
                || previous.StartUtc != request.StartUtc.ToUniversalTime()
                || previous.EndUtc != request.EndUtc.ToUniversalTime()
                || previous.Status is "queued" or "running"
                || previous.TestStatus == "running"
                || previous.CommittedPath is not null)
            {
                throw new InvalidDataException("Only an uncommitted draft from this selected period can be regenerated.");
            }
        }
        var extraction = new ActiveTaskExtraction(
            Guid.CreateVersion7().ToString("N"),
            session.SessionId,
            session.AppId,
            Path.GetFullPath(app.CodebasePath),
            request.StartUtc.ToUniversalTime(),
            request.EndUtc.ToUniversalTime(),
            ResolveTaskName(request),
            request.Description.Trim(),
            mode,
            NormalizeModel(request.Model),
            AgentReasoningModes.Normalize(request.Reasoning),
            request.TeamId,
            !string.IsNullOrWhiteSpace(request.TaskName),
            request.ValidateSelectors,
            runtime.FeatureLifetime);
        lock (gate)
        {
            extractions[extraction.ExtractionId] = extraction;
            PruneCompletedExtractions();
        }

        extraction.WorkTask = RunExtractionAsync(extraction, request.ReplaceExtractionId);
        return extraction.Snapshot();
    }

    public async Task<IReadOnlyList<LocalTaskExtractionSnapshot>> RestoreDraftsAsync(
        IReadOnlyList<LocalTaskExtractionSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (snapshots.Count is < 1 or > MaximumRetainedExtractions
            || snapshots.Select(static item => item.ExtractionId).Distinct(StringComparer.Ordinal).Count() != snapshots.Count)
        {
            throw new InvalidDataException("Provide distinct task draft snapshots within the retention limit.");
        }

        var restored = new List<ActiveTaskExtraction>(snapshots.Count);
        try
        {
            foreach (var item in snapshots)
            {
                if (item.Schema != "ansight.local-task-extraction/v1"
                    || !Guid.TryParseExact(item.ExtractionId, "N", out _)
                    || item.Status is not ("ready" or "needsReview")
                    || item.Draft is null
                    || item.CommittedPath is not null
                    || item.EndUtc < item.StartUtc)
                {
                    throw new InvalidDataException("Only saved, uncommitted task draft snapshots can be restored.");
                }

                var app = runtime.Apps.Get(item.AppId);
                if (app is null || string.IsNullOrWhiteSpace(app.CodebasePath)
                    || !string.Equals(Path.GetFullPath(app.CodebasePath), Path.GetFullPath(item.WorkspacePath), StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The linked workspace for '{item.AppId}' does not match this draft.");
                }

                var session = await runtime.Sessions.LoadSnapshotAsync(item.SessionId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Session '{item.SessionId}' was not found.");
                if (!string.Equals(session.AppId, item.AppId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The draft session belongs to another app.");
                }

                var expectedRoot = Path.GetFullPath(Path.Combine(runtime.BaseFolderPath, "task-extraction-drafts", item.ExtractionId));
                var sourcePath = Path.GetFullPath(item.Draft.SourcePath);
                if (!string.Equals(Path.GetFullPath(item.Draft.DraftRootPath), expectedRoot, StringComparison.Ordinal)
                    || !sourcePath.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !File.Exists(sourcePath)
                    || !string.Equals(
                        (await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n'),
                        item.Draft.Source.TrimEnd('\r', '\n'),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The original source file for draft '{item.ExtractionId}' is missing or changed.");
                }

                var extraction = new ActiveTaskExtraction(
                    item.ExtractionId, item.SessionId, item.AppId, item.WorkspacePath,
                    item.StartUtc, item.EndUtc, item.TaskName, item.Description,
                    NormalizeMode(item.Mode), item.Model, item.Reasoning, null,
                    item.TaskNameIsAuthoritative,
                    item.ValidateSelectors, runtime.FeatureLifetime);
                var selectedTrees = session.VisualTreeSnapshots
                    .Where(tree => tree.CapturedAtUtc >= item.StartUtc && tree.CapturedAtUtc <= item.EndUtc)
                    .ToArray();
                var selectedImageCount = session.Images.Count(frame => frame.CapturedAtUtc >= item.StartUtc && frame.CapturedAtUtc <= item.EndUtc);
                extraction.ConfigureSelectorEvidence(session, LocalTaskSelectorEvidence.Create(selectedTrees, selectedImageCount));
                extraction.RestoreSnapshotState(item);
                restored.Add(extraction);
            }

            lock (gate)
            {
                if (restored.Any(item => extractions.ContainsKey(item.ExtractionId)))
                {
                    throw new InvalidDataException("One or more task drafts are already loaded.");
                }
                foreach (var item in restored)
                {
                    extractions.Add(item.ExtractionId, item);
                }
                PruneCompletedExtractions();
            }
            return restored.Select(static item => item.Snapshot()).ToArray();
        }
        catch
        {
            foreach (var item in restored)
            {
                item.Dispose();
            }
            throw;
        }
    }

    public LocalTaskExtractionSnapshot? Test(string extractionId, LocalTaskExtractionTestRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        ArgumentNullException.ThrowIfNull(request);
        ActiveTaskExtraction? extraction;
        lock (gate)
        {
            extraction = extractions.GetValueOrDefault(extractionId.Trim());
        }

        if (extraction is null)
        {
            return null;
        }

        extraction.BeginTest(request.SessionId);
        extraction.TestTask = RunTestAsync(extraction, request);
        return extraction.Snapshot();
    }

    public async Task<LocalTaskExtractionFailureDebugResult?> DebugTestFailureAsync(
        string extractionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        ActiveTaskExtraction? extraction;
        lock (gate)
        {
            extraction = extractions.GetValueOrDefault(extractionId.Trim());
        }

        if (extraction is null)
        {
            return null;
        }

        var debugContext = extraction.RequireFailureDebugContext();
        var result = debugContext.TestResult;
        var findings = new List<LocalTaskExtractionFailureDebugFinding>();
        var failedAssertions = result.Assertions.Where(static assertion => !assertion.Passed).ToArray();
        foreach (var assertion in failedAssertions)
        {
            findings.Add(new LocalTaskExtractionFailureDebugFinding(
                "assertionFailed",
                $"Assertion '{assertion.AssertionId}' failed: {assertion.Message}"));
        }

        var failedToolCall = result.ToolCalls
            .OrderBy(static call => call.Sequence)
            .FirstOrDefault(static call => call.IsError);
        if (failedToolCall is null)
        {
            var failureKind = failedAssertions.Length > 0 ? "assertionFailed" : "runtimeFailure";
            var summary = failedAssertions.Length > 0
                ? "The task reached its assertions, but at least one expected product outcome was false."
                : "The run failed without a failed UI tool call in the retained task trace.";
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                findings.Add(new LocalTaskExtractionFailureDebugFinding(
                    "runtimeError",
                    Truncate(result.StandardError.Trim(), 1000)));
            }

            return new LocalTaskExtractionFailureDebugResult(
                summary,
                failureKind,
                findings,
                [],
                DateTimeOffset.UtcNow);
        }

        findings.Add(new LocalTaskExtractionFailureDebugFinding(
            "failedToolCall",
            $"Tool call {failedToolCall.Sequence:N0} ({failedToolCall.ToolName}) failed after {failedToolCall.DurationMilliseconds:N0} ms: {failedToolCall.Message}"));
        var selectorCalls = LocalTaskSelectorEvidence.ExtractSelectorCalls(debugContext.Draft.Source);
        var failedSelector = FindSelectorCall(result.ToolCalls, failedToolCall, selectorCalls);
        if (failedSelector is null)
        {
            return new LocalTaskExtractionFailureDebugResult(
                "The failed tool call could not be mapped to a statically inspectable selector in the draft source.",
                "toolFailure",
                findings,
                [],
                DateTimeOffset.UtcNow);
        }

        var runStartUtc = result.ToolCalls.Min(static call => call.StartedAtUtc);
        var runEndUtc = result.ToolCalls.Max(static call => call.StartedAtUtc.AddMilliseconds(call.DurationMilliseconds));
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(
                result.SessionId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            findings.Add(new LocalTaskExtractionFailureDebugFinding(
                "evidenceUnavailable",
                $"Session evidence for test run '{result.RunId}' is no longer available."));
            return new LocalTaskExtractionFailureDebugResult(
                "The failed selector was identified, but the test session evidence is unavailable.",
                "evidenceUnavailable",
                findings,
                [],
                DateTimeOffset.UtcNow);
        }

        var runTrees = snapshot.VisualTreeSnapshots
            .Where(tree => IsWithin(tree.CapturedAtUtc, runStartUtc, runEndUtc))
            .OrderBy(static tree => tree.CapturedAtUtc)
            .ToArray();
        var runImages = snapshot.Images.Count(frame => IsWithin(frame.CapturedAtUtc, runStartUtc, runEndUtc));
        var evidence = LocalTaskSelectorEvidence.Create(runTrees, runImages);
        if (LocalTaskSelectorEvidence.CanUseOcr(failedSelector))
        {
            evidence = AugmentWithOcrEvidence(
                snapshot,
                runStartUtc,
                runEndUtc,
                evidence,
                [failedSelector],
                new HashSet<string>(StringComparer.Ordinal));
        }

        var selectorObserved = evidence.Nodes.Any(node => LocalTaskSelectorEvidence.Matches(
            node,
            failedSelector.Fields));
        if (selectorObserved)
        {
            findings.Add(new LocalTaskExtractionFailureDebugFinding(
                "selectorObserved",
                $"The selector was present in retained UI evidence during the run: {LocalTaskSelectorEvidence.FormatSelector(failedSelector.Fields)}. The failure is more likely a visibility, enabled-state, timing, or ambiguity problem."));
        }
        else
        {
            findings.Add(new LocalTaskExtractionFailureDebugFinding(
                "selectorNotObserved",
                $"The selector did not appear in any inspected visual-tree node or OCR result during the failed run: {LocalTaskSelectorEvidence.FormatSelector(failedSelector.Fields)}."));
        }

        var logOnlyFields = failedSelector.Fields
            .Where(field => snapshot.Logs.Any(log => IsWithin(log.TimestampUtc, runStartUtc, runEndUtc)
                                                   && log.Message.Contains(field.Value, StringComparison.OrdinalIgnoreCase)))
            .Select(static field => field.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!selectorObserved && logOnlyFields.Length > 0)
        {
            findings.Add(new LocalTaskExtractionFailureDebugFinding(
                "logOnlySelector",
                $"{string.Join(", ", logOnlyFields.Select(static value => JsonValue.Create(value)!.ToJsonString()))} appeared in logs but not in the UI evidence. Log and framework names are not valid proof of a UI selector."));
        }

        var orderedToolCalls = result.ToolCalls.OrderBy(static call => call.Sequence).ToArray();
        var failedToolIndex = Array.FindIndex(
            orderedToolCalls,
            call => call.Sequence == failedToolCall.Sequence);
        var precedingToolCall = failedToolIndex > 0 ? orderedToolCalls[failedToolIndex - 1] : null;
        var beforeEndUtc = precedingToolCall?.StartedAtUtc ?? failedToolCall.StartedAtUtc;
        var afterStartUtc = precedingToolCall?.StartedAtUtc.AddMilliseconds(
            precedingToolCall.DurationMilliseconds) ?? failedToolCall.StartedAtUtc;
        var beforeEvidence = new LocalTaskSelectorEvidenceIndex(
            runTrees.Count(tree => tree.CapturedAtUtc <= beforeEndUtc),
            runImages,
            evidence.OcrFrameCount,
            evidence.Nodes.Where(node => node.CapturedAtUtc <= beforeEndUtc).ToArray());
        var afterEvidence = new LocalTaskSelectorEvidenceIndex(
            runTrees.Count(tree => tree.CapturedAtUtc >= afterStartUtc),
            runImages,
            evidence.OcrFrameCount,
            evidence.Nodes.Where(node => node.CapturedAtUtc >= afterStartUtc).ToArray());
        var suggestions = LocalTaskSelectorEvidence.SuggestSelectors(
            failedSelector,
            beforeEvidence,
            afterEvidence);
        var debugSummary = selectorObserved
            ? "The selector existed in recorded UI evidence, so inspect its state constraints and timing."
            : logOnlyFields.Length > 0
                ? "The draft used an implementation or log string that was never exposed as a UI selector."
                : "The failed selector was not grounded in the recorded UI evidence.";
        return new LocalTaskExtractionFailureDebugResult(
            debugSummary,
            selectorObserved ? "selectorStateOrTiming" : "selectorNotObserved",
            findings,
            suggestions,
            DateTimeOffset.UtcNow);
    }

    public LocalTaskExtractionSnapshot? UpdateDraft(
        string extractionId,
        LocalTaskExtractionDraftUpdateRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        ArgumentNullException.ThrowIfNull(request);
        ActiveTaskExtraction? extraction;
        lock (gate)
        {
            extraction = extractions.GetValueOrDefault(extractionId.Trim());
        }

        if (extraction is null)
        {
            return null;
        }

        var currentDraft = extraction.RequireDraftForEditing();
        var result = MaterializeAndValidateDraft(
            extraction,
            new AgentDraftSubmission(
                currentDraft.SuggestedName,
                currentDraft.Summary,
                RequireValue(request.Source, "Task source is required.")));
        extraction.ApplyEditedDraft(result);
        return extraction.Snapshot();
    }

    public LocalTaskExtractionSnapshot? Commit(string extractionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        ActiveTaskExtraction? extraction;
        lock (gate)
        {
            extraction = extractions.GetValueOrDefault(extractionId.Trim());
        }

        if (extraction is null)
        {
            return null;
        }

        var draft = extraction.RequireReadyDraft();
        var taskDirectory = Path.Combine(
            extraction.WorkspacePath,
            RepositoryTaskLoader.TaskDirectoryRelativePath);
        Directory.CreateDirectory(taskDirectory);
        var destinationPath = Path.Combine(taskDirectory, draft.SuggestedName + ".ts");
        if (File.Exists(destinationPath))
        {
            throw new InvalidDataException(
                $"Task '{draft.SuggestedName}' already exists at '{destinationPath}'. Choose a different extraction brief or move the existing task.");
        }

        var temporaryPath = destinationPath + ".tmp-" + extraction.ExtractionId;
        File.WriteAllText(temporaryPath, draft.Source, new UTF8Encoding(false));
        var taskRuntimePath = Path.Combine(taskDirectory, "ansight-task.js");
        if (!File.Exists(taskRuntimePath))
            File.WriteAllText(taskRuntimePath, RepositoryModuleContractArtifacts.GetTaskRuntimeModule(), new UTF8Encoding(false));
        File.Move(temporaryPath, destinationPath);
        extraction.MarkCommitted(destinationPath);
        runtime.Analytics.RecordUsage("task_saved", outcome: "succeeded");
        return extraction.Snapshot();
    }

    public LocalTaskExtractionSnapshot? Cancel(string extractionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        ActiveTaskExtraction? extraction;
        lock (gate)
        {
            extraction = extractions.GetValueOrDefault(extractionId.Trim());
        }

        extraction?.Cancel();
        return extraction?.Snapshot();
    }

    public bool Discard(string extractionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        lock (gate)
        {
            if (!extractions.TryGetValue(extractionId.Trim(), out var extraction))
            {
                return false;
            }

            var snapshot = extraction.Snapshot();
            if (snapshot.Status is "queued" or "running" || snapshot.TestStatus == "running")
            {
                throw new InvalidDataException("Wait for the extraction or test run to finish before discarding its draft.");
            }
            if (snapshot.CommittedPath is not null)
            {
                throw new InvalidDataException("This task was saved to the workspace and is no longer a draft.");
            }

            var draftRoot = Path.Combine(runtime.BaseFolderPath, "task-extraction-drafts", extraction.ExtractionId);
            if (Directory.Exists(draftRoot))
            {
                Directory.Delete(draftRoot, recursive: true);
            }
            extractions.Remove(extraction.ExtractionId);
            extraction.Dispose();
            return true;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ActiveTaskExtraction[] active;
        lock (gate)
        {
            active = extractions.Values.ToArray();
        }

        foreach (var extraction in active)
        {
            extraction.Cancel();
        }
    }

    private async Task RunExtractionAsync(ActiveTaskExtraction extraction, string? replaceExtractionId)
    {
        var usageOutcome = "failed";
        extraction.Begin("Loading the selected capture period.");
        var stopwatch = Stopwatch.StartNew();
        WorkspaceTestRunPreparation? preparation = null;
        var tokens = SimulatorAgentTokenUsage.Empty;
        var modelPasses = new List<SimulatorAgentModelPassUsage>();
        try
        {
            var validationEnvironmentError = LocalTypeScriptTaskCompiler.GetEnvironmentError(
                runtime.JavaScriptExecutablePath);
            if (validationEnvironmentError is not null)
            {
                throw new InvalidOperationException(validationEnvironmentError);
            }

            var snapshot = await runtime.Sessions.LoadSnapshotAsync(
                    extraction.SessionId,
                    cancellationToken: extraction.Cancellation.Token)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Session '{extraction.SessionId}' could not be loaded.");
            if (!string.Equals(snapshot.AppId, extraction.AppId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The selected session app changed while extraction was starting.");
            }

            extraction.Report("evidence.loaded", "Building deterministic evidence from the selected period.");
            var seed = TimelineTaskExtractor.Extract(
                snapshot,
                extraction.StartUtc,
                extraction.EndUtc,
                extraction.TaskName);
            var selectedTrees = snapshot.VisualTreeSnapshots
                .Where(tree => IsWithin(tree.CapturedAtUtc, extraction))
                .ToArray();
            var selectedImages = snapshot.Images
                .Where(frame => IsWithin(frame.CapturedAtUtc, extraction))
                .ToArray();
            extraction.ConfigureSelectorEvidence(
                snapshot,
                LocalTaskSelectorEvidence.Create(selectedTrees, selectedImages.Length));

            preparation = await PrepareAgentAsync(extraction).ConfigureAwait(false);
            if (!preparation.IsSuccess)
            {
                throw new InvalidOperationException(preparation.Message);
            }
            extraction.ApplyReasoningConfiguration(preparation.ReasoningConfiguration
                ?? AgentReasoningConfiguration.CreateDefault(extraction.Reasoning, extraction.ModelOverride));
            if (preparation.UsesExternalTransport)
            {
                extraction.BeginTrace(preparation.TrackingRunId);
            }

            extraction.Report(
                "agent.started",
                "The extraction agent is inspecting the selected evidence.");
            var draftResult = await GenerateDraftAsync(
                    extraction,
                    snapshot,
                    seed,
                    preparation,
                    modelPasses,
                    usage => tokens = tokens.Add(usage))
                .ConfigureAwait(false);
            if (draftResult.IsValid)
            {
                extraction.Complete(draftResult.Draft, "The agent draft is ready to test.");
                usageOutcome = "succeeded";
                if (!string.IsNullOrWhiteSpace(replaceExtractionId))
                {
                    try
                    {
                        Discard(replaceExtractionId);
                    }
                    catch (Exception exception)
                    {
                        extraction.Report("previous-draft.cleanup", $"New draft is ready, but the previous draft could not be discarded: {exception.Message}");
                    }
                }
            }
            else
            {
                extraction.NeedsReview(draftResult.Draft, draftResult.Message);
            }
        }
        catch (OperationCanceledException) when (extraction.Cancellation.IsCancellationRequested)
        {
            extraction.Fail("cancelled", "Task extraction cancelled.");
        }
        catch (Exception exception)
        {
            extraction.Fail(
                "failed",
                SanitizeUserFacingMessage(
                    exception.GetBaseException().Message,
                    "Task extraction failed. Please try again."));
        }
        finally
        {
            runtime.Analytics.RecordUsage("task_extract",
                outcome: extraction.Cancellation.IsCancellationRequested ? "cancelled" : usageOutcome,
                durationSeconds: stopwatch.Elapsed.TotalSeconds);
            stopwatch.Stop();
            if (preparation?.UsesExternalTransport == true
                && preparation.TrackingRunId is { } trackingRunId
                && runtime.WorkspaceTests.RunGateway is { } gateway)
            {
                var completionStatus = extraction.Snapshot().Status switch
                {
                    "ready" => "succeeded",
                    "cancelled" => "cancelled",
                    _ => "failed"
                };
                try
                {
                    var completion = await gateway.CompleteAsync(
                            new WorkspaceTestRunMeterCompletion(
                                trackingRunId,
                                completionStatus,
                                stopwatch.ElapsedMilliseconds,
                                InstructionCount: 1,
                                ModelPassCount: modelPasses.Count,
                                AnsightToolCallCount: 0,
                                SuccessfulAnsightToolCallCount: 0,
                                preparation.ModelTransport is null ? null : tokens,
                                preparation.ModelTransport is null ? null : modelPasses),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    extraction.CompleteTrace(completion);
                    if (!completion.IsSuccess)
                    {
                        extraction.Report(
                            "metering.failed",
                            SanitizeUserFacingMessage(
                                completion.Message,
                                "Extraction usage metering could not be finalized."));
                    }
                }
                catch (Exception exception)
                {
                    var failureMessage = SanitizeUserFacingMessage(
                        exception.GetBaseException().Message,
                        "Extraction usage metering could not be finalized.");
                    extraction.FailTrace(failureMessage);
                    extraction.Report(
                        "metering.failed",
                        failureMessage);
                }
            }
        }
    }

    private async Task<WorkspaceTestRunPreparation> PrepareAgentAsync(ActiveTaskExtraction extraction)
    {
        var gateway = runtime.WorkspaceTests.RunGateway
                      ?? throw new InvalidOperationException("Task extraction is unavailable on this host.");
        return await gateway.PrepareAsync(
                new WorkspaceTestRunPreparationRequest(
                    extraction.TeamId,
                    extraction.WorkspacePath,
                    $"task-extraction-{extraction.ExtractionId}",
                    extraction.TaskName,
                    extraction.AppId,
                    extraction.ModelOverride,
                    ValidationAssertionCount: 1,
                    InstructionCount: 1,
                    IsDefinition: false)
                {
                    Reasoning = extraction.Reasoning
                },
                extraction.Cancellation.Token)
            .ConfigureAwait(false);
    }

    private async Task<DraftMaterializationResult> GenerateDraftAsync(
        ActiveTaskExtraction extraction,
        AppSessionSnapshot snapshot,
        TimelineTaskExtraction seed,
        WorkspaceTestRunPreparation preparation,
        ICollection<SimulatorAgentModelPassUsage> modelPasses,
        Action<SimulatorAgentTokenUsage> addUsage)
    {
        var transport = preparation.ModelTransport
            ?? throw new InvalidOperationException("Task extraction requires a brokered model transport.");
        var apiKey = await transport.ResolveAccessKeyAsync(extraction.Cancellation.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("The brokered model transport returned no access token.");
        if (extraction.Mode == LocalTaskExtractionModes.DirectWebSocket && preparation.ModelTransport?.SupportsWebSockets == false)
            throw new InvalidOperationException("The selected transport does not support WebSockets.");

        using var httpClient = new OpenAiResponsesClient();
        await using var webSocketSession = extraction.Mode == LocalTaskExtractionModes.DirectWebSocket
            ? new OpenAiResponsesWebSocketSession()
            : null;
        var history = new JsonArray(CreateUserInput(BuildAgentHandoff(
            extraction,
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions(),
            snapshot.AppToolCatalog)));
        var pendingInput = history.DeepClone().AsArray();
        var tools = BuildAgentTools();
        var requiresFullReplay = true;
        DraftMaterializationResult? lastRejectedDraft = null;
        var draftRevisionAttempts = 0;
        for (var turn = 1; turn <= MaximumAgentTurns; turn++)
        {
            extraction.Cancellation.Token.ThrowIfCancellationRequested();
            extraction.Report("agent.turn", $"Extraction agent pass {turn:N0}.");
            var passStartedAtUtc = DateTimeOffset.UtcNow;
            var passStopwatch = Stopwatch.StartNew();
            var instructions = BuildAgentInstructions();
            var context = CreateTracePayload(new JsonObject
            {
                ["instructions"] = instructions,
                ["input"] = history.DeepClone(),
                ["incrementalInput"] = pendingInput.DeepClone(),
                ["startNewConversation"] = requiresFullReplay,
                ["tools"] = tools.DeepClone()
            }.ToJsonString(), MaximumTraceContextCharacters);
            var request = new OpenAiRequest(
                apiKey,
                extraction.Model,
                instructions,
                history,
                tools,
                extraction.ReasoningEffort,
                $"ansight-task-extraction-{extraction.AppId}",
                10_000)
            {
                Transport = preparation.ModelTransport,
                IncrementalInput = pendingInput,
                StartNewConversation = requiresFullReplay
            };
            OpenAiTurn response;
            try
            {
                response = webSocketSession is null
                    ? await httpClient.CreateResponseAsync(request, extraction.Cancellation.Token).ConfigureAwait(false)
                    : await webSocketSession.CreateResponseAsync(request, extraction.Cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                passStopwatch.Stop();
                extraction.RecordTracePass(new LocalTaskExtractionModelPassTrace(
                    turn, null, extraction.Model, null, passStartedAtUtc, DateTimeOffset.UtcNow,
                    passStopwatch.ElapsedMilliseconds, SimulatorAgentTokenUsage.Empty, [])
                {
                    Reasoning = extraction.Reasoning,
                    Context = context,
                    Succeeded = false,
                    ErrorMessage = exception.Message
                });
                throw;
            }
            passStopwatch.Stop();
            var passCompletedAtUtc = DateTimeOffset.UtcNow;
            addUsage(response.Tokens);
            extraction.RecordTracePass(new LocalTaskExtractionModelPassTrace(
                turn,
                string.IsNullOrWhiteSpace(response.ResponseId) ? null : response.ResponseId,
                response.ResponseModel ?? extraction.Model,
                response.ResponseServiceTier,
                passStartedAtUtc,
                passCompletedAtUtc,
                passStopwatch.ElapsedMilliseconds,
                response.Tokens,
                response.FunctionCalls.Select(static call => call.Name).ToArray())
            {
                Reasoning = extraction.Reasoning,
                Context = context,
                AssistantOutput = CreateTracePayload(response.AssistantText, MaximumTracePayloadCharacters)
            });
            if (!string.IsNullOrWhiteSpace(response.ResponseId))
            {
                modelPasses.Add(new SimulatorAgentModelPassUsage(
                    response.ResponseId,
                    response.ResponseModel ?? extraction.Model,
                    response.ResponseServiceTier,
                    passCompletedAtUtc,
                    response.Tokens));
            }

            pendingInput.Clear();
            foreach (var output in response.Output)
            {
                history.Add(output?.DeepClone());
            }
            requiresFullReplay = false;

            foreach (var call in response.FunctionCalls)
            {
                var toolStartedAtUtc = DateTimeOffset.UtcNow;
                var toolStopwatch = Stopwatch.StartNew();
                if (string.Equals(call.Name, SubmitDraftToolName, StringComparison.Ordinal))
                {
                    try
                    {
                        var submission = ParseDraftSubmission(call.Arguments);
                        var draftResult = MaterializeAndValidateDraft(extraction, submission);
                        if (draftResult.IsValid)
                        {
                            RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                                JsonSerializer.Serialize(new { isSuccess = true, draftResult.Draft.SuggestedName, draftResult.Draft.TaskId, draftResult.Message }), false);
                            return draftResult;
                        }

                        lastRejectedDraft = draftResult;
                        if (draftResult.IsInfrastructureFailure)
                        {
                            RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                                JsonSerializer.Serialize(new { isSuccess = false, draftResult.Message, draftResult.Draft.ValidationWarnings }), true);
                            extraction.Report(
                                "validation.unavailable",
                                draftResult.Message);
                            return draftResult;
                        }

                        draftRevisionAttempts++;
                        var firstDiagnostic = Truncate(
                            draftResult.Draft.ValidationWarnings.FirstOrDefault()
                            ?? "The draft did not pass validation.",
                            500);
                        if (draftRevisionAttempts >= MaximumDraftRevisionAttempts)
                        {
                            var stalledMessage =
                                $"The extraction agent could not produce a valid module after {draftRevisionAttempts:N0} submissions. {firstDiagnostic}";
                            extraction.Report("agent.stalled", stalledMessage);
                            RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                                JsonSerializer.Serialize(new { isSuccess = false, message = stalledMessage, draftResult.Draft.ValidationWarnings }), true);
                            return DraftMaterializationResult.Invalid(draftResult.Draft, stalledMessage);
                        }

                        extraction.Report(
                            "agent.revising",
                            $"Draft validation failed ({draftRevisionAttempts:N0}/{MaximumDraftRevisionAttempts:N0}): {firstDiagnostic}");
                        var rejectedOutput = CreateFunctionOutput(
                            call.CallId,
                            SerializeObject(new
                            {
                                isSuccess = false,
                                message = "The submitted task was rejected. Revise the source and call submit_task_draft again.",
                                validationErrors = draftResult.Draft.ValidationWarnings
                            }).ToJsonString());
                        history.Add(rejectedOutput);
                        pendingInput.Add(rejectedOutput.DeepClone());
                        RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                            rejectedOutput.ToJsonString(), true);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or JsonException)
                    {
                        var rejectedOutput = CreateFunctionOutput(
                            call.CallId,
                            SerializeObject(new
                            {
                                isSuccess = false,
                                message = exception.GetBaseException().Message,
                                requiredAction = "Correct the complete TypeScript source and call submit_task_draft again."
                            }).ToJsonString());
                        history.Add(rejectedOutput);
                        pendingInput.Add(rejectedOutput.DeepClone());
                        RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                            rejectedOutput.ToJsonString(), true);
                    }
                    catch (Exception exception)
                    {
                        RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                            JsonSerializer.Serialize(new { isSuccess = false, message = exception.Message }), true);
                        throw;
                    }

                    continue;
                }

                JsonObject toolOutput;
                try
                {
                    toolOutput = call.Name switch
                    {
                        InspectSelectionToolName => BuildSelectionEvidence(snapshot, extraction, seed),
                        InspectTaskSeedToolName => BuildTaskSeedEvidence(seed),
                        InspectVisualTreesToolName => BuildVisualTreeEvidence(snapshot, extraction, call.Arguments),
                        InspectScreenshotOcrToolName => BuildScreenshotOcrEvidence(snapshot, extraction, call.Arguments),
                        InspectTaskSupportModulesToolName => BuildTaskSupportModuleEvidence(extraction, call.Arguments),
                        _ => new JsonObject
                        {
                            ["isSuccess"] = false,
                            ["message"] = $"Unknown extraction tool '{call.Name}'."
                        }
                    };
                }
                catch (Exception exception)
                {
                    RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                        JsonSerializer.Serialize(new { isSuccess = false, message = exception.Message }), true);
                    throw;
                }
                var functionOutput = CreateFunctionOutput(call.CallId, toolOutput.ToJsonString());
                history.Add(functionOutput);
                pendingInput.Add(functionOutput.DeepClone());
                RecordTraceToolCall(extraction, turn, call, toolStartedAtUtc, toolStopwatch,
                    toolOutput.ToJsonString(), toolOutput["isSuccess"]?.ToJsonString() == "false");
            }

            if (response.FunctionCalls.Count == 0)
            {
                var reminder = CreateUserInput(
                    "Continue the extraction with the provided tools. Submit a validated TypeScript draft with submit_task_draft; do not answer in prose.");
                history.Add(reminder);
                pendingInput.Add(reminder.DeepClone());
            }
        }

        return lastRejectedDraft
               ?? throw new InvalidOperationException(
                   $"The extraction agent did not submit a task draft within {MaximumAgentTurns:N0} passes.");
    }

    private DraftMaterializationResult MaterializeAndValidateDraft(
        ActiveTaskExtraction extraction,
        AgentDraftSubmission submission)
    {
        var suggestedName = Slugify(extraction.TaskNameIsAuthoritative
            ? extraction.TaskName
            : submission.SuggestedName);
        if (string.IsNullOrWhiteSpace(suggestedName))
        {
            suggestedName = "extracted-task";
        }

        var source = NormalizeTaskDescriptorSource(submission.Source.Trim());
        if (source.Length > MaximumSourceCharacters)
        {
            throw new InvalidDataException(
                $"The generated task exceeds the {MaximumSourceCharacters:N0}-character limit.");
        }
        var validationWarnings = new List<string>();
        if (!source.Contains("expect(", StringComparison.Ordinal)
            && !source.Contains("expect.soft(", StringComparison.Ordinal))
        {
            validationWarnings.Add("The task has no named product-outcome assertion.");
        }
        if (source.Contains("REVIEW:", StringComparison.OrdinalIgnoreCase))
        {
            validationWarnings.Add("The task still contains unresolved REVIEW markers.");
        }
        validationWarnings.AddRange(ValidateTaskActionBudget(source));
        validationWarnings.AddRange(ValidateTaskApiSurface(source));
        if (extraction.ValidateSelectors)
        {
            validationWarnings.AddRange(ValidateSelectorGrounding(extraction, source));
        }

        var draftRoot = Path.Combine(
            runtime.BaseFolderPath,
            "task-extraction-drafts",
            extraction.ExtractionId);
        var taskDirectory = Path.Combine(draftRoot, RepositoryTaskLoader.TaskDirectoryRelativePath);
        Directory.CreateDirectory(taskDirectory);
        CopyTaskSupportModules(extraction.WorkspacePath, taskDirectory);
        var sourcePath = Path.Combine(taskDirectory, suggestedName + ".ts");
        var previousSourcePath = extraction.ReplaceGeneratedSourcePath(sourcePath);
        if (previousSourcePath is not null
            && !string.Equals(previousSourcePath, sourcePath, StringComparison.Ordinal))
        {
            File.Delete(previousSourcePath);
        }
        File.WriteAllText(sourcePath, source + Environment.NewLine, new UTF8Encoding(false));
        var compilerValidation = LocalTypeScriptTaskCompiler.ValidateDetailed(
            taskDirectory,
            sourcePath,
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions(),
            runtime.JavaScriptExecutablePath);
        validationWarnings.AddRange(compilerValidation.Diagnostics);
        var catalog = runtime.InspectRepositoryTasks(extraction.AppId, draftRoot);
        validationWarnings.AddRange(catalog.Warnings);
        if (catalog.Tasks.Count != 1 && catalog.Warnings.Count == 0)
        {
            validationWarnings.Add($"Expected one task but discovered {catalog.Tasks.Count:N0}.");
        }
        if (catalog.Tasks.Count == 1 && extraction.TaskNameIsAuthoritative
            && !string.Equals(catalog.Tasks[0].Title, extraction.TaskName, StringComparison.Ordinal))
        {
            validationWarnings.Add(
                $"The task title must exactly match the requested task name '{extraction.TaskName}'.");
        }
        if (catalog.Tasks.Count == 1 && !extraction.TaskNameIsAuthoritative)
        {
            var generatedTitle = catalog.Tasks[0].Title.Trim();
            if (generatedTitle.Length > 60 || generatedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7)
            {
                validationWarnings.Add("Use a concise task title of at most seven words and 60 characters.");
            }
            if (!string.Equals(Slugify(generatedTitle), suggestedName, StringComparison.Ordinal))
            {
                validationWarnings.Add("The suggested task file name must be the kebab-case form of its title.");
            }
            if (validationWarnings.Count == 0)
            {
                extraction.ApplyGeneratedTaskName(generatedTitle);
            }
        }

        var draft = new LocalTaskExtractionDraft(
            suggestedName,
            submission.Summary.Trim(),
            source,
            catalog.Tasks.Count == 1 ? catalog.Tasks[0].TaskId : suggestedName,
            draftRoot,
            sourcePath,
            validationWarnings);
        return validationWarnings.Count == 0
            ? DraftMaterializationResult.Valid(draft)
            : DraftMaterializationResult.Invalid(
                draft,
                "The generated task needs edits before it can be tested. "
                + string.Join(" ", validationWarnings),
            compilerValidation.IsInfrastructureFailure);
    }

    private static void CopyTaskSupportModules(string workspacePath, string targetTaskDirectory)
    {
        var targetRoot = Path.GetFullPath(targetTaskDirectory) + Path.DirectorySeparatorChar;
        foreach (var module in RepositoryTaskLoader.DiscoverSupportModules(workspacePath))
        {
            var relativePath = module.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(targetTaskDirectory, relativePath));
            if (!targetPath.StartsWith(targetRoot, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, module.Source, new UTF8Encoding(false));
        }
    }

    private IReadOnlyList<string> ValidateSelectorGrounding(
        ActiveTaskExtraction extraction,
        string source)
    {
        var context = extraction.RequireSelectorEvidenceContext();
        var selectorCalls = LocalTaskSelectorEvidence.ExtractSelectorCalls(source);
        var initialIssues = LocalTaskSelectorEvidence.Validate(source, context.Evidence);
        var unresolvedOcrCalls = initialIssues
            .Select(static issue => issue.Call)
            .Where(LocalTaskSelectorEvidence.CanUseOcr)
            .ToArray();
        if (unresolvedOcrCalls.Length > 0)
        {
            context.Evidence = AugmentWithOcrEvidence(
                context.Snapshot,
                extraction.StartUtc,
                extraction.EndUtc,
                context.Evidence,
                unresolvedOcrCalls,
                context.ScannedOcrFrameIds);
        }

        if (selectorCalls.Count == 0)
        {
            return [];
        }

        var messages = new List<string>();
        var emptyEvidence = new LocalTaskSelectorEvidenceIndex(0, 0, 0, []);
        foreach (var issue in LocalTaskSelectorEvidence.Validate(source, context.Evidence))
        {
            messages.Add(issue.Message);
            if (issue.Call.Fields.Count == 0)
            {
                continue;
            }

            var alternatives = LocalTaskSelectorEvidence.SuggestSelectors(
                issue.Call,
                emptyEvidence,
                context.Evidence);
            if (alternatives.Count > 0)
            {
                messages.Add(
                    "Selectors actually observed in the selected UI evidence include: "
                    + string.Join(
                        ", ",
                        alternatives.Take(3).Select(static alternative => alternative.Selector.ToJsonString()))
                    + ".");
            }
        }

        return messages;
    }

    private LocalTaskSelectorEvidenceIndex AugmentWithOcrEvidence(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        LocalTaskSelectorEvidenceIndex evidence,
        IReadOnlyList<LocalTaskSelectorCall> unresolvedOcrCalls,
        ISet<string> scannedOcrFrameIds)
    {
        foreach (var frame in SelectRepresentativeFrames(
                     snapshot.Images.Where(frame => IsWithin(frame.CapturedAtUtc, startUtc, endUtc)),
                     MaximumOcrEvidenceFrames))
        {
            if (!scannedOcrFrameIds.Add(frame.FrameId))
            {
                continue;
            }

            var imagePath = SessionFileLocator.ResolveScreenshotPath(
                runtime.ApplicationPaths,
                snapshot,
                frame);
            if (!File.Exists(imagePath))
            {
                continue;
            }

            SessionScreenshotOcrResult scan;
            try
            {
                scan = ocrScanner.Scan(imagePath);
            }
            catch (Exception exception) when (exception is IOException
                                               or InvalidOperationException
                                               or UnauthorizedAccessException)
            {
                continue;
            }
            if (!scan.Available)
            {
                continue;
            }

            evidence = evidence.WithOcrBlocks(frame, scan.Blocks);
            if (unresolvedOcrCalls.All(call => evidence.Nodes.Any(
                    node => LocalTaskSelectorEvidence.Matches(node, call.Fields))))
            {
                break;
            }
        }

        return evidence;
    }

    private static IReadOnlyList<SessionImageFrame> SelectRepresentativeFrames(
        IEnumerable<SessionImageFrame> source,
        int maximumCount)
    {
        var frames = source
            .OrderBy(static frame => frame.CapturedAtUtc)
            .ThenBy(static frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();
        if (frames.Length <= maximumCount)
        {
            return frames;
        }

        var selected = new List<SessionImageFrame>(maximumCount);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < maximumCount; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (frames.Length - 1d) / (maximumCount - 1d),
                MidpointRounding.AwayFromZero);
            if (selectedIds.Add(frames[sourceIndex].FrameId))
            {
                selected.Add(frames[sourceIndex]);
            }
        }

        return selected;
    }

    private async Task RunTestAsync(
        ActiveTaskExtraction extraction,
        LocalTaskExtractionTestRequest request)
    {
        try
        {
            var draft = extraction.RequireReadyDraft();
            var sessionId = RequireValue(request.SessionId, "Choose a connected live session for the test run.");
            var session = runtime.Sessions.GetSummaries().FirstOrDefault(candidate => string.Equals(
                candidate.SessionId,
                sessionId,
                StringComparison.Ordinal));
            if (session is null || !runtime.IsSessionLive(sessionId))
            {
                throw new InvalidDataException("Choose a connected live session for the test run.");
            }
            if (!string.Equals(session.AppId, extraction.AppId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The test session belongs to '{session.AppId}', not '{extraction.AppId}'.");
            }

            extraction.Report("test.running", $"Running draft '{draft.TaskId}' on '{sessionId}'.");
            var result = await runtime.RunRepositoryTaskAsync(
                    draft.DraftRootPath,
                    extraction.AppId,
                    sessionId,
                    draft.TaskId,
                    request.Input,
                    extraction.Cancellation.Token)
                .ConfigureAwait(false);
            extraction.CompleteTest(CreateTestResult(result));
        }
        catch (OperationCanceledException) when (extraction.Cancellation.IsCancellationRequested)
        {
            extraction.FailTest("Task test run cancelled.");
        }
        catch (Exception exception)
        {
            extraction.FailTest(exception.GetBaseException().Message);
        }
    }

    private static LocalTaskExtractionTestResult CreateTestResult(RepositoryTaskRunResult result)
        => new(
            result.Status.ToString().ToLowerInvariant(),
            result.Message,
            result.RunId,
            result.SessionId,
            result.DurationMilliseconds,
            result.Assertions,
            result.ToolCalls,
            result.StandardError);

    internal static LocalTaskSelectorCall? FindSelectorCall(
        IReadOnlyList<RepositoryTaskToolCall> toolCalls,
        RepositoryTaskToolCall failedToolCall,
        IReadOnlyList<LocalTaskSelectorCall> selectorCalls)
    {
        var toolOccurrence = toolCalls
            .Where(call => call.Sequence <= failedToolCall.Sequence
                           && string.Equals(
                               call.ToolName,
                               failedToolCall.ToolName,
                               StringComparison.Ordinal))
            .Count();
        return selectorCalls
            .Where(call => string.Equals(
                call.ToolName,
                failedToolCall.ToolName,
                StringComparison.Ordinal))
            .ElementAtOrDefault(toolOccurrence - 1);
    }

    private static JsonObject BuildSelectionEvidence(
        AppSessionSnapshot snapshot,
        ActiveTaskExtraction extraction,
        TimelineTaskExtraction seed)
    {
        var touches = snapshot.Touches
            .Where(touch => IsWithin(touch.CapturedAtUtc, extraction))
            .OrderBy(touch => touch.CapturedAtUtc)
            .Take(200)
            .Select(touch => new
            {
                touch.Id,
                touch.Action,
                touch.CapturedAtUtc,
                touch.PointerId,
                touch.PointerCount,
                touch.NormalizedX,
                touch.NormalizedY,
                touch.CoordinateSpace
            });
        var visualTrees = snapshot.VisualTreeSnapshots
            .Where(tree => IsWithin(tree.CapturedAtUtc, extraction))
            .OrderBy(tree => tree.CapturedAtUtc)
            .Take(100)
            .Select(tree => new
            {
                tree.SnapshotId,
                tree.CapturedAtUtc,
                tree.VisualTreeKind,
                tree.RuntimePlatform,
                tree.NodeCount,
                tree.Truncated,
                tree.ScreenshotFrameId,
                tree.EvidencePhase
            });
        var screenshots = snapshot.Images
            .Where(frame => IsWithin(frame.CapturedAtUtc, extraction))
            .OrderBy(frame => frame.CapturedAtUtc)
            .Take(100)
            .Select(frame => new
            {
                frame.FrameId,
                frame.CapturedAtUtc,
                frame.Width,
                frame.Height
            });
        var logs = snapshot.Logs
            .Where(log => IsWithin(log.TimestampUtc, extraction))
            .OrderBy(log => log.TimestampUtc)
            .Take(200)
            .Select(log => new
            {
                log.TimestampUtc,
                log.Priority,
                log.Source,
                log.Tag,
                message = Truncate(log.Message, 1000)
            });
        var events = snapshot.ApplicationEvents
            .Where(item => IsWithin(item.CapturedAtUtc, extraction))
            .OrderBy(item => item.CapturedAtUtc)
            .Take(100);
        var annotations = snapshot.Annotations
            .Where(item => item.EndUtc.GetValueOrDefault(item.StartUtc) >= extraction.StartUtc
                           && item.StartUtc <= extraction.EndUtc)
            .OrderBy(item => item.StartUtc)
            .Take(100)
            .Select(item => new
            {
                item.AnnotationId,
                item.StartUtc,
                item.EndUtc,
                item.Label,
                item.Notes,
                target = item.Target
            });
        return SerializeObject(new
        {
            schema = "ansight.task-extraction-evidence/v1",
            session = new
            {
                snapshot.SessionId,
                snapshot.AppId,
                snapshot.ClientName,
                snapshot.CreatedUtc
            },
            selectedPeriod = new { extraction.StartUtc, extraction.EndUtc },
            taskName = extraction.TaskName,
            handoffDescription = extraction.Description,
            counts = new
            {
                touches = touches.Count(),
                visualTrees = visualTrees.Count(),
                screenshots = screenshots.Count(),
                logs = logs.Count(),
                applicationEvents = events.Count(),
                annotations = annotations.Count(),
                generatedReplaySteps = seed.ReplaySteps.Count
            },
            touches,
            visualTrees,
            screenshots,
            logs,
            applicationEvents = events,
            annotations,
            replayInstructions = seed.ReplayInstructions,
            seedDiagnostics = seed.Diagnostics
        });
    }

    private static JsonObject BuildTaskSeedEvidence(TimelineTaskExtraction seed)
        => SerializeObject(new
        {
            schema = "ansight.task-extraction-seed/v1",
            seed.SuggestedName,
            seed.Source,
            seed.GestureCount,
            seed.GeneratedActionCount,
            seed.Diagnostics,
            seed.ReplaySteps
        });

    private static JsonObject BuildVisualTreeEvidence(
        AppSessionSnapshot snapshot,
        ActiveTaskExtraction extraction,
        JsonObject arguments)
    {
        var requestedIds = arguments["snapshotIds"] is JsonArray values
            ? values.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var id) ? id : null)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .Take(3)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var candidates = snapshot.VisualTreeSnapshots
            .Where(tree => IsWithin(tree.CapturedAtUtc, extraction)
                           && (requestedIds.Count == 0 || requestedIds.Contains(tree.SnapshotId)))
            .OrderBy(tree => tree.CapturedAtUtc)
            .ToArray();
        var trees = (requestedIds.Count == 0
                ? SelectRepresentativeTrees(candidates, 3)
                : candidates.Take(3))
            .Select(tree => new
            {
                tree.SnapshotId,
                tree.CapturedAtUtc,
                tree.VisualTreeKind,
                tree.RuntimePlatform,
                tree.NodeCount,
                tree.Truncated,
                payload = ParseTruncatedJson(tree.Payload, 80_000)
            });
        return SerializeObject(new
        {
            schema = "ansight.task-extraction-visual-trees/v1",
            visualTrees = trees
        });
    }

    private JsonObject BuildScreenshotOcrEvidence(
        AppSessionSnapshot snapshot,
        ActiveTaskExtraction extraction,
        JsonObject arguments)
    {
        var requestedIds = arguments["frameIds"] is JsonArray values
            ? values.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var id) ? id : null)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .Take(3)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var candidates = snapshot.Images
            .Where(frame => IsWithin(frame.CapturedAtUtc, extraction)
                            && (requestedIds.Count == 0 || requestedIds.Contains(frame.FrameId)))
            .OrderBy(frame => frame.CapturedAtUtc)
            .ToArray();
        var frames = requestedIds.Count == 0
            ? SelectRepresentativeFrames(candidates, 3)
            : candidates.Take(3).ToArray();
        var context = extraction.RequireSelectorEvidenceContext();
        var results = new JsonArray();
        foreach (var frame in frames)
        {
            string? provider = null;
            string? message = null;
            if (context.ScannedOcrFrameIds.Add(frame.FrameId))
            {
                var imagePath = SessionFileLocator.ResolveScreenshotPath(
                    runtime.ApplicationPaths,
                    snapshot,
                    frame);
                if (!File.Exists(imagePath))
                {
                    message = "The retained screenshot file is unavailable.";
                }
                else
                {
                    try
                    {
                        var scan = ocrScanner.Scan(imagePath);
                        provider = scan.Provider;
                        message = scan.Message;
                        if (scan.Available)
                        {
                            context.Evidence = context.Evidence.WithOcrBlocks(frame, scan.Blocks);
                        }
                    }
                    catch (Exception exception) when (exception is IOException
                                                       or InvalidOperationException
                                                       or UnauthorizedAccessException)
                    {
                        message = "Screenshot OCR was unavailable for this frame.";
                    }
                }
            }

            var text = context.Evidence.Nodes
                .Where(node => string.Equals(node.Source, "ocr", StringComparison.Ordinal)
                               && string.Equals(node.SnapshotId, frame.FrameId, StringComparison.Ordinal))
                .SelectMany(static node => node.TextValues)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToArray();
            results.Add(SerializeObject(new
            {
                frame.FrameId,
                frame.CapturedAtUtc,
                available = text.Length > 0,
                provider,
                message,
                text
            }));
        }

        return new JsonObject
        {
            ["schema"] = "ansight.task-extraction-screenshot-ocr/v1",
            ["frames"] = results,
            ["message"] = results.Count == 0
                ? "No matching retained screenshot frames were found in the selected period."
                : null
        };
    }

    private static JsonObject BuildTaskSupportModuleEvidence(
        ActiveTaskExtraction extraction,
        JsonObject arguments)
    {
        var modules = RepositoryTaskLoader.DiscoverSupportModules(extraction.WorkspacePath);
        var requestedPaths = arguments["paths"] is JsonArray values
            ? values.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var path) ? path : null)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!)
                .Take(3)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        return SerializeObject(new
        {
            schema = "ansight.task-extraction-support-modules/v1",
            modules = modules.Select(module => new
            {
                path = module.RelativePath,
                module.IsDeclaration,
                source = requestedPaths.Contains(module.RelativePath)
                    ? Truncate(module.Source, 120_000)
                    : null,
                sourceTruncated = requestedPaths.Contains(module.RelativePath)
                                  && module.Source.Length > 120_000
            })
        });
    }

    private static IReadOnlyList<SessionVisualTreeSnapshot> SelectRepresentativeTrees(
        IReadOnlyList<SessionVisualTreeSnapshot> trees,
        int maximumCount)
    {
        if (trees.Count <= maximumCount)
        {
            return trees;
        }

        var selected = new List<SessionVisualTreeSnapshot>(maximumCount);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < maximumCount; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (trees.Count - 1d) / (maximumCount - 1d),
                MidpointRounding.AwayFromZero);
            if (selectedIds.Add(trees[sourceIndex].SnapshotId))
            {
                selected.Add(trees[sourceIndex]);
            }
        }

        return selected;
    }

    private static JsonArray BuildAgentTools()
        => new(
            EmptyObjectTool(
                InspectSelectionToolName,
                "Inspect the selected capture period, including touches, events, logs, annotations, visual-tree inventory, and inferred replay steps."),
            EmptyObjectTool(
                InspectTaskSeedToolName,
                "Inspect the deterministic TypeScript seed and its evidence diagnostics. Use it as grounding, then improve it to match the handoff."),
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = InspectVisualTreesToolName,
                ["description"] = "Inspect up to three visual-tree payloads from the selected period by snapshot ID. Pass an empty array to inspect representative beginning, middle, and ending trees.",
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["snapshotIds"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["maxItems"] = 3
                        }
                    },
                    ["required"] = new JsonArray("snapshotIds"),
                    ["additionalProperties"] = false
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = InspectScreenshotOcrToolName,
                ["description"] = "Inspect OCR text from up to three retained screenshots in the selected period by frame ID. Pass an empty array to inspect representative beginning, middle, and ending screenshots.",
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["frameIds"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["maxItems"] = 3
                        }
                    },
                    ["required"] = new JsonArray("frameIds"),
                    ["additionalProperties"] = false
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = InspectTaskSupportModulesToolName,
                ["description"] = "List workspace-authored task support modules, or inspect up to three exact module paths. Pass an empty array for the manifest. Reuse these modules instead of duplicating their types and helper functions.",
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["paths"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["maxItems"] = 3
                        }
                    },
                    ["required"] = new JsonArray("paths"),
                    ["additionalProperties"] = false
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = SubmitDraftToolName,
                ["description"] = "Submit the complete validated repository task draft. This ends extraction.",
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["suggestedName"] = StringSchema("Short kebab-case task file name without an extension."),
                        ["summary"] = StringSchema("Concise evidence-grounded description of what the task performs and verifies."),
                        ["source"] = StringSchema("Complete TypeScript source for the repository task.")
                    },
                    ["required"] = new JsonArray("suggestedName", "summary", "source"),
                    ["additionalProperties"] = false
                }
            });

    private static JsonObject EmptyObjectTool(string name, string description)
        => new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["strict"] = true,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["required"] = new JsonArray(),
                ["additionalProperties"] = false
            }
        };

    private static JsonObject StringSchema(string description)
        => new()
        {
            ["type"] = "string",
            ["description"] = description
        };

    private static string BuildAgentInstructions()
        => """
           You are Ansight's local repository-task extraction agent. Convert only the user-selected capture period into one deterministic, source-controlled TypeScript task.

           Inspect the selected period, deterministic seed, and the relevant visual trees before submitting. Inspect retained screenshot OCR when visual trees do not expose the visible text needed for a semantic assertion. Every literal UI selector value in ansight.ui or ansight.keyboard calls must be directly present in the selected session's UI evidence: a visible visual-tree node, or recorded screenshot OCR for a text selector. Logs, telemetry, framework navigation names, source-code class names, the handoff, and inferred component names do not prove that a selector exists. Never turn those strings into `type`, `automationId`, `text`, `role`, `nodeId`, `ancestorAutomationId`, or `action` selectors unless the same value appears on a captured UI node or in OCR. Prefer a captured automation ID; otherwise use captured visible text with its captured role. Avoid coordinate taps. Omit unsupported or ambiguous steps rather than fabricating them.

           The handoff contains Ansight's complete bundled `ansight-task.d.ts`. It is the sole authoritative execution contract. Every member used in the submitted module must be declared by that contract; never infer a convenience method from a raw tool name or from ordinary browser APIs. Host operations are feature-sliced: use `ansight.ui.tap`, `ansight.ui.waitFor`, and `ansight.ui.find`, never `ansight.tap`, `ansight.waitFor`, `ansight.find_ui`, or `ansight.getCurrentPage`. Framework inspection, when actually necessary, is feature-sliced under `app` (for example `app.maui.getCurrentPage`) and returns the `AppToolCallResult` envelope declared by the contract. Prefer `ansight.ui.find` and `ansight.ui.waitFor` for visible product outcomes.

           The handoff may also contain exact app-tool or artifact definitions selected by the user with `@tool:`, `@artifact-provider:`, or `@artifact:` references. These definitions describe device capabilities, but do not add typed convenience members to the TypeScript contract. Invoke a selected custom tool through the contract's generic `app.callTool` API, or use a declared standardized `app` suite method when one maps to the exact selected tool. Use artifact provider and artifact identifiers exactly as recorded.

           Workspace-authored `.ts` and `.d.ts` files without an exported `task` descriptor are support modules, not tasks. Use `inspect_task_support_modules` to discover them and inspect relevant sources. Prefer importing their existing types and helper functions over reproducing them in the generated task. The extraction compiler includes those support modules at their workspace-relative paths.

           The submitted source must statically export `task` and a default async run function, use only members declared in the bundled contract, declare an object input schema, await calls serially, stay within 100 actions, and contain at least one meaningful stable named `expect` assertion for the requested product outcome. A generic stability-only assertion is insufficient. Remove every REVIEW marker. Do not include markdown fences or prose in `source`.

           Do not copy the deterministic seed's `maximumActions` value after adding or changing calls. Omit `maximumActions` from extracted task descriptors unless the user explicitly requested a custom action budget; omission uses the host's safe 64-action default. If a custom value is required, it must cover every possible runtime `ansight` and `app` call, including calls made by branches, loops, retries, and imported helpers, and it cannot exceed 100.

           Assertions use Playwright-style mechanics: `expect(actual, { id, message? }).toBe(expected)`, `.toEqual(expected)`, `.toBeTruthy()`, `.toBeFalsy()`, `.toBeDefined()`, `.toBeUndefined()`, `.toBeNull()`, `.toContain(expected)`, or `.toContainEqual(expected)`. Negate a matcher with `.not`; use `expect.soft(actual, metadata)` only when later steps remain safe after failure. Every expectation requires a stable unique `id`. Do not invent convenience matchers such as `toBeVisible`; inspect product state with a member declared in the contract, then pass the observed value into a supported matcher.

           The exported `task` descriptor is extracted without executing TypeScript. It and every nested schema object must be strict JSON syntax: double-quote every property name and every string, use no comments or trailing commas inside the descriptor, and use only JSON values. Keep `satisfies TaskDefinition` after the closing brace. For example: `export const task = { "schemaVersion": 1, "title": "Example", "description": "Example task", "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false } } satisfies TaskDefinition;`.

           Every submission is compiled with strict TypeScript against that exact bundled declaration file, statically loaded as a repository task, and may be checked against the selected session's visual-tree and OCR evidence. If submit_task_draft returns diagnostics, repair all of them and resubmit the complete module; do not defend or work around an undeclared API or unobserved selector.

           Use tools to inspect evidence. Finish only by calling submit_task_draft.
           """;

    private static string BuildAgentHandoff(
        ActiveTaskExtraction extraction,
        string taskTypeDefinitions,
        SessionAppToolCatalogSnapshot? appToolCatalog)
        => $"""
           Extract a reusable Ansight repository task from the selected timeline period.

           App ID: {extraction.AppId}
           Session ID: {extraction.SessionId}
           Selected period: {extraction.StartUtc:O} to {extraction.EndUtc:O}

           {(extraction.TaskNameIsAuthoritative
               ? $"Requested task name (authoritative): {JsonSerializer.Serialize(extraction.TaskName)}. Use this exact value for the exported task descriptor's `title` and its kebab-case form for `suggestedName`."
               : "Choose a concise, descriptive task title from the handoff and evidence: two to five words, at most 60 characters. Name the user-visible outcome, not the full sequence of taps. Use this title in the exported task descriptor and its kebab-case form for `suggestedName`.")}

           Agent handoff from the user:
           {extraction.Description}

           BEGIN AUTHORITATIVE BUNDLED ANSIGHT TYPESCRIPT CONTRACT
           {taskTypeDefinitions}
           END AUTHORITATIVE BUNDLED ANSIGHT TYPESCRIPT CONTRACT

           {BuildTaskSupportModuleHandoff(extraction.WorkspacePath)}

           {BuildReferencedAppCapabilityHandoff(extraction.Description, appToolCatalog)}
           """;

    private static string BuildTaskSupportModuleHandoff(string workspacePath)
    {
        var modules = RepositoryTaskLoader.DiscoverSupportModules(workspacePath);
        if (modules.Count == 0)
        {
            return "No workspace-authored task support modules were discovered.";
        }

        var manifest = new JsonArray(modules.Select(module => (JsonNode?)new JsonObject
        {
            ["path"] = module.RelativePath,
            ["isDeclaration"] = module.IsDeclaration
        }).ToArray());
        return $"""
               BEGIN WORKSPACE TASK SUPPORT MODULE MANIFEST
               These modules are available for import. Call `inspect_task_support_modules` with exact paths to read relevant source before using them:
               {manifest.ToJsonString(jsonOptions)}
               END WORKSPACE TASK SUPPORT MODULE MANIFEST
               """;
    }

    internal static string BuildReferencedAppCapabilityHandoff(
        string description,
        SessionAppToolCatalogSnapshot? catalog)
    {
        if (catalog is null)
        {
            return "No recorded app-tool catalog is available for this session.";
        }

        var selectedTools = new JsonArray();
        if (catalog.ToolCatalog["tools"] is JsonArray tools)
        {
            foreach (var tool in tools.OfType<JsonObject>())
            {
                var toolId = ReadOptionalString(tool, "id");
                if (toolId is not null
                    && ContainsReferenceMention(description, $"@tool:{toolId}"))
                {
                    selectedTools.Add(tool.DeepClone());
                }
            }
        }

        var referencesArtifacts = description.Contains("@artifact:", StringComparison.Ordinal)
                                  || description.Contains("@artifact-provider:", StringComparison.Ordinal);
        if (selectedTools.Count == 0 && !referencesArtifacts)
        {
            return "No app-tool or artifact references were selected in the handoff.";
        }

        var selected = new JsonObject
        {
            ["capturedAtUtc"] = catalog.CapturedAtUtc,
            ["tools"] = selectedTools,
            ["artifactCatalog"] = referencesArtifacts
                ? catalog.ArtifactCatalog?.DeepClone()
                : null
        };
        return $"""
               BEGIN USER-REFERENCED SESSION CAPABILITIES
               The `@...` tokens in the handoff refer to these exact recorded definitions:
               {selected.ToJsonString(jsonOptions)}
               END USER-REFERENCED SESSION CAPABILITIES
               """;
    }

    private static bool ContainsReferenceMention(string description, string mention)
    {
        var searchStart = 0;
        while (searchStart < description.Length)
        {
            var index = description.IndexOf(mention, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var boundaryIndex = index + mention.Length;
            if (boundaryIndex >= description.Length
                || !IsReferenceIdentifierContinuation(description, boundaryIndex))
            {
                return true;
            }

            searchStart = boundaryIndex;
        }

        return false;
    }

    private static bool IsReferenceIdentifierContinuation(string description, int index)
    {
        var value = description[index];
        if (char.IsLetterOrDigit(value) || value is '_' or '-' or ':' or '/')
        {
            return true;
        }

        return value == '.'
               && index + 1 < description.Length
               && (char.IsLetterOrDigit(description[index + 1])
                   || description[index + 1] is '_' or '-');
    }

    private static AgentDraftSubmission ParseDraftSubmission(JsonObject arguments)
    {
        var suggestedName = ReadRequiredString(arguments, "suggestedName");
        var summary = ReadRequiredString(arguments, "summary");
        var source = ReadRequiredString(arguments, "source");
        return new AgentDraftSubmission(suggestedName, summary, source);
    }

    private static JsonObject CreateUserInput(string text)
        => new()
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = text
            })
        };

    private static JsonObject CreateFunctionOutput(string callId, string output)
        => new()
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        };

    private static JsonObject SerializeObject<T>(T value)
        => JsonSerializer.SerializeToNode(value, jsonOptions) as JsonObject ?? new JsonObject();

    private static JsonNode ParseTruncatedJson(JsonObject value, int maximumCharacters)
    {
        var serialized = value.ToJsonString();
        if (serialized.Length <= maximumCharacters)
        {
            return value.DeepClone();
        }

        return new JsonObject
        {
            ["truncated"] = true,
            ["originalCharacters"] = serialized.Length,
            ["prefix"] = serialized[..maximumCharacters]
        };
    }

    private static bool IsWithin(DateTimeOffset timestamp, ActiveTaskExtraction extraction)
        => IsWithin(timestamp, extraction.StartUtc, extraction.EndUtc);

    private static bool IsWithin(
        DateTimeOffset timestamp,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
        => timestamp >= startUtc && timestamp <= endUtc;

    internal static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)
            || mode.Equals(LocalTaskExtractionModes.Hosted, StringComparison.OrdinalIgnoreCase))
        {
            return LocalTaskExtractionModes.Hosted;
        }
        if (mode.Equals(LocalTaskExtractionModes.DirectWebSocket, StringComparison.OrdinalIgnoreCase)
            || mode.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("direct", StringComparison.OrdinalIgnoreCase))
        {
            return LocalTaskExtractionModes.DirectWebSocket;
        }

        throw new InvalidDataException("Task extraction mode is not supported.");
    }

    internal static string SanitizeUserFacingMessage(string? message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        string[] implementationTerms =
        [
            "api key",
            "broker",
            "credential",
            "openai",
            "proxy",
            "websocket"
        ];
        return implementationTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? fallback
            : message;
    }

    private static string NormalizeModel(string? model)
        => model?.Trim() ?? string.Empty;

    internal static void ValidateStartRequest(LocalTaskExtractionStartRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Description);
        _ = AgentReasoningModes.Normalize(request.Reasoning);
        if (request.EndUtc.ToUniversalTime() <= request.StartUtc.ToUniversalTime())
        {
            throw new InvalidDataException("Select a timeline period with a positive duration.");
        }
        if (request.Description.Trim().Length > 8_000)
        {
            throw new InvalidDataException("The extraction handoff must be 8,000 characters or fewer.");
        }
        if (request.TaskName?.Trim().Length > 200)
        {
            throw new InvalidDataException("The task name must be 200 characters or fewer.");
        }
    }

    internal static string ResolveTaskName(LocalTaskExtractionStartRequest request)
        => string.IsNullOrWhiteSpace(request.TaskName)
            ? request.Description.Trim()
            : request.TaskName.Trim();

    private static string ReadRequiredString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var result)
           && !string.IsNullOrWhiteSpace(result)
            ? result.Trim()
            : throw new InvalidDataException($"The extraction agent omitted '{propertyName}'.");

    private static string? ReadOptionalString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var result)
           && !string.IsNullOrWhiteSpace(result)
            ? result.Trim()
            : null;

    private static string RequireValue(string? value, string message)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException(message) : value.Trim();

    internal static string Slugify(string value)
    {
        var builder = new StringBuilder();
        var lastWasSeparator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (builder.Length >= 80)
            {
                break;
            }
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    internal static string NormalizeTaskDescriptorSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const string exportMarker = "export const task";
        var markerIndex = source.IndexOf(exportMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return source;
        }

        var equalsIndex = source.IndexOf('=', markerIndex + exportMarker.Length);
        if (equalsIndex < 0)
        {
            return source;
        }

        var objectStart = equalsIndex + 1;
        while (objectStart < source.Length && char.IsWhiteSpace(source[objectStart]))
        {
            objectStart++;
        }
        if (objectStart >= source.Length || source[objectStart] != '{')
        {
            return source;
        }

        var builder = new StringBuilder(source.Length + 64);
        builder.Append(source.AsSpan(0, objectStart));
        var isInString = false;
        var isEscaped = false;
        var stringDelimiter = '\0';
        var objectDepth = 0;
        for (var index = objectStart; index < source.Length; index++)
        {
            var character = source[index];
            if (isInString)
            {
                builder.Append(character);
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (character == '\\')
                {
                    isEscaped = true;
                }
                else if (character == stringDelimiter)
                {
                    isInString = false;
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                isInString = true;
                stringDelimiter = character;
                builder.Append(character);
                continue;
            }

            if (character == '{')
            {
                objectDepth++;
            }
            else if (character == '}')
            {
                objectDepth--;
                builder.Append(character);
                if (objectDepth == 0)
                {
                    builder.Append(source.AsSpan(index + 1));
                    return builder.ToString();
                }

                continue;
            }

            if (IsIdentifierStart(character))
            {
                var identifierEnd = index + 1;
                while (identifierEnd < source.Length && IsIdentifierPart(source[identifierEnd]))
                {
                    identifierEnd++;
                }

                var colonIndex = identifierEnd;
                while (colonIndex < source.Length && char.IsWhiteSpace(source[colonIndex]))
                {
                    colonIndex++;
                }
                var previousSignificant = PreviousSignificantCharacter(source, index - 1, objectStart);
                if (colonIndex < source.Length
                    && source[colonIndex] == ':'
                    && previousSignificant is '{' or ',')
                {
                    builder.Append('"');
                    builder.Append(source.AsSpan(index, identifierEnd - index));
                    builder.Append('"');
                    index = identifierEnd - 1;
                    continue;
                }
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    internal static IReadOnlyList<string> ValidateTaskApiSurface(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = TokenizeTaskSource(source);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var root = tokens[tokenIndex].Text;
            if (root is not "ansight" and not "app" and not "expect" and not "check")
            {
                continue;
            }

            var path = new List<string> { root };
            var cursor = tokenIndex + 1;
            while (cursor + 1 < tokens.Count
                   && tokens[cursor].Text == "."
                   && tokens[cursor + 1].IsIdentifier)
            {
                path.Add(tokens[cursor + 1].Text);
                cursor += 2;
            }
            if (cursor >= tokens.Count || tokens[cursor].Text != "(")
            {
                continue;
            }

            var callPath = string.Join('.', path);
            switch (root)
            {
                case "ansight":
                    ValidateFeatureCall(
                        path,
                        callPath,
                        RepositoryJavaScriptApiMethods.TaskApiSuites,
                        "ansight",
                        errors);
                    break;
                case "app":
                    ValidateFeatureCall(
                        path,
                        callPath,
                        RepositoryJavaScriptApiMethods.StandardAppToolSuites,
                        "app",
                        errors);
                    break;
                case "expect":
                    if (path.Count != 1
                        && (path.Count != 2 || !expectStaticMethods.Contains(path[1])))
                    {
                        errors.Add(
                            $"'{callPath}' is not declared by TaskExpect. Call expect(actual, metadata) or expect.soft(actual, metadata), then use a declared matcher.");
                    }
                    break;
                case "check":
                    errors.Add(
                        $"'{callPath}' is not declared by TaskInvocation. Use Playwright-style expect(actual, metadata).matcher(expected) assertions.");
                    break;
            }
        }

        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<string> ValidateTaskActionBudget(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var actionCallCount = CountTaskActionCalls(source);
        if (actionCallCount == 0)
        {
            return [];
        }

        int maximumActions;
        try
        {
            var descriptor = RepositoryModuleDescriptorReader.ExtractObject(
                source,
                "draft.ts",
                "task",
                "Task");
            using var document = JsonDocument.Parse(descriptor);
            maximumActions = document.RootElement.TryGetProperty("maximumActions", out var value)
                             && value.ValueKind == JsonValueKind.Number
                             && value.TryGetInt32(out var declaredMaximum)
                ? declaredMaximum
                : RepositoryTaskLoader.DefaultMaximumActionsPerTask;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            return [];
        }

        if (maximumActions >= actionCallCount)
        {
            return [];
        }

        return
        [
            $"The task declares an action budget of {maximumActions:N0}, but its source contains at least "
            + $"{actionCallCount:N0} ansight/app action calls. Omit maximumActions to use the "
            + $"{RepositoryTaskLoader.DefaultMaximumActionsPerTask:N0}-action default, or raise it to cover every possible runtime action."
        ];
    }

    private static int CountTaskActionCalls(string source)
    {
        var tokens = TokenizeTaskSource(source);
        var count = 0;
        for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            if (tokens[tokenIndex].Text is not "ansight" and not "app")
            {
                continue;
            }

            var pathLength = 1;
            var cursor = tokenIndex + 1;
            while (cursor + 1 < tokens.Count
                   && tokens[cursor].Text == "."
                   && tokens[cursor + 1].IsIdentifier)
            {
                pathLength++;
                cursor += 2;
            }

            if (pathLength >= 2 && cursor < tokens.Count && tokens[cursor].Text == "(")
            {
                count++;
            }
        }

        return count;
    }

    private static void ValidateFeatureCall(
        IReadOnlyList<string> path,
        string callPath,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> suites,
        string root,
        ISet<string> errors)
    {
        if (path.Count == 2 && string.Equals(path[1], "callTool", StringComparison.Ordinal))
        {
            return;
        }
        if (path.Count >= 3
            && suites.TryGetValue(string.Join('.', path.Skip(1).SkipLast(1)), out var methods)
            && methods.ContainsKey(path[^1]))
        {
            return;
        }

        if (string.Equals(root, "ansight", StringComparison.Ordinal)
            && path.Count == 2
            && RepositoryJavaScriptApiMethods.StandardHostToolSuites["ui"].ContainsKey(path[1]))
        {
            errors.Add(
                $"'{callPath}' is not declared by AnsightHost. Use 'ansight.ui.{path[1]}(...)'.");
            return;
        }
        if (string.Equals(callPath, "ansight.getCurrentPage", StringComparison.Ordinal))
        {
            errors.Add(
                "'ansight.getCurrentPage' is not declared by AnsightHost. For visible state use ansight.ui.find/waitFor; framework inspection is app.maui.getCurrentPage and returns AppToolCallResult.");
            return;
        }

        var availableSuites = string.Join(", ", suites.Keys.Order(StringComparer.Ordinal));
        errors.Add(
            $"'{callPath}' is not declared by the bundled task contract. {root} calls must use '{root}.<suite>.<method>(...)' (available suites: {availableSuites}) or the declared '{root}.callTool(...)' escape hatch.");
    }

    private static IReadOnlyList<TaskSourceToken> TokenizeTaskSource(string source)
    {
        var tokens = new List<TaskSourceToken>();
        for (var index = 0; index < source.Length;)
        {
            var character = source[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }
            if (character == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not '\r' and not '\n')
                {
                    index++;
                }
                continue;
            }
            if (character == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length
                       && (source[index] != '*' || source[index + 1] != '/'))
                {
                    index++;
                }
                index = Math.Min(source.Length, index + 2);
                continue;
            }
            if (character is '\'' or '"' or '`')
            {
                index = SkipQuotedSource(source, index, character);
                continue;
            }
            if (IsIdentifierStart(character))
            {
                var end = index + 1;
                while (end < source.Length && IsIdentifierPart(source[end]))
                {
                    end++;
                }
                tokens.Add(new TaskSourceToken(source[index..end], IsIdentifier: true));
                index = end;
                continue;
            }

            tokens.Add(new TaskSourceToken(character.ToString(), IsIdentifier: false));
            index++;
        }

        return tokens;
    }

    private static int SkipQuotedSource(string source, int start, char delimiter)
    {
        var escaped = false;
        for (var index = start + 1; index < source.Length; index++)
        {
            var character = source[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == delimiter)
            {
                return index + 1;
            }
        }

        return source.Length;
    }

    private static bool IsIdentifierStart(char character)
        => char.IsAsciiLetter(character) || character is '_' or '$';

    private static bool IsIdentifierPart(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '_' or '$';

    private static char PreviousSignificantCharacter(string source, int index, int lowerBound)
    {
        while (index >= lowerBound && char.IsWhiteSpace(source[index]))
        {
            index--;
        }

        return index >= lowerBound ? source[index] : '\0';
    }

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "…";

    private void PruneCompletedExtractions()
    {
        var completed = extractions.Values
            .Where(static extraction => extraction.IsTerminal)
            .OrderByDescending(static extraction => extraction.UpdatedAtUtc)
            .Skip(MaximumRetainedExtractions)
            .ToArray();
        foreach (var extraction in completed)
        {
            extractions.Remove(extraction.ExtractionId);
            extraction.Dispose();
        }
    }

    private static SimulatorAgentAuditPayload CreateTracePayload(string? content, int maximumCharacters)
    {
        var value = content ?? string.Empty;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return new SimulatorAgentAuditPayload(
            value.Length <= maximumCharacters ? value : value[..maximumCharacters],
            value.Length,
            value.Length > maximumCharacters,
            hash);
    }

    private static void RecordTraceToolCall(
        ActiveTaskExtraction extraction,
        int passSequence,
        OpenAiFunctionCall call,
        DateTimeOffset startedAtUtc,
        Stopwatch stopwatch,
        string result,
        bool isError)
    {
        stopwatch.Stop();
        extraction.RecordTraceToolCall(new LocalTaskExtractionToolCallTrace(
            0,
            passSequence,
            call.CallId,
            call.Name,
            startedAtUtc,
            stopwatch.ElapsedMilliseconds,
            CreateTracePayload(call.Arguments.ToJsonString(), MaximumTracePayloadCharacters),
            CreateTracePayload(result, MaximumTracePayloadCharacters),
            isError));
    }

    private sealed record AgentDraftSubmission(string SuggestedName, string Summary, string Source);

    private readonly record struct TaskSourceToken(string Text, bool IsIdentifier);

    private sealed record DraftMaterializationResult(
        LocalTaskExtractionDraft Draft,
        bool IsValid,
        string Message,
        bool IsInfrastructureFailure)
    {
        public static DraftMaterializationResult Valid(LocalTaskExtractionDraft draft)
            => new(draft, true, string.Empty, false);

        public static DraftMaterializationResult Invalid(
            LocalTaskExtractionDraft draft,
            string message,
            bool isInfrastructureFailure = false)
            => new(draft, false, message, isInfrastructureFailure);
    }

    private sealed class ActiveTaskExtraction : IDisposable
    {
        private readonly Lock gate = new();
        private readonly List<LocalTaskExtractionProgress> progress = [];
        private string status = "queued";
        private string message = "Task extraction queued.";
        private LocalTaskExtractionDraft? draft;
        private string testStatus = "idle";
        private string? testMessage;
        private LocalTaskExtractionTestResult? testResult;
        private string? committedPath;
        private SelectorEvidenceContext? selectorEvidenceContext;
        private Guid? traceRunId;
        private string traceStatus = "idle";
        private string? traceMessage;
        private SimulatorAgentTokenUsage traceTokens = SimulatorAgentTokenUsage.Empty;
        private SimulatorAgentRunCost? traceCost;
        private readonly List<LocalTaskExtractionModelPassTrace> traceModelPasses = [];
        private readonly List<LocalTaskExtractionToolCallTrace> traceToolCalls = [];

        public ActiveTaskExtraction(
            string extractionId,
            string sessionId,
            string appId,
            string workspacePath,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            string taskName,
            string description,
            string mode,
            string model,
            string reasoning,
            Guid? teamId,
            bool taskNameIsAuthoritative,
            bool validateSelectors,
            CancellationToken featureLifetime)
        {
            ExtractionId = extractionId;
            SessionId = sessionId;
            AppId = appId;
            WorkspacePath = workspacePath;
            StartUtc = startUtc;
            EndUtc = endUtc;
            TaskName = taskName;
            Description = description;
            Mode = mode;
            Model = model;
            ModelOverride = model;
            Reasoning = reasoning;
            TeamId = teamId;
            TaskNameIsAuthoritative = taskNameIsAuthoritative;
            ValidateSelectors = validateSelectors;
            CreatedAtUtc = DateTimeOffset.UtcNow;
            UpdatedAtUtc = CreatedAtUtc;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(featureLifetime);
            AddProgress("queued", message);
        }

        public string ExtractionId { get; }
        public string SessionId { get; }
        public string AppId { get; }
        public string WorkspacePath { get; }
        public DateTimeOffset StartUtc { get; }
        public DateTimeOffset EndUtc { get; }
        public string TaskName { get; private set; }
        public bool TaskNameIsAuthoritative { get; }
        public string Description { get; }
        public string Mode { get; }
        public string Model { get; private set; }
        public string ModelOverride { get; }
        public string Reasoning { get; }
        public string ReasoningEffort { get; private set; } = "medium";
        public string? ReasoningConfigurationRevision { get; private set; }
        public Guid? TeamId { get; }
        public bool ValidateSelectors { get; }
        public DateTimeOffset CreatedAtUtc { get; private set; }
        public DateTimeOffset UpdatedAtUtc { get; private set; }
        public CancellationTokenSource Cancellation { get; }
        public Task? WorkTask { get; set; }
        public Task? TestTask { get; set; }
        private string? generatedSourcePath;

        public string? ReplaceGeneratedSourcePath(string sourcePath)
        {
            lock (gate)
            {
                var previousSourcePath = generatedSourcePath;
                generatedSourcePath = sourcePath;
                return previousSourcePath;
            }
        }

        public void ApplyReasoningConfiguration(AgentReasoningConfiguration configuration)
        {
            lock (gate)
            {
                Model = configuration.Model;
                ReasoningEffort = configuration.ReasoningEffort;
                ReasoningConfigurationRevision = configuration.Revision;
            }
        }

        public void ApplyGeneratedTaskName(string title)
        {
            lock (gate)
            {
                TaskName = title;
                Touch();
            }
        }

        public void BeginTrace(Guid? runId)
        {
            lock (gate)
            {
                traceRunId = runId;
                traceStatus = "collecting";
                traceMessage = null;
                traceTokens = SimulatorAgentTokenUsage.Empty;
                traceCost = null;
                traceModelPasses.Clear();
                traceToolCalls.Clear();
                Touch();
            }
        }

        public void RecordTracePass(LocalTaskExtractionModelPassTrace pass)
        {
            ArgumentNullException.ThrowIfNull(pass);
            lock (gate)
            {
                if (traceStatus == "idle")
                {
                    traceStatus = "collecting";
                }

                traceTokens = traceTokens.Add(pass.Tokens);
                traceModelPasses.Add(pass);
                Touch();
            }
        }

        public void RecordTraceToolCall(LocalTaskExtractionToolCallTrace call)
        {
            lock (gate)
            {
                traceToolCalls.Add(call with { Sequence = traceToolCalls.Count + 1 });
                Touch();
            }
        }

        public void CompleteTrace(WorkspaceTestRunMeterResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            lock (gate)
            {
                traceStatus = result.IsSuccess ? "complete" : "failed";
                traceMessage = string.IsNullOrWhiteSpace(result.Message) ? null : result.Message;
                traceCost = result.Cost;
                Touch();
            }
        }

        public void FailTrace(string failureMessage)
        {
            lock (gate)
            {
                traceStatus = "failed";
                traceMessage = failureMessage;
                Touch();
            }
        }

        public void ConfigureSelectorEvidence(
            AppSessionSnapshot snapshot,
            LocalTaskSelectorEvidenceIndex evidence)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(evidence);
            lock (gate)
            {
                selectorEvidenceContext = new SelectorEvidenceContext(snapshot, evidence);
            }
        }

        public void RestoreSnapshotState(LocalTaskExtractionSnapshot snapshot)
        {
            lock (gate)
            {
                status = snapshot.Status;
                message = snapshot.Message;
                draft = snapshot.Draft;
                CreatedAtUtc = snapshot.CreatedAtUtc;
                UpdatedAtUtc = snapshot.UpdatedAtUtc;
                Model = snapshot.Model;
                ReasoningEffort = snapshot.ReasoningEffort ?? ReasoningEffort;
                ReasoningConfigurationRevision = snapshot.ReasoningConfigurationRevision;
                progress.Clear();
                progress.AddRange(snapshot.Progress.TakeLast(MaximumProgressEntries));
                testStatus = snapshot.TestStatus == "running" ? "idle" : snapshot.TestStatus;
                testMessage = snapshot.TestStatus == "running" ? null : snapshot.TestMessage;
                testResult = snapshot.TestStatus == "running" ? null : snapshot.TestResult;
                traceStatus = snapshot.Trace?.Status ?? "idle";
                traceMessage = snapshot.Trace?.Message;
                traceRunId = snapshot.Trace?.RunId;
                traceTokens = snapshot.Trace?.Tokens ?? SimulatorAgentTokenUsage.Empty;
                traceCost = snapshot.Trace?.CalculatedCost;
                traceModelPasses.Clear();
                traceToolCalls.Clear();
                if (snapshot.Trace is not null)
                {
                    traceModelPasses.AddRange(snapshot.Trace.ModelPasses);
                    traceToolCalls.AddRange(snapshot.Trace.ToolCalls);
                }
            }
        }

        public SelectorEvidenceContext RequireSelectorEvidenceContext()
        {
            lock (gate)
            {
                return selectorEvidenceContext
                       ?? throw new InvalidOperationException(
                           "The selected UI evidence has not finished loading.");
            }
        }

        public bool IsTerminal
        {
            get
            {
                lock (gate)
                {
                    return status is "ready" or "needsReview" or "failed" or "cancelled" or "committed";
                }
            }
        }

        public void Begin(string nextMessage)
        {
            lock (gate)
            {
                status = "running";
                message = nextMessage;
                Touch();
                AddProgress("started", nextMessage);
            }
        }

        public void Report(string stage, string nextMessage)
        {
            lock (gate)
            {
                message = nextMessage;
                Touch();
                AddProgress(stage, nextMessage);
            }
        }

        public void Complete(LocalTaskExtractionDraft nextDraft, string nextMessage)
        {
            lock (gate)
            {
                draft = nextDraft;
                status = "ready";
                message = nextMessage;
                Touch();
                AddProgress("draft.ready", nextMessage);
            }
        }

        public void NeedsReview(LocalTaskExtractionDraft nextDraft, string nextMessage)
        {
            lock (gate)
            {
                draft = nextDraft;
                status = "needsReview";
                message = nextMessage;
                Touch();
                AddProgress("draft.needs-review", nextMessage);
            }
        }

        public void ApplyEditedDraft(DraftMaterializationResult result)
        {
            lock (gate)
            {
                if (testStatus == "running")
                {
                    throw new InvalidDataException("Wait for the task test run to finish before editing the draft.");
                }

                draft = result.Draft;
                status = result.IsValid ? "ready" : "needsReview";
                message = result.IsValid
                    ? "The edited task draft passed static validation."
                    : result.Message;
                testStatus = "idle";
                testMessage = null;
                testResult = null;
                Touch();
                AddProgress(result.IsValid ? "draft.validated" : "draft.needs-review", message);
            }
        }

        public void Fail(string nextStatus, string nextMessage)
        {
            lock (gate)
            {
                status = nextStatus;
                message = nextMessage;
                Touch();
                AddProgress(nextStatus, nextMessage);
            }
        }

        public void BeginTest(string? sessionId)
        {
            lock (gate)
            {
                if (status is not "ready" and not "committed")
                {
                    throw new InvalidDataException("Wait for a ready task draft before testing it.");
                }
                if (testStatus == "running")
                {
                    throw new InvalidDataException("This task draft already has a test run in progress.");
                }

                testStatus = "running";
                testMessage = $"Starting test run on '{sessionId?.Trim()}'.";
                testResult = null;
                Touch();
                AddProgress("test.started", testMessage);
            }
        }

        public void CompleteTest(LocalTaskExtractionTestResult result)
        {
            lock (gate)
            {
                testResult = result;
                testStatus = result.Status;
                testMessage = result.Message;
                Touch();
                AddProgress("test.completed", result.Message);
            }
        }

        public void FailTest(string nextMessage)
        {
            lock (gate)
            {
                testStatus = "failed";
                testMessage = nextMessage;
                Touch();
                AddProgress("test.failed", nextMessage);
            }
        }

        public LocalTaskExtractionDraft RequireReadyDraft()
        {
            lock (gate)
            {
                if (draft is null)
                {
                    throw new InvalidDataException("Wait for the agent to produce a task draft.");
                }
                if (draft.ValidationWarnings.Count > 0 || status == "needsReview")
                {
                    throw new InvalidDataException("Resolve the task draft validation errors before testing or saving it.");
                }

                return draft;
            }
        }

        public LocalTaskExtractionDraft RequireDraftForEditing()
        {
            lock (gate)
            {
                if (draft is null)
                {
                    throw new InvalidDataException("Wait for the agent to produce a task draft.");
                }
                if (testStatus == "running")
                {
                    throw new InvalidDataException("Wait for the task test run to finish before editing the draft.");
                }
                if (status == "committed")
                {
                    throw new InvalidDataException("The task was already saved and can no longer be edited in this extraction.");
                }

                return draft;
            }
        }

        public FailureDebugContext RequireFailureDebugContext()
        {
            lock (gate)
            {
                if (testStatus == "running")
                {
                    throw new InvalidDataException("Wait for the task test run to finish before debugging it.");
                }
                if (testResult is null || draft is null)
                {
                    throw new InvalidDataException("Run the task draft before debugging a failure.");
                }
                if (testStatus is "passed" or "succeeded")
                {
                    throw new InvalidDataException("The latest task test passed and has no failure to debug.");
                }

                return new FailureDebugContext(draft, testResult);
            }
        }

        public void MarkCommitted(string path)
        {
            lock (gate)
            {
                committedPath = path;
                status = "committed";
                message = $"Saved task to '{path}'.";
                Touch();
                AddProgress("task.committed", message);
            }
        }

        public void Cancel()
        {
            Cancellation.Cancel();
            lock (gate)
            {
                if (status is "queued" or "running")
                {
                    status = "cancelled";
                    message = "Task extraction cancelled.";
                    Touch();
                    AddProgress("cancelled", message);
                }
            }
        }

        public LocalTaskExtractionSnapshot Snapshot()
        {
            lock (gate)
            {
                return new LocalTaskExtractionSnapshot(
                    "ansight.local-task-extraction/v1",
                    ExtractionId,
                    status,
                    message,
                    SessionId,
                    AppId,
                    WorkspacePath,
                    StartUtc,
                    EndUtc,
                    TaskName,
                    Description,
                    Mode,
                    Model,
                    ValidateSelectors,
                    CreatedAtUtc,
                    UpdatedAtUtc,
                    progress.ToArray(),
                    draft,
                    testStatus,
                    testMessage,
                    testResult,
                    committedPath,
                    traceStatus == "idle"
                        ? null
                        : new LocalTaskExtractionTrace(
                            traceStatus,
                            traceMessage,
                            traceRunId,
                            traceTokens,
                            traceCost,
                            traceModelPasses.ToArray())
                        {
                            ToolCalls = traceToolCalls.ToArray()
                        })
                {
                    Reasoning = Reasoning,
                    ReasoningEffort = ReasoningEffort,
                    ReasoningConfigurationRevision = ReasoningConfigurationRevision,
                    TaskNameIsAuthoritative = TaskNameIsAuthoritative
                };
            }
        }

        public void Dispose()
            => Cancellation.Dispose();

        private void Touch()
            => UpdatedAtUtc = DateTimeOffset.UtcNow;

        private void AddProgress(string stage, string nextMessage)
        {
            progress.Add(new LocalTaskExtractionProgress(stage, nextMessage, DateTimeOffset.UtcNow));
            if (progress.Count > MaximumProgressEntries)
            {
                progress.RemoveRange(0, progress.Count - MaximumProgressEntries);
            }
        }
    }

    private sealed class SelectorEvidenceContext
    {
        public SelectorEvidenceContext(
            AppSessionSnapshot snapshot,
            LocalTaskSelectorEvidenceIndex evidence)
        {
            Snapshot = snapshot;
            Evidence = evidence;
        }

        public AppSessionSnapshot Snapshot { get; }
        public LocalTaskSelectorEvidenceIndex Evidence { get; set; }
        public HashSet<string> ScannedOcrFrameIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed record FailureDebugContext(
        LocalTaskExtractionDraft Draft,
        LocalTaskExtractionTestResult TestResult);
}
