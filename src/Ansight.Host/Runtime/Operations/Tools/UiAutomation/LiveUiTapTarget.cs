using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

/// <summary>Observed context and a freshness check for otherwise indistinguishable tap targets.</summary>
internal static class LiveUiTapTarget
{
    private const int MaximumNearbyLabels = 6;
    private const int FingerprintCharacters = 20;

    public static LiveUiSelector RefreshTransientNodeId(JsonObject root, VisualTreeTypeRegistry registry,
        LiveUiBounds viewport, LiveUiSelector selector, string captureSource, JsonObject? arguments)
    {
        if (selector.NodeId is null || !LiveUiTreeCapture.IsTargetSource(captureSource)
            || arguments?["targetSource"]?.GetValue<string>() != captureSource
            || arguments?["targetFingerprint"] is not JsonValue scalar
            || !scalar.TryGetValue<string>(out var fingerprint) || string.IsNullOrWhiteSpace(fingerprint)
            || LiveUiNodeQuery.Find(root, selector, registry).Count != 0) return selector;

        var criteria = selector.ToJson();
        criteria.Remove("nodeId");
        var candidates = LiveUiNodeQuery.Find(root, LiveUiSelector.Parse(criteria), registry)
            .Where(match => LiveUiNodeQuery.IsEffectivelyVisible(match)
                && LiveUiNodeQuery.IsEffectivelyEnabled(match)
                && LiveUiNodeQuery.HasUsableBoundsInViewport(match, viewport)
                && MatchesFingerprint(root, match, viewport, fingerprint))
            .Take(2).ToArray();
        // Only the transient ID may change. Preserve every other selector constraint,
        // require the same provider, and refuse changed or ambiguous visible evidence.
        if (candidates.Length != 1 || LiveUiNodeQuery.ReadString(candidates[0].Node, "id") is not { Length: > 0 } currentId)
            return selector;
        criteria["nodeId"] = currentId;
        criteria["index"] = 0;
        return LiveUiSelector.Parse(criteria);
    }

    public static JsonArray NearbyText(JsonObject root, LiveUiNodeMatch match, LiveUiBounds? viewport)
        => new(NearbyLabels(root, match, viewport)
            .Select(label => (JsonNode)UiNodeProjection.FromMatch(label, UiProjectionOptions.Model, includeAncestors: false))
            .ToArray());

    public static string Fingerprint(JsonObject root, LiveUiNodeMatch match, LiveUiBounds viewport)
        => FullFingerprint(root, match, viewport)[..FingerprintCharacters];

    public static bool MatchesFingerprint(JsonObject root, LiveUiNodeMatch match, LiveUiBounds viewport, string fingerprint)
    {
        // Keep full hashes from older observations valid. New handles use an 80-bit
        // prefix to reduce model transcription errors; every character must still match.
        var full = FullFingerprint(root, match, viewport);
        return string.Equals(fingerprint, fingerprint.Length == FingerprintCharacters
            ? full[..FingerprintCharacters] : full, StringComparison.Ordinal);
    }

    private static string FullFingerprint(JsonObject root, LiveUiNodeMatch match, LiveUiBounds viewport)
    {
        // Exclude transient node IDs and capture timestamps. Include geometry and surrounding
        // labels so reordering identical titles or replacing the row invalidates its old hint.
        var signature = new JsonObject
        {
            ["viewport"] = viewport.ToJson(),
            ["target"] = Identity(match),
            ["ancestors"] = new JsonArray(match.Ancestors.Select(ancestor => (JsonNode)new JsonObject
            {
                ["automationId"] = LiveUiNodeQuery.ReadAutomationId(ancestor),
                ["text"] = LiveUiNodeQuery.ReadText(ancestor),
                ["type"] = match.TypeRegistry.Resolve(ancestor)
            }).ToArray()),
            ["nearbyText"] = new JsonArray(NearbyLabels(root, match, viewport)
                .Select(label => (JsonNode)Identity(label)).ToArray())
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToJsonString())));
    }

    private static JsonObject Identity(LiveUiNodeMatch match)
        => new()
        {
            ["automationId"] = LiveUiNodeQuery.ReadAutomationId(match.Node),
            ["text"] = LiveUiNodeQuery.ReadText(match.Node),
            ["role"] = LiveUiNodeQuery.ReadRole(match.Node, match.TypeRegistry),
            ["type"] = match.TypeRegistry.Resolve(match.Node),
            ["bounds"] = LiveUiNodeQuery.ReadBounds(match.Node)?.ToJson()
        };

    private static IReadOnlyList<LiveUiNodeMatch> NearbyLabels(
        JsonObject root, LiveUiNodeMatch match, LiveUiBounds? viewport)
    {
        var targetBounds = LiveUiNodeQuery.ReadBounds(match.Node);
        if (targetBounds is null || viewport is null || viewport.Height <= 0) return [];
        var text = LiveUiNodeQuery.ReadText(match.Node);
        return LiveUiNodeQuery.Enumerate(root, match.TypeRegistry)
            .Where(candidate => !ReferenceEquals(candidate.Node, match.Node)
                && LiveUiNodeQuery.ReadRole(candidate.Node, candidate.TypeRegistry) == "text"
                && !LiveUiNodeQuery.ReadBoolean(candidate.Node, "isPassword", false)
                && !LiveUiNodeQuery.ReadBoolean(candidate.Node, "password", false)
                && LiveUiNodeQuery.ReadText(candidate.Node) is { Length: > 0 } label
                && !string.Equals(label, text, StringComparison.OrdinalIgnoreCase)
                && LiveUiNodeQuery.IsEffectivelyVisible(candidate)
                && LiveUiNodeQuery.ReadBounds(candidate.Node) is { Width: > 0, Height: > 0 } bounds
                && FindLiveUiTool.ResolveViewportRelation(bounds, viewport) is "inside" or "partial"
                && bounds.X < targetBounds.X + targetBounds.Width
                && bounds.X + bounds.Width > targetBounds.X
                && Math.Abs(bounds.CenterY - targetBounds.CenterY) <= viewport.Height * 0.12)
            .OrderBy(candidate => Math.Abs(LiveUiNodeQuery.ReadBounds(candidate.Node)!.CenterY - targetBounds.CenterY))
            .Take(MaximumNearbyLabels)
            .OrderBy(candidate => LiveUiNodeQuery.ReadBounds(candidate.Node)!.Y)
            .ThenBy(candidate => LiveUiNodeQuery.ReadBounds(candidate.Node)!.X)
            .ToArray();
    }
}
