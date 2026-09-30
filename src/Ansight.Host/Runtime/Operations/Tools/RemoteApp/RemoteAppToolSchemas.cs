using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RemoteApp;

internal static class RemoteAppToolSchemas
{
    public static ToolSchema AfterEvidence(bool nullable)
        => ToolSchema.Object(
            description: "Optional post-call verification captured by the app after the operation completes.",
            properties: new Dictionary<string, ToolSchema>
            {
                ["include"] = ToolSchema.Array(
                    ToolSchema.String(
                        "Evidence kind.",
                        enumValues: ["visualTree", "screenshot"]),
                    description: "Evidence to capture. Defaults to visualTree.",
                    nullable: true),
                ["delayMilliseconds"] = ToolSchema.Integer(
                    "Delay before capture, from 0 through 2000 milliseconds.",
                    nullable: true),
                ["visualTreeArguments"] = ToolSchema.Object(
                    description: "Arguments forwarded to ui.get_visual_tree.",
                    properties: new Dictionary<string, ToolSchema>(),
                    additionalProperties: true,
                    nullable: true),
                ["screenshotArguments"] = ToolSchema.Object(
                    description: "Arguments forwarded to ui.get_screenshot.",
                    properties: new Dictionary<string, ToolSchema>(),
                    additionalProperties: true,
                    nullable: true)
            },
            additionalProperties: false,
            nullable: nullable);
}
