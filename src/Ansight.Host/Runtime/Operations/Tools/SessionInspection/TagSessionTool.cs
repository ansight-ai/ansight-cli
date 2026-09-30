using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class TagSessionTool : Operation
{
    public TagSessionTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_tag_session";

    protected override string Title => "Tag Session";

    protected override string Description => "Replace, add, or remove captured-session tags and update review metadata such as pin, notes, and name.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to update.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["tags"] = ToolSchema.Array(
                ToolSchema.String("Tag value."),
                description: "Optional full replacement tag set. Existing tags are preserved when omitted.",
                nullable: true),
            ["addTags"] = ToolSchema.Array(
                ToolSchema.String("Tag value to add."),
                description: "Optional tags to merge into the session.",
                nullable: true),
            ["removeTags"] = ToolSchema.Array(
                ToolSchema.String("Tag value to remove."),
                description: "Optional tags to remove from the session.",
                nullable: true),
            ["notes"] = ToolSchema.String("Optional replacement session notes.", nullable: true),
            ["name"] = ToolSchema.String("Optional replacement session display name.", nullable: true),
            ["isPinned"] = ToolSchema.Boolean("Optional pinned state.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return Task.FromResult(ToolError(resolutionError));
        }

        if (!ArgumentReader.TryReadOptionalBooleanArgument(arguments, "isPinned", out var isPinned, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid pinned state."));
        }

        var tags = BuildUpdatedTags(snapshot!, arguments);
        var notes = arguments?["notes"] is null
            ? snapshot!.Notes
            : NormalizeOptionalString(arguments?["notes"]?.GetValue<string>());
        var name = arguments?["name"] is null
            ? null
            : NormalizeOptionalString(arguments?["name"]?.GetValue<string>());
        var result = runtimeState.UpdateSessionMetadata(
            snapshot!.SessionId,
            isPinned ?? snapshot.IsPinned,
            tags,
            notes,
            name);
        if (!result.IsSuccess)
        {
            return Task.FromResult(ToolError(result.Message));
        }

        if (!runtimeState.TryGetSessionSnapshot(snapshot.SessionId, out var updatedSnapshot) || updatedSnapshot is null)
        {
            updatedSnapshot = snapshot;
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sessionId"] = updatedSnapshot.SessionId,
                ["appId"] = updatedSnapshot.AppId,
                ["name"] = updatedSnapshot.Name,
                ["isPinned"] = updatedSnapshot.IsPinned,
                ["notes"] = updatedSnapshot.Notes,
                ["tags"] = PayloadJson.CreateJsonArray(updatedSnapshot.Tags.Select(tag => JsonValue.Create(tag))),
                ["session"] = PayloadJson.BuildSessionPayload(updatedSnapshot, sessionResolver.GetLiveSessionIds().Contains(updatedSnapshot.SessionId))
            },
            isError: false));
    }

    private static IReadOnlyList<string> BuildUpdatedTags(AppSessionSnapshot snapshot, JsonObject? arguments)
    {
        var tags = arguments?["tags"] is null
            ? new SortedSet<string>(snapshot.Tags, StringComparer.OrdinalIgnoreCase)
            : new SortedSet<string>(ArgumentReader.ReadStringSet(arguments, "tags"), StringComparer.OrdinalIgnoreCase);

        foreach (var tag in ArgumentReader.ReadStringSet(arguments, "addTags"))
        {
            tags.Add(tag);
        }

        foreach (var tag in ArgumentReader.ReadStringSet(arguments, "removeTags"))
        {
            tags.Remove(tag);
        }

        return tags
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
    }
}
