using System.Text.Json.Nodes;

namespace Ansight.Host.UiAutomation;

public sealed class RuntimeFocusedControlSnapshot
{
    public required string SessionId { get; init; }

    public required string AppId { get; init; }

    public required string ClientName { get; init; }

    public required string Source { get; init; }

    public required string NodeId { get; init; }

    public required string Type { get; init; }

    public required string Label { get; init; }

    public required string AutomationId { get; init; }

    public required int Depth { get; init; }

    public required int ChildCount { get; init; }

    public required DateTimeOffset FocusedAtUtc { get; init; }

    public string ScreenshotArtifactPath { get; init; } = string.Empty;

    public string ScreenshotStatus { get; init; } = string.Empty;

    public JsonObject? LocalBounds { get; init; }

    public JsonObject? AbsoluteBounds { get; init; }

    public JsonObject? NormalizedBounds { get; init; }

    public required JsonObject Node { get; init; }

    public RuntimeFocusedControlSnapshot Clone()
    {
        return new RuntimeFocusedControlSnapshot
        {
            SessionId = SessionId,
            AppId = AppId,
            ClientName = ClientName,
            Source = Source,
            NodeId = NodeId,
            Type = Type,
            Label = Label,
            AutomationId = AutomationId,
            Depth = Depth,
            ChildCount = ChildCount,
            FocusedAtUtc = FocusedAtUtc,
            ScreenshotArtifactPath = ScreenshotArtifactPath,
            ScreenshotStatus = ScreenshotStatus,
            LocalBounds = LocalBounds?.DeepClone() as JsonObject,
            AbsoluteBounds = AbsoluteBounds?.DeepClone() as JsonObject,
            NormalizedBounds = NormalizedBounds?.DeepClone() as JsonObject,
            Node = Node.DeepClone() as JsonObject ?? new JsonObject()
        };
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["sessionId"] = SessionId,
            ["appId"] = AppId,
            ["clientName"] = ClientName,
            ["source"] = Source,
            ["nodeId"] = NodeId,
            ["type"] = Type,
            ["label"] = Label,
            ["automationId"] = AutomationId,
            ["depth"] = Depth,
            ["childCount"] = ChildCount,
            ["focusedAtUtc"] = FocusedAtUtc,
            ["screenshotArtifactPath"] = ScreenshotArtifactPath,
            ["screenshotStatus"] = ScreenshotStatus,
            ["localBounds"] = LocalBounds?.DeepClone(),
            ["absoluteBounds"] = AbsoluteBounds?.DeepClone(),
            ["normalizedBounds"] = NormalizedBounds?.DeepClone(),
            ["node"] = Node.DeepClone()
        };
    }
}
