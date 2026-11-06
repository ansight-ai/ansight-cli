using System.Text.Json.Nodes;

namespace Ansight.Cli.Input;

internal static class CliSelectorArguments
{
    internal static IReadOnlyList<string> SelectorFlagOptions { get; } = ["contains", "case-sensitive"];

    internal static IReadOnlyList<string> SelectorValueOptions { get; } =
    [
        "automation-id",
        "text",
        "role",
        "ancestor",
        "index"
    ];

    internal static void AddSelector(JsonObject target, CliArguments arguments)
    {
        AddOptionalString(target, "automationId", arguments.GetOption("automation-id"));
        AddOptionalString(target, "text", arguments.GetOption("text"));
        AddOptionalString(target, "role", arguments.GetOption("role"));
        AddOptionalString(target, "ancestorAutomationId", arguments.GetOption("ancestor"));

        if (arguments.GetOption("index") is not null)
        {
            target["index"] = arguments.GetIntOption("index", 0, 0, int.MaxValue);
        }

        if (arguments.HasFlag("contains"))
        {
            target["exact"] = false;
        }

        if (arguments.HasFlag("case-sensitive"))
        {
            target["caseSensitive"] = true;
        }
    }

    internal static void AddOptionalString(JsonObject target, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[propertyName] = value.Trim();
        }
    }
}
