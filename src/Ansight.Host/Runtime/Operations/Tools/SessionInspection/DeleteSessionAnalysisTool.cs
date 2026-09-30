using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class DeleteSessionAnalysisTool : Operation
{
    public DeleteSessionAnalysisTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_delete_session_analysis";

    protected override string Title => "Delete Session Analysis";

    protected override string Description => "Delete a saved session analysis record, optionally guarding by the current agent id.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: AnalysisProperties(),
        required: ["analysisId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Select a session."));
        }

        var analysisId = NormalizeOptionalString(arguments?["analysisId"]?.GetValue<string>());
        if (analysisId is null)
        {
            return Task.FromResult(ToolError("analysisId is required."));
        }

        var existingAnalysis = snapshot!.Analyses.FirstOrDefault(analysis =>
            string.Equals(analysis.AnalysisId, analysisId, StringComparison.Ordinal));
        if (existingAnalysis is null)
        {
            return Task.FromResult(ToolError($"Analysis '{analysisId}' was not found for session '{snapshot.SessionId}'."));
        }

        var expectedAgentId = NormalizeOptionalString(arguments?["expectedAgentId"]?.GetValue<string>());
        if (expectedAgentId is not null && !string.Equals(existingAnalysis.AgentId, expectedAgentId, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolError($"Analysis '{analysisId}' agentId is '{existingAnalysis.AgentId}', not '{expectedAgentId}'."));
        }

        var result = runtimeState.DeleteSessionAnalysis(snapshot.SessionId, analysisId);
        if (!result.IsSuccess)
        {
            return Task.FromResult(ToolError(result.Message));
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["analysisId"] = existingAnalysis.AnalysisId,
                ["deletedAnalysis"] = BuildAnalysisPayload(existingAnalysis)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> AnalysisProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["analysisId"] = ToolSchema.String("Required analysis id to delete.");
        properties["expectedAgentId"] = ToolSchema.String("Optional agent id guard; the delete is rejected if the analysis agent differs.", nullable: true);
        return properties;
    }

    private static JsonObject BuildAnalysisPayload(SessionAnalysisRecord analysis)
    {
        return new JsonObject
        {
            ["analysisId"] = analysis.AnalysisId,
            ["agentId"] = analysis.AgentId,
            ["analysisKind"] = analysis.AnalysisKind,
            ["startedUtc"] = analysis.StartedUtc,
            ["completedUtc"] = analysis.CompletedUtc,
            ["success"] = analysis.Success,
            ["statusMessage"] = analysis.StatusMessage,
            ["hasPrompt"] = !string.IsNullOrWhiteSpace(analysis.Prompt),
            ["hasTranscript"] = !string.IsNullOrWhiteSpace(analysis.Transcript),
            ["hasFinalResponse"] = !string.IsNullOrWhiteSpace(analysis.FinalResponse),
            ["hasMermaidDefinition"] = !string.IsNullOrWhiteSpace(analysis.MermaidDefinition)
        };
    }
}
