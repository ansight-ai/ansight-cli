using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Pairing;

internal sealed class DeletePairingConfigTool : PairingOperation
{
    public DeletePairingConfigTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_delete_enrollment_invite";

    protected override string Title => "Delete Enrollment Invite";

    protected override string Description => "Delete an enrollment invite by exact invite and app identifiers.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["inviteId"] = ToolSchema.String("Required enrollment invite identifier to delete."),
            ["confirmDelete"] = ToolSchema.Boolean("Required true value to confirm permanent deletion."),
            ["expectedAppId"] = ToolSchema.String("Required app id guard; deletion is rejected if the config belongs to another app.")
        },
        required: ["inviteId", "confirmDelete", "expectedAppId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var configId = NormalizeOptionalString(arguments?["inviteId"]?.GetValue<string>());
        if (configId is null)
        {
            return Task.FromResult(ToolError("inviteId is required."));
        }

        if (!ArgumentReader.TryReadOptionalBooleanArgument(arguments, "confirmDelete", out var confirmDelete, out var errorMessage)
            || confirmDelete != true)
        {
            return Task.FromResult(ToolError(errorMessage ?? "confirmDelete must be true."));
        }

        var expectedAppId = NormalizeOptionalString(arguments?["expectedAppId"]?.GetValue<string>());
        if (expectedAppId is null)
        {
            return Task.FromResult(ToolError("expectedAppId is required."));
        }

        var item = hostPairingService.Get(configId);
        if (item is null)
        {
            return Task.FromResult(ToolError($"Enrollment invite '{configId}' was not found."));
        }

        if (!string.Equals(item.Summary.AppId, expectedAppId, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolError(
                $"Enrollment invite '{configId}' appId is '{item.Summary.AppId}', not '{expectedAppId}'."));
        }

        if (item.Summary.IsReadOnly)
        {
            return Task.FromResult(ToolError(
                "Team-synced connection profiles are read-only in Ansight. Ask a team admin or owner to revoke the profile from the portal."));
        }

        var deletedConfig = BuildPairingConfigSummaryPayload(item.Summary);
        var result = hostPairingService.Revoke(configId, expectedAppId);
        if (!result.IsSuccess)
        {
            return Task.FromResult(ToolError(result.Message));
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = $"Enrollment invite '{configId}' was deleted.",
                ["inviteId"] = configId,
                ["appId"] = item.Summary.AppId,
                ["deletedConfig"] = deletedConfig,
                ["remainingConfigCount"] = hostPairingService.List().Count
            },
            isError: false));
    }
}
