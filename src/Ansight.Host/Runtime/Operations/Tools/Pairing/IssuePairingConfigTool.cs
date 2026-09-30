using System.Text.Json.Nodes;
using Ansight.Pairing;
using Ansight.Pairing.Models;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Pairing;

internal sealed class IssuePairingConfigTool : PairingOperation
{
    public IssuePairingConfigTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_issue_enrollment_invite";

    protected override string Title => "Issue Enrollment Invite";

    protected override string Description => "Issue a QR-ready enrollment invite. Omit appId to create a generic invite that registers whichever app scans it.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Optional app identifier or package id. Omit to issue a generic any-app invite.", nullable: true),
            ["appName"] = ToolSchema.String("Optional display name for an app-specific invite. Defaults to the known app name or appId when omitted.", nullable: true),
            ["duration"] = ToolSchema.String("Optional lifetime such as 30m, 12h, 7d, or 1mo. Defaults to 1mo.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(BuildIssuePairingConfigResult(arguments));
}
