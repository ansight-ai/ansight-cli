using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiToolSchemas
{
    public static Dictionary<string, ToolSchema> SelectorProperties(
        Dictionary<string, ToolSchema>? properties = null)
    {
        properties ??= [];
        properties["nodeId"] = ToolSchema.String("Exact ephemeral visual-tree node id.", nullable: true);
        properties["automationId"] = ToolSchema.String("Stable automation, accessibility, test, or native id.", nullable: true);
        properties["text"] = ToolSchema.String("Visible or accessibility text on the target node. All supplied selector fields must match this same node.", nullable: true);
        properties["role"] = ToolSchema.String("Platform-neutral semantic role, such as button or textbox.", nullable: true);
        properties["type"] = ToolSchema.String("Platform or framework type of the target node itself, not its page or container.", nullable: true);
        properties["ancestorAutomationId"] = ToolSchema.String("Automation id required on an ancestor; use this to scope a target to a page or container.", nullable: true);
        properties["action"] = ToolSchema.String("Semantic action the node must advertise.", nullable: true);
        properties["visible"] = ToolSchema.Boolean("Optional visibility filter.", nullable: true);
        properties["enabled"] = ToolSchema.Boolean("Optional enabled-state filter.", nullable: true);
        properties["exact"] = ToolSchema.Boolean("Use exact string matching. Defaults to true.", nullable: true);
        properties["caseSensitive"] = ToolSchema.Boolean("Use case-sensitive string matching. Defaults to false.", nullable: true);
        properties["index"] = ToolSchema.Integer("Zero-based match index when a selector matches multiple nodes. Defaults to zero.", nullable: true);
        return properties;
    }

    public static int ReadInteger(JsonObject? arguments, string propertyName, int fallback)
        => arguments?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : fallback;

    public static double ReadDouble(JsonObject? arguments, string propertyName, double fallback)
        => TryReadDouble(arguments, propertyName, out var result) ? result : fallback;

    public static bool TryReadDouble(JsonObject? arguments, string propertyName, out double result)
    {
        if (arguments?[propertyName] is not JsonValue value)
        {
            result = default;
            return false;
        }

        if (value.TryGetValue<double>(out result))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return true;
        }

        if (value.TryGetValue<long>(out var longInteger))
        {
            result = longInteger;
            return true;
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            result = (double)decimalValue;
            return true;
        }

        result = default;
        return false;
    }

    public static string? ReadString(JsonObject? arguments, string propertyName)
        => arguments?[propertyName] is JsonValue value
           && value.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    public static bool TryReadRawString(
        JsonObject? arguments,
        string propertyName,
        out string value)
    {
        if (arguments?[propertyName] is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
