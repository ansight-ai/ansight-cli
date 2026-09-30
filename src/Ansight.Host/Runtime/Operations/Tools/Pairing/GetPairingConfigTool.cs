using System.Text.Json.Nodes;
using Ansight.Pairing;
using Ansight.Pairing.Models;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Pairing;

internal sealed class GetPairingConfigTool : PairingOperation
{
    public GetPairingConfigTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_enrollment_invite";

    protected override string Title => "Get Enrollment Invite";

    protected override string Description => "Return one current enrollment invite.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["inviteId"] = ToolSchema.String("Required enrollment invite identifier.")
        },
        required: ["inviteId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(BuildGetPairingConfigResult(arguments));
}
