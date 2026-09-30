using System.Text.Json.Nodes;
using Ansight.Pairing;
using Ansight.Pairing.Models;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Pairing;

internal sealed class ListPairingConfigsTool : PairingOperation
{
    public ListPairingConfigsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_enrollment_invites";

    protected override string Title => "List Enrollment Invites";

    protected override string Description => "List current enrollment invites, optionally filtered by app and status.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Optional app identifier to filter results.", nullable: true),
            ["includeConsumed"] = ToolSchema.Boolean("Include configs already consumed. Defaults to true.", nullable: true),
            ["includeExpired"] = ToolSchema.Boolean("Include configs whose expiry has passed. Defaults to true.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(RequestResult.ToolResult(BuildListPairingConfigsPayload(arguments), isError: false));
}
