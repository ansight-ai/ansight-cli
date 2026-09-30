using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Pairing;

internal abstract class PairingOperation : Operation
{
    protected PairingOperation(OperationServices services)
        : base(services)
    {
    }

    protected RequestResult BuildIssuePairingConfigResult(JsonObject? arguments)
    {
        var requestedAppId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        var durationInput = NormalizeOptionalString(arguments?["duration"]?.GetValue<string>());
        var requestedAppName = NormalizeOptionalString(arguments?["appName"]?.GetValue<string>());
        var result = hostPairingService.Issue(requestedAppId, requestedAppName, durationInput);
        if (!result.IsSuccess || result.Invite is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["appId"] = requestedAppId ?? PairingConfig.AnyAppId,
                    ["message"] = result.Message
                },
                isError: true);
        }

        var invite = result.Invite;
        var configJson = result.InviteJson ?? string.Empty;
        var configNode = string.IsNullOrWhiteSpace(configJson) ? null : JsonNode.Parse(configJson);

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["scope"] = invite.Scope,
                ["appId"] = invite.AppId,
                ["appName"] = invite.AppName,
                ["inviteId"] = invite.InviteId,
                ["duration"] = result.Duration,
                ["protocolVersion"] = 2,
                ["issuedAtUtc"] = invite.IssuedAtUtc,
                ["expiresAtUtc"] = invite.ExpiresAtUtc,
                ["inviteFilePath"] = result.InviteFilePath,
                ["invite"] = configNode,
                ["inviteJson"] = configJson
            },
            isError: false);
    }

    protected JsonObject BuildListPairingConfigsPayload(JsonObject? arguments)
    {
        var appId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        var includeConsumed = arguments?["includeConsumed"]?.GetValue<bool>() ?? true;
        var includeExpired = arguments?["includeExpired"]?.GetValue<bool>() ?? true;
        var configs = hostPairingService.List(appId, includeConsumed, includeExpired)
            .Select(item => (JsonNode?)BuildPairingConfigSummaryPayload(item))
            .ToArray();

        return new JsonObject
        {
            ["count"] = configs.Length,
            ["appId"] = appId,
            ["includeConsumed"] = includeConsumed,
            ["includeExpired"] = includeExpired,
            ["invites"] = PayloadJson.CreateJsonArray(configs)
        };
    }

    protected RequestResult BuildGetPairingConfigResult(JsonObject? arguments)
    {
        var configId = NormalizeOptionalString(arguments?["inviteId"]?.GetValue<string>());
        if (string.IsNullOrWhiteSpace(configId))
        {
            return ToolError("inviteId is required.");
        }

        var item = hostPairingService.Get(configId);
        if (item is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["inviteId"] = configId,
                    ["message"] = $"Enrollment invite '{configId}' was not found."
                },
                isError: true);
        }

        var payload = BuildPairingConfigSummaryPayload(item.Summary);
        payload["invite"] = JsonNode.Parse(item.InviteJson);
        payload["inviteJson"] = item.InviteJson;
        return RequestResult.ToolResult(payload, isError: false);
    }

    protected static JsonObject BuildPairingConfigSummaryPayload(PairingInviteSummary item)
    {
        return new JsonObject
        {
            ["inviteId"] = item.InviteId,
            ["scope"] = item.Scope,
            ["appId"] = item.AppId,
            ["appName"] = item.AppName,
            ["schema"] = item.Schema,
            ["issuedAtUtc"] = item.IssuedAtUtc,
            ["expiresAtUtc"] = item.ExpiresAtUtc,
            ["consumed"] = item.IsConsumed,
            ["isExpired"] = item.IsExpired,
            ["status"] = item.Status
        };
    }
}
