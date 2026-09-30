using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiMauiContext
{
    private const double MauiHorizontalGestureInset = 0.05;
    private const double MauiTopGestureInset = 0.08;
    private const double MauiBottomGestureLimit = 0.92;
    private const double MauiTabbedBottomGestureLimit = 0.80;

    public static bool HasActivePageContext(JsonObject payload)
        => IsMauiPayload(payload)
           && (string.Equals(
                   LiveUiNodeQuery.ReadString(payload, "rootScope"),
                   MauiVisualTreeRootScope.CurrentPage,
                   StringComparison.Ordinal)
               || payload["currentPage"] is JsonObject
               || payload["flagBits"] is JsonObject flagBits
               && (flagBits["currentPage"] is not null || flagBits["activePage"] is not null));

    public static bool IsInActivePage(LiveUiNodeMatch match, JsonObject payload)
    {
        if (!HasActivePageContext(payload))
        {
            return false;
        }

        if (string.Equals(
                LiveUiNodeQuery.ReadString(payload, "rootScope"),
                MauiVisualTreeRootScope.CurrentPage,
                StringComparison.Ordinal))
        {
            return true;
        }

        var currentPageId = payload["currentPage"] is JsonObject currentPage
            ? LiveUiNodeQuery.ReadString(currentPage, "id")
            : null;
        return IsActivePageNode(match.Node, currentPageId, match.TypeRegistry)
               || match.Ancestors.Any(ancestor =>
                   IsActivePageNode(ancestor, currentPageId, match.TypeRegistry));
    }

    public static LiveUiBounds ResolveGestureBounds(
        JsonObject payload,
        JsonObject root,
        VisualTreeTypeRegistry typeRegistry,
        LiveUiNodeMatch? target,
        LiveUiBounds targetBounds,
        LiveUiBounds viewport)
    {
        var targetIntersection = LiveUiBounds.Intersection(targetBounds, viewport);
        var targetRole = target is null
            ? null
            : LiveUiNodeQuery.ReadRole(target.Node, target.TypeRegistry);
        var targetCanHostGesture = targetIntersection is not null
                                   && (string.Equals(targetRole, "scrollview", StringComparison.Ordinal)
                                       || targetIntersection.Width >= viewport.Width * 0.25
                                       && targetIntersection.Height >= viewport.Height * 0.25);
        var gestureSurface = targetCanHostGesture ? targetIntersection! : viewport;
        if (!IsMauiPayload(payload))
        {
            return gestureSurface;
        }

        var hasTabbedPage = LiveUiNodeQuery.Enumerate(root, typeRegistry)
            .Select(match => typeRegistry.Resolve(match.Node))
            .Any(type => type?.Contains("TabbedPage", StringComparison.OrdinalIgnoreCase) == true);
        var minimumX = Math.Max(
            gestureSurface.X,
            viewport.X + (viewport.Width * MauiHorizontalGestureInset));
        var maximumX = Math.Min(
            gestureSurface.X + gestureSurface.Width,
            viewport.X + (viewport.Width * (1 - MauiHorizontalGestureInset)));
        var minimumY = Math.Max(
            gestureSurface.Y,
            viewport.Y + (viewport.Height * MauiTopGestureInset));
        var maximumY = Math.Min(
            gestureSurface.Y + gestureSurface.Height,
            viewport.Y + (viewport.Height * (hasTabbedPage
                ? MauiTabbedBottomGestureLimit
                : MauiBottomGestureLimit)));
        return maximumX > minimumX && maximumY > minimumY
            ? new LiveUiBounds(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY)
            : gestureSurface;
    }

    public static string? DescribeUnexpectedPageChange(
        JsonObject beforePayload,
        VisualTreeTypeRegistry beforeTypeRegistry,
        JsonObject afterPayload,
        VisualTreeTypeRegistry afterTypeRegistry)
    {
        if (!IsMauiPayload(beforePayload) || !IsMauiPayload(afterPayload))
        {
            return null;
        }

        var beforePage = ReadCurrentPage(beforePayload, beforeTypeRegistry);
        var afterPage = ReadCurrentPage(afterPayload, afterTypeRegistry);
        if (beforePage is null || afterPage is null || beforePage.IsSamePage(afterPage))
        {
            return null;
        }

        return $"The scroll gesture was delivered, but the active .NET MAUI page changed unexpectedly from {beforePage.Describe()} to {afterPage.Describe()}. The gesture likely intersected navigation chrome; re-observe the current page before continuing.";
    }

    private static bool IsMauiPayload(JsonObject payload)
        => LiveUiNodeQuery.ReadString(payload, "format")
               ?.Contains(".maui.", StringComparison.OrdinalIgnoreCase) == true
           || string.Equals(
               LiveUiNodeQuery.ReadString(payload, "source"),
               "maui",
               StringComparison.OrdinalIgnoreCase);

    private static bool IsActivePageNode(
        JsonObject node,
        string? currentPageId,
        VisualTreeTypeRegistry typeRegistry)
        => !string.IsNullOrWhiteSpace(currentPageId)
           && string.Equals(
               LiveUiNodeQuery.ReadString(node, "id"),
               currentPageId,
               StringComparison.Ordinal)
           || LiveUiNodeQuery.ReadBoolean(node, "currentPage", fallback: false, typeRegistry)
           || LiveUiNodeQuery.ReadBoolean(node, "activePage", fallback: false, typeRegistry);

    private static LiveUiPageIdentity? ReadCurrentPage(
        JsonObject payload,
        VisualTreeTypeRegistry typeRegistry)
    {
        if (payload["currentPage"] is JsonObject currentPage)
        {
            return LiveUiPageIdentity.Create(currentPage, typeRegistry);
        }

        if (payload["root"] is not JsonObject root)
        {
            return null;
        }

        var currentMatch = LiveUiNodeQuery.Enumerate(root, typeRegistry)
            .FirstOrDefault(match =>
                LiveUiNodeQuery.ReadBoolean(match.Node, "currentPage", fallback: false, typeRegistry));
        return currentMatch is null
            ? null
            : LiveUiPageIdentity.Create(currentMatch.Node, typeRegistry);
    }

    private sealed record LiveUiPageIdentity(
        string? Id,
        string? AutomationId,
        string? Text,
        string? Type)
    {
        public static LiveUiPageIdentity Create(
            JsonObject node,
            VisualTreeTypeRegistry typeRegistry)
            => new(
                LiveUiNodeQuery.ReadString(node, "id"),
                LiveUiNodeQuery.ReadAutomationId(node),
                LiveUiNodeQuery.ReadText(node),
                typeRegistry.Resolve(node));

        public bool IsSamePage(LiveUiPageIdentity other)
        {
            if (!string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(other.Id))
            {
                return string.Equals(Id, other.Id, StringComparison.Ordinal);
            }

            if (!string.IsNullOrWhiteSpace(AutomationId)
                && !string.IsNullOrWhiteSpace(other.AutomationId))
            {
                return string.Equals(AutomationId, other.AutomationId, StringComparison.Ordinal);
            }

            return EqualsWhenBothPresent(Type, other.Type)
                   && (Text is null
                       || other.Text is null
                       || string.Equals(Text, other.Text, StringComparison.Ordinal));
        }

        public string Describe()
            => $"'{Text ?? AutomationId ?? Type ?? Id ?? "unknown page"}' ({Id ?? "no id"})";

        private static bool EqualsWhenBothPresent(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left)
               && !string.IsNullOrWhiteSpace(right)
               && string.Equals(left, right, StringComparison.Ordinal);
    }
}
