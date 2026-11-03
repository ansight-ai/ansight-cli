using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.AppGraphs;

/// <summary>Separates observed target identity from reusable navigation behavior.</summary>
public static class AppGraphActionIdentity
{
    public static string NavigationKind(string toolName, string automationId, JsonObject? selector,
        string? declaredKind = null)
    {
        if (declaredKind is "back" or "pop" or "dismiss" or "tab" or "menu") return declaredKind;
        if (toolName == "ansight_back_ui") return "back";
        if (toolName == "ansight_dismiss_keyboard") return "dismiss";
        foreach (var value in new[] { automationId, Text(selector, "action") })
        {
            var words = Regex.Replace(value, "([a-z])([A-Z])", "$1 $2")
                .Split(['-', '_', '.', ' ', ':'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(word => word.Equals("back", StringComparison.OrdinalIgnoreCase))) return "back";
            if (words.Any(word => word.Equals("pop", StringComparison.OrdinalIgnoreCase))) return "pop";
            if (words.Any(word => word.Equals("dismiss", StringComparison.OrdinalIgnoreCase))) return "dismiss";
            if (toolName == "ansight_tap_ui")
            {
                if (words.Contains("tab", StringComparer.OrdinalIgnoreCase)) return "tab";
                if (words.Any(word => word.Equals("menu", StringComparison.OrdinalIgnoreCase)
                                      || word.Equals("flyout", StringComparison.OrdinalIgnoreCase)
                                      || word.Equals("drawer", StringComparison.OrdinalIgnoreCase))
                    && words.Any(word => word.Equals("toggle", StringComparison.OrdinalIgnoreCase)
                                         || word.Equals("open", StringComparison.OrdinalIgnoreCase)
                                         || word.Equals("close", StringComparison.OrdinalIgnoreCase))) return "menu";
            }
        }
        var text = Text(selector, "text").Trim();
        if (text.Equals("Back", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Go back", StringComparison.OrdinalIgnoreCase)) return "back";
        if (text.Equals("Dismiss", StringComparison.OrdinalIgnoreCase)) return "dismiss";
        if (toolName == "ansight_tap_ui" && Text(selector, "role") is "tab" or "tabitem") return "tab";
        return string.Empty;
    }

    public static bool HasObservedSelector(JsonObject? selector)
        => new[] { "automationId", "text", "nodeId" }.Any(key => !string.IsNullOrWhiteSpace(Text(selector, key)));

    public static JsonObject NormalizeSelector(JsonObject? selector)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "automationId", "text", "role", "type", "ancestorAutomationId", "nodeId" })
        {
            var value = Text(selector, key);
            if (!string.IsNullOrWhiteSpace(value)) result[key] = value;
        }
        // An omitted index selects the first match. Preserve later matches as distinct observations.
        if (selector?["index"] is JsonValue indexValue && indexValue.TryGetValue<int>(out var index) && index > 0)
            result["index"] = index;
        return result;
    }

    private static string Text(JsonObject? value, string key)
        => value?[key] is JsonValue text && text.TryGetValue<string>(out var result) ? result : string.Empty;
}
