using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RemoteApp;

internal sealed class ListAppToolsTool : RemoteAppOperation
{
    public ListAppToolsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_app_tools";

    protected override string Title => "List App Tools";

    protected override string Description =>
        "Search a live paired app's remotely available Ansight tools by keywords or feature, including current guard policy and schemas.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["ifRevision"] = ToolSchema.String(
                "Known catalog revision. The app returns unchanged=true when it still matches.",
                nullable: true),
            ["query"] = ToolSchema.String(
                "Focused keyword query matched against indexed tool id, name, description, category, and keywords. All words must match.",
                nullable: true),
            ["feature"] = ToolSchema.String(
                "Feature or domain hint such as navigation, media, search, local data, or settings.",
                nullable: true),
            ["category"] = ToolSchema.String(
                "Exact case-insensitive tool category to return.",
                nullable: true),
            ["idPrefix"] = ToolSchema.String(
                "Case-insensitive tool id prefix to return.",
                nullable: true),
            ["toolId"] = ToolSchema.String(
                "Exact case-insensitive tool id to return.",
                nullable: true),
            ["policy"] = ToolSchema.String(
                "Optional required-policy filter.",
                enumValues: ["read", "write", "critical"],
                nullable: true),
            ["executableOnly"] = ToolSchema.Boolean(
                "Return only tools executable under the current live guard and authorization policy. Defaults to true.",
                nullable: true),
            ["maxResults"] = ToolSchema.Integer(
                "Maximum directly matching tools to return before supplemental prerequisites. Defaults to 20 and is capped at 50.",
                nullable: true),
            ["detail"] = ToolSchema.String(
                "Return summary metadata without schemas, or full matching definitions. Defaults to full.",
                enumValues: ["summary", "full"],
                nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => BuildListAppToolsResultAsync(arguments, correlationId);
}
