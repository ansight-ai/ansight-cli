using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class DeleteSessionTool : Operation
{
    public DeleteSessionTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_delete_session";

    protected override string Title => "Delete Session";

    protected override string Description => "Delete a captured session by explicit session id. Requires confirmDelete=true and rejects live sessions.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Required session id to delete."),
            ["confirmDelete"] = ToolSchema.Boolean("Required true value to confirm permanent deletion."),
            ["expectedAppId"] = ToolSchema.String("Optional app id guard; delete is rejected if the session app differs.", nullable: true),
            ["expectedName"] = ToolSchema.String("Optional session display-name guard; delete is rejected if the current name differs.", nullable: true)
        },
        required: ["sessionId", "confirmDelete"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var sessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        if (sessionId is null)
        {
            return Task.FromResult(ToolError("sessionId is required."));
        }

        if (!ArgumentReader.TryReadOptionalBooleanArgument(arguments, "confirmDelete", out var confirmDelete, out var errorMessage)
            || confirmDelete != true)
        {
            return Task.FromResult(ToolError(errorMessage ?? "confirmDelete must be true."));
        }

        if (!sessionResolver.TryLoadResolvedSnapshot(sessionId, out var snapshot, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage));
        }

        if (SessionReviewContext.IsLiveSession(sessionResolver, snapshot!))
        {
            return Task.FromResult(ToolError("Live sessions cannot be deleted while they are still active."));
        }

        var expectedAppId = NormalizeOptionalString(arguments?["expectedAppId"]?.GetValue<string>());
        if (expectedAppId is not null && !string.Equals(snapshot!.AppId, expectedAppId, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolError($"Session '{snapshot.SessionId}' appId is '{snapshot.AppId}', not '{expectedAppId}'."));
        }

        var expectedName = NormalizeOptionalString(arguments?["expectedName"]?.GetValue<string>());
        if (expectedName is not null && !string.Equals(snapshot!.Name, expectedName, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolError($"Session '{snapshot.SessionId}' name is '{snapshot.Name}', not '{expectedName}'."));
        }

        var result = runtimeState.DeleteSession(snapshot!.SessionId);
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
                ["deletedSession"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, isLive: false)
            },
            isError: false));
    }
}
