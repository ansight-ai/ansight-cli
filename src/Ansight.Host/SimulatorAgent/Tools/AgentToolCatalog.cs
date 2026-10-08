using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Tools;

/// <summary>Projects the model's callable tools without restricting the repository-task runtime.</summary>
internal sealed class AgentToolCatalog
{
    public const string LoadToolName = "ansight_load_tools";
    private static readonly string[] coreToolNames =
    ["ansight_get_live_visual_tree", "ansight_list_tasks", "ansight_run_task", "complete_instruction"];
    private static readonly Dictionary<string, string[]> bundleToolNames = new(StringComparer.Ordinal)
    {
        ["manual-ui"] = ["ansight_find_ui", "ansight_wait_for_ui", "ansight_assert_ui", "ansight_tap_ui",
            "ansight_type_text", "ansight_type_secret", "ansight_scroll_ui", "ansight_back_ui", AgentUiBatch.ToolName],
        ["gestures"] = ["ansight_swipe_ui", "ansight_pinch_ui", "ansight_run_ui_sequence"],
        ["app-tools"] = ["ansight_list_app_tools", "ansight_call_app_tool"],
        ["evidence"] = ["ansight_take_screenshot", "ansight_get_live_navigation_structure"],
        ["lifecycle"] = ["ansight_list_host_devices", "ansight_start_device", "ansight_launch_app", "ansight_terminate_app"],
        ["tasks"] = ["ansight_describe_module"]
    };
    private readonly Dictionary<string, JsonObject> definitions;
    private readonly HashSet<string> loadedToolNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> loadedBundles = new(StringComparer.Ordinal);

    public AgentToolCatalog(JsonArray fullTools)
    {
        FullTools = fullTools.DeepClone().AsArray();
        definitions = fullTools.OfType<JsonObject>()
            .Where(tool => tool["name"] is JsonValue)
            .ToDictionary(tool => tool["name"]!.GetValue<string>(), tool => tool, StringComparer.Ordinal);
        InitialTools = new JsonArray();
        foreach (var name in coreToolNames)
        {
            if (definitions.TryGetValue(name, out var definition))
            {
                InitialTools.Add(definition.DeepClone());
                loadedToolNames.Add(name);
            }
        }
        InitialTools.Add(BuildLoaderDefinition());
        loadedToolNames.Add(LoadToolName);

        var deferredNames = bundleToolNames.Values.SelectMany(names => names).ToHashSet(StringComparer.Ordinal);
        InitialAdditionalTools = new JsonArray();
        foreach (var name in definitions.Keys.Order(StringComparer.Ordinal))
        {
            if (!loadedToolNames.Contains(name) && !deferredNames.Contains(name))
            {
                // Typed task shortcuts and their declaration schema vary by instruction. Keep
                // them after the fixed tool/instruction prefix, including any future extension.
                InitialAdditionalTools.Add(definitions[name].DeepClone());
                loadedToolNames.Add(name);
            }
        }
    }

    public JsonArray FullTools { get; }
    public JsonArray InitialTools { get; }
    public JsonArray InitialAdditionalTools { get; }

    public bool IsDeferred(string toolName)
        => definitions.ContainsKey(toolName) && !loadedToolNames.Contains(toolName);

    public ToolLoadResult Load(JsonObject arguments)
    {
        var bundle = arguments["bundle"] is JsonValue value && value.TryGetValue<string>(out var name)
            ? name : null;
        if (bundle is null || !bundleToolNames.ContainsKey(bundle))
        {
            const string message = "Choose one bundle: manual-ui, gestures, app-tools, evidence, lifecycle, or tasks.";
            return new(new ToolCallResult(true,
                new JsonObject { ["isError"] = true, ["message"] = message }.ToJsonString(), message), [], string.Empty);
        }

        // Gestures often need discovery of their target. Load that dependency in the same turn.
        var bundles = bundle == "gestures" ? new[] { "manual-ui", "gestures" } : [bundle];
        var additionalTools = new JsonArray();
        var guidance = new List<string>();
        foreach (var selectedBundle in bundles)
        {
            if (!loadedBundles.Add(selectedBundle))
            {
                continue;
            }
            var before = additionalTools.Count;
            foreach (var toolName in bundleToolNames[selectedBundle])
            {
                if (definitions.TryGetValue(toolName, out var definition) && loadedToolNames.Add(toolName))
                {
                    additionalTools.Add(definition.DeepClone());
                }
            }
            if (additionalTools.Count > before)
            {
                guidance.Add(ReadGuidance(selectedBundle));
            }
        }
        var callable = bundles.SelectMany(selected => bundleToolNames[selected])
            .Where(loadedToolNames.Contains).ToArray();
        var messageText = additionalTools.Count > 0
            ? $"Loaded {additionalTools.Count} tool(s). No app action was performed."
            : callable.Length > 0 ? "These tools are already loaded." : "This capability is unavailable for the selected app and instruction.";
        var output = new JsonObject
        {
            ["isError"] = callable.Length == 0,
            ["bundle"] = bundle,
            ["tools"] = new JsonArray(callable.Select(tool => (JsonNode?)JsonValue.Create(tool)).ToArray()),
            ["message"] = messageText
        }.ToJsonString();
        return new(new ToolCallResult(callable.Length == 0, output, messageText),
            additionalTools, string.Join("\n\n", guidance));
    }

    public static JsonObject CreateAdditionalToolsInput(JsonArray tools)
        => new() { ["type"] = "additional_tools", ["role"] = "developer", ["tools"] = tools.DeepClone() };

    public static string ReadGuidance(string bundle)
        => EmbeddedTextResource.ReadSection("SimulatorAgent/Prompts/tool-capabilities.md", bundle);

    public static string FullGuidance()
        => string.Join("\n\n", new[] { "manual-ui", "gestures", "app-tools", "evidence" }.Select(ReadGuidance));

    private static JsonObject BuildLoaderDefinition()
        => new()
        {
            ["type"] = "function", ["name"] = LoadToolName,
            ["description"] = "Load capabilities when current tools are insufficient. manual-ui: find, wait, assert, tap, type, scroll, back, ordered tool batches; gestures: swipe, pinch, recorded gestures; app-tools: domain tool discovery/calls; evidence: screenshots/navigation; lifecycle: app/device lifecycle; tasks: task details. Loading performs no app action.",
            ["strict"] = true,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["bundle"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(bundleToolNames.Keys.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray())
                    }
                },
                ["required"] = new JsonArray("bundle"), ["additionalProperties"] = false
            }
        };
}
