using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent;
using Ansight.Host.SimulatorAgent.OpenAi;
using Ansight.Host.SimulatorAgent.Prompts;
using Ansight.Host.Workspaces.Execution;
using Ansight.Host.Workspaces.Cloud;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>Authors external tests from the same replay evidence used for Ansight tests.</summary>
public static class ExternalScriptRefiner
{
    private const int MaximumAttempts = 3;

    public static Task<ExternalScriptRefinement> RefineAsync(
        RuntimeCoordinator runtime,
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title,
        string source,
        string format,
        string reasoningMode,
        string? modelOverride,
        string? workspaceOverride,
        CancellationToken cancellationToken,
        Action<string>? reportProgress = null,
        string? generationNotes = null)
        => RefineAsync(runtime.WorkspaceTests.RunGateway
            ?? throw new InvalidOperationException("AI test generation is unavailable on this host."),
            runtime, snapshot, startUtc, endUtc, title, source, format, reasoningMode, modelOverride,
            workspaceOverride, cancellationToken, reportProgress, generationNotes);

    internal static async Task<ExternalScriptRefinement> RefineAsync(
        IWorkspaceTestRunGateway gateway,
        RuntimeCoordinator runtime,
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title,
        string source,
        string format,
        string reasoningMode,
        string? modelOverride,
        string? workspaceOverride,
        CancellationToken cancellationToken,
        Action<string>? reportProgress = null,
        string? generationNotes = null)
    {
        if (format is not ("maestro" or "appium")) throw new InvalidDataException("Choose Maestro or Appium for extraction.");
        if (endUtc <= startUtc) throw new InvalidDataException("Select a timeline period with a positive duration.");
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256_000)
            throw new InvalidDataException("The starting draft must contain 1 to 256,000 characters.");
        if (generationNotes?.Length > 4_000) throw new InvalidDataException("Generation notes cannot exceed 4,000 characters.");

        var reasoning = AgentReasoningModes.Normalize(reasoningMode);
        var requestedModel = string.IsNullOrWhiteSpace(modelOverride)
            ? AgentReasoningConfiguration.CreateDefault(reasoning).Model : modelOverride.Trim();
        var workspacePath = workspaceOverride ?? runtime.Apps.Get(snapshot.AppId)?.CodebasePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidDataException($"App '{snapshot.AppId}' must be linked to a workspace before generating a {format} test.");
        reportProgress?.Invoke("Preparing model access…");
        var preparation = await gateway.PrepareAsync(new WorkspaceTestRunPreparationRequest(
            TeamId: null, workspacePath, $"{format}-extraction-{Guid.NewGuid():N}", title, snapshot.AppId,
            requestedModel, ValidationAssertionCount: 1, InstructionCount: 1, IsDefinition: false)
        {
            Reasoning = reasoning
        }, cancellationToken).ConfigureAwait(false);
        if (!preparation.IsSuccess) throw new InvalidOperationException(preparation.Message);

        var stopwatch = Stopwatch.StartNew();
        var passes = new List<SimulatorAgentModelPassUsage>();
        var tokens = SimulatorAgentTokenUsage.Empty;
        var outcome = "failed";
        try
        {
            var transport = preparation.ModelTransport
                ?? throw new InvalidOperationException("AI test generation requires a model transport.");
            var accessKey = await transport.ResolveAccessKeyAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessKey)) throw new InvalidOperationException("AI test generation could not be authorized.");
            var configuration = preparation.ReasoningConfiguration
                ?? AgentReasoningConfiguration.CreateDefault(reasoning, requestedModel);
            reportProgress?.Invoke("Selecting recorded actions, annotations, and UI evidence across the selected period…");
            var content = new JsonArray(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = ExternalScriptExtractionPrompt.Create(snapshot, startUtc, endUtc, title, source, format, generationNotes)
            });
            var sectionEnds = snapshot.Annotations
                .Where(annotation => annotation.StartUtc >= startUtc && annotation.EndUtc <= endUtc)
                .Select(annotation => annotation.EndUtc).OfType<DateTimeOffset>();
            var screenshotCount = ReplayExtractionEvidence.AppendScreenshots(runtime, snapshot, startUtc, endUtc, sectionEnds, content);
            reportProgress?.Invoke($"Added {screenshotCount} screenshot(s) to the AI request.");
            var input = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content });
            using var client = new OpenAiResponsesClient();
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                reportProgress?.Invoke(attempt == 1
                    ? $"Waiting for {configuration.Model} to analyze the journey and write the {format} test…"
                    : $"Repairing {format} draft after validation (attempt {attempt}/{MaximumAttempts})…");
                var turn = await client.CreateResponseAsync(new OpenAiRequest(
                    accessKey, configuration.Model,
                    $"You author evidence-grounded {format} mobile UI tests. Return only the complete test source.",
                    input, new JsonArray(), configuration.ReasoningEffort,
                    $"ansight-{format}-extraction-{snapshot.AppId}", 12_000) { Transport = transport }, cancellationToken).ConfigureAwait(false);
                tokens = tokens.Add(turn.Tokens);
                if (turn.ResponseId is { Length: > 0 } responseId)
                    passes.Add(new SimulatorAgentModelPassUsage(responseId, turn.ResponseModel ?? configuration.Model,
                        turn.ResponseServiceTier, DateTimeOffset.UtcNow, turn.Tokens));
                var refinedSource = StripCodeFence(turn.AssistantText) + "\n";
                reportProgress?.Invoke("AI response received. Checking syntax and app ID…");
                try
                {
                    await ExternalScriptValidator.ValidateAsync(format, refinedSource, snapshot.AppId,
                        runtime.JavaScriptExecutablePath, cancellationToken).ConfigureAwait(false);
                    outcome = "succeeded";
                    return new ExternalScriptRefinement(refinedSource, turn.ResponseModel ?? configuration.Model);
                }
                catch (InvalidDataException error) when (attempt < MaximumAttempts)
                {
                    input.Add(new JsonObject { ["role"] = "assistant", ["content"] = refinedSource });
                    input.Add(new JsonObject { ["role"] = "user", ["content"] =
                        $"The draft failed validation: {error.Message}\nRepair it using the original evidence. Return the complete source only." });
                }
            }
            throw new InvalidDataException("The AI could not produce a valid test draft.");
        }
        finally
        {
            if (preparation.UsesExternalTransport && preparation.TrackingRunId is { } trackingRunId)
                await gateway.CompleteAsync(new WorkspaceTestRunMeterCompletion(
                    trackingRunId, outcome, stopwatch.ElapsedMilliseconds, InstructionCount: 1,
                    ModelPassCount: passes.Count, AnsightToolCallCount: 0, SuccessfulAnsightToolCallCount: 0,
                    tokens, passes), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string StripCodeFence(string source)
    {
        var trimmed = source.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && closingFence > firstNewline ? trimmed[(firstNewline + 1)..closingFence].Trim() : trimmed;
    }
}
