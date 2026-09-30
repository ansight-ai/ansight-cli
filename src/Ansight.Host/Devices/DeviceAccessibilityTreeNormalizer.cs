using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ansight.Host.Devices;

internal static partial class DeviceAccessibilityTreeNormalizer
{
    private const int VisibleFlag = 1;
    private const int EnabledFlag = 2;
    private const string AndroidUiEvidenceOverlayType =
        "ai.ansight.runtime.AndroidUiEvidence$OverlaySurface";
    private const string SdkOverlayAutomationId = "ansight.overlay.surface";

    public static JsonObject NormalizeIos(
        string source,
        double viewportWidth,
        double viewportHeight,
        int maxNodes,
        int maxDepth)
        => Normalize(
            source,
            "ios",
            viewportWidth,
            viewportHeight,
            maxNodes,
            maxDepth,
            ParseIos);

    public static JsonObject NormalizeIosSimulator(
        JsonNode source,
        int maxNodes,
        int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is JsonObject nativeSnapshot
            && nativeSnapshot["elements"] is JsonArray nativeElements)
        {
            return NormalizeIosSimulatorNative(
                nativeSnapshot,
                nativeElements,
                maxNodes,
                maxDepth);
        }

        var sourceRoots = source switch
        {
            JsonArray array => array.OfType<JsonObject>().ToArray(),
            JsonObject singleRoot => [singleRoot],
            _ => []
        };
        if (sourceRoots.Length == 0)
        {
            throw new InvalidDataException("CoreSimulator accessibility returned no root elements.");
        }

        var rawNodes = sourceRoots.SelectMany(EnumerateAxeNodes).ToArray();
        var frames = rawNodes
            .Select(ReadAxeFrame)
            .Where(static frame => frame.Width > 0 && frame.Height > 0)
            .ToArray();
        if (frames.Length == 0)
        {
            throw new InvalidDataException(
                "CoreSimulator accessibility returned no usable element frames.");
        }

        var rootFrame = sourceRoots
            .Select(ReadAxeFrame)
            .FirstOrDefault(static frame => frame.Width > 0 && frame.Height > 0);
        var viewportWidth = rootFrame.Width > 0
            ? rootFrame.Width
            : frames.Max(static frame => frame.X + frame.Width);
        var viewportHeight = rootFrame.Height > 0
            ? rootFrame.Height
            : frames.Max(static frame => frame.Y + frame.Height);
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0
            || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            throw new InvalidDataException(
                "CoreSimulator accessibility returned an invalid viewport.");
        }

        var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var types = new List<string>();
        var root = new JsonObject
        {
            ["typeId"] = ResolveTypeId("Application", typeIds, types),
            ["role"] = "application",
            ["flags"] = VisibleFlag | EnabledFlag,
            ["bounds"] = Bounds(0, 0, 1, 1),
            ["children"] = new JsonArray()
        };
        var state = new AxeNormalizationState(
            Math.Clamp(maxNodes, 1, 2_000),
            Math.Clamp(maxDepth, 1, 64),
            typeIds,
            types,
            viewportWidth,
            viewportHeight)
        {
            ReturnedNodeCount = 1
        };
        foreach (var sourceRoot in sourceRoots)
        {
            AppendAxeDescendants(sourceRoot, root, semanticDepth: 0, state);
            if (state.ReturnedNodeCount >= state.MaxNodes)
            {
                state.Truncated = true;
                break;
            }
        }

        return new JsonObject
        {
            ["format"] = "ansight.device-accessibility.compact.v2",
            ["platform"] = "ios",
            ["source"] = "core-simulator-ax-service",
            ["capturedAtUtc"] = DateTimeOffset.UtcNow,
            ["keyboardVisible"] = rawNodes.Any(IsIosKeyboardElement),
            ["coordinateSpace"] = Bounds(0, 0, 1, 1),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = VisibleFlag,
                ["enabled"] = EnabledFlag
            },
            ["types"] = new JsonArray(types.Select(static type => JsonValue.Create(type)).ToArray()),
            ["root"] = root,
            ["rawNodeCount"] = rawNodes.Length,
            ["nodeCount"] = state.ReturnedNodeCount,
            ["truncated"] = state.Truncated
        };
    }

    private static JsonObject NormalizeIosSimulatorNative(
        JsonObject source,
        JsonArray elements,
        int maxNodes,
        int maxDepth)
    {
        var roots = new JsonArray();
        var ancestors = new List<(int Depth, JsonObject Node)>();
        foreach (var element in elements.OfType<JsonObject>())
        {
            var role = ReadAxeString(element, "role") ?? "AXElement";
            var type = role.StartsWith("AX", StringComparison.Ordinal)
                ? role[2..]
                : role;
            var node = new JsonObject
            {
                ["type"] = type,
                ["role"] = role,
                ["AXUniqueId"] = ReadAxeString(element, "id"),
                ["AXLabel"] = ReadAxeString(element, "label"),
                ["AXValue"] = ReadAxeString(element, "value"),
                ["enabled"] = ReadAxeBoolean(element, "enabled") ?? true,
                ["selected"] = ReadAxeBoolean(element, "selected"),
                ["frame"] = new JsonObject
                {
                    ["x"] = ReadAxeNumber(element, "x"),
                    ["y"] = ReadAxeNumber(element, "y"),
                    ["width"] = ReadAxeNumber(element, "width"),
                    ["height"] = ReadAxeNumber(element, "height")
                },
                ["custom_actions"] = element["actionNames"]?.DeepClone() ?? new JsonArray(),
                ["children"] = new JsonArray()
            };
            var depth = Math.Max(0, (int)ReadAxeNumber(element, "depth"));
            while (ancestors.Count > 0 && ancestors[^1].Depth >= depth)
            {
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            if (ancestors.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                ((JsonArray)ancestors[^1].Node["children"]!).Add(node);
            }
            ancestors.Add((depth, node));
        }

        if (roots.Count == 0)
        {
            throw new InvalidDataException(
                "The resident CoreSimulator accessibility service returned no meaningful elements.");
        }

        var normalized = NormalizeIosSimulator(roots, maxNodes, maxDepth);
        normalized["rawNodeCount"] = source["rawNodeCount"]?.DeepClone()
                                     ?? elements.Count;
        normalized["nativeNodeCount"] = elements.Count;
        normalized["provider"] = "resident-axptranslator";
        return normalized;
    }

    public static JsonObject NormalizeAndroid(
        string source,
        double viewportWidth,
        double viewportHeight,
        int maxNodes,
        int maxDepth)
        => Normalize(
            source,
            "android",
            viewportWidth,
            viewportHeight,
            maxNodes,
            maxDepth,
            ParseAndroid);

    private static JsonObject Normalize(
        string source,
        string platform,
        double viewportWidth,
        double viewportHeight,
        int maxNodes,
        int maxDepth,
        Func<XElement, double, double, ParsedAccessibilityNode> parser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0
            || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportWidth),
                "A positive accessibility viewport is required.");
        }

        var document = XDocument.Parse(source, LoadOptions.None);
        var sourceRoot = document.Root
                         ?? throw new InvalidDataException("The device accessibility source has no root element.");
        var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var types = new List<string>();
        var root = new JsonObject
        {
            ["typeId"] = ResolveTypeId("Application", typeIds, types),
            ["role"] = "application",
            ["flags"] = VisibleFlag | EnabledFlag,
            ["bounds"] = Bounds(0, 0, 1, 1),
            ["children"] = new JsonArray()
        };
        var state = new NormalizationState(
            Math.Clamp(maxNodes, 1, 2_000),
            Math.Clamp(maxDepth, 1, 64),
            typeIds,
            types,
            parser,
            viewportWidth,
            viewportHeight)
        {
            ReturnedNodeCount = 1
        };

        AppendDescendants(sourceRoot, root, semanticDepth: 0, state, includeElement: true);
        return new JsonObject
        {
            ["format"] = "ansight.device-accessibility.compact.v2",
            ["platform"] = platform,
            ["source"] = "device-accessibility",
            ["capturedAtUtc"] = DateTimeOffset.UtcNow,
            ["keyboardVisible"] = string.Equals(platform, "ios", StringComparison.Ordinal)
                ? sourceRoot.DescendantsAndSelf().Any(IsIosKeyboardElement)
                : sourceRoot.DescendantsAndSelf().Any(IsAndroidKeyboardElement),
            ["coordinateSpace"] = Bounds(0, 0, 1, 1),
            ["flagBits"] = new JsonObject
            {
                ["visible"] = VisibleFlag,
                ["enabled"] = EnabledFlag
            },
            ["types"] = new JsonArray(types.Select(static type => JsonValue.Create(type)).ToArray()),
            ["root"] = root,
            ["rawNodeCount"] = sourceRoot.DescendantsAndSelf().Count(),
            ["nodeCount"] = state.ReturnedNodeCount,
            ["truncated"] = state.Truncated
        };
    }

    private static void AppendDescendants(
        XElement element,
        JsonObject semanticParent,
        int semanticDepth,
        NormalizationState state,
        bool includeElement)
    {
        if (IsSdkInjectedAndroidOverlay(element))
        {
            return;
        }

        if (state.ReturnedNodeCount >= state.MaxNodes)
        {
            state.Truncated = true;
            return;
        }

        var parsed = state.Parser(element, state.ViewportWidth, state.ViewportHeight);
        var parentForChildren = semanticParent;
        var childDepth = semanticDepth;
        if (includeElement && parsed.IsMeaningful && semanticDepth < state.MaxDepth)
        {
            var node = ToJson(parsed, state.TypeIds, state.Types);
            ((JsonArray)semanticParent["children"]!).Add(node);
            state.ReturnedNodeCount++;
            parentForChildren = node;
            childDepth++;
        }

        foreach (var child in element.Elements())
        {
            AppendDescendants(child, parentForChildren, childDepth, state, includeElement: true);
            if (state.ReturnedNodeCount >= state.MaxNodes)
            {
                state.Truncated = child.ElementsAfterSelf().Any() || state.Truncated;
                break;
            }
        }
    }

    private static bool IsSdkInjectedAndroidOverlay(XElement element)
        => string.Equals(
               Attribute(element, "class"),
               AndroidUiEvidenceOverlayType,
               StringComparison.Ordinal)
           || string.Equals(
               Attribute(element, "resource-id"),
               SdkOverlayAutomationId,
               StringComparison.Ordinal)
           || string.Equals(
               Attribute(element, "content-desc"),
               SdkOverlayAutomationId,
               StringComparison.Ordinal);

    private static void AppendAxeDescendants(
        JsonObject element,
        JsonObject semanticParent,
        int semanticDepth,
        AxeNormalizationState state)
    {
        if (state.ReturnedNodeCount >= state.MaxNodes)
        {
            state.Truncated = true;
            return;
        }

        var parsed = ParseAxe(element, state.ViewportWidth, state.ViewportHeight);
        var parentForChildren = semanticParent;
        var childDepth = semanticDepth;
        if (parsed.IsMeaningful && semanticDepth < state.MaxDepth)
        {
            var node = ToJson(parsed, state.TypeIds, state.Types);
            ((JsonArray)semanticParent["children"]!).Add(node);
            state.ReturnedNodeCount++;
            parentForChildren = node;
            childDepth++;
        }

        foreach (var child in ReadAxeChildren(element))
        {
            AppendAxeDescendants(child, parentForChildren, childDepth, state);
            if (state.ReturnedNodeCount >= state.MaxNodes)
            {
                state.Truncated = true;
                break;
            }
        }
    }

    private static JsonObject ToJson(
        ParsedAccessibilityNode parsed,
        Dictionary<string, int> typeIds,
        List<string> types)
    {
        var node = new JsonObject
        {
            ["typeId"] = ResolveTypeId(parsed.Type, typeIds, types),
            ["role"] = parsed.Role,
            ["flags"] = (parsed.Visible ? VisibleFlag : 0) | (parsed.Enabled ? EnabledFlag : 0),
            ["bounds"] = Bounds(parsed.X, parsed.Y, parsed.Width, parsed.Height),
            ["children"] = new JsonArray()
        };
        AddString(node, "automationId", parsed.AutomationId);
        AddString(node, "text", parsed.Text);
        AddString(node, "label", parsed.Label);
        AddString(node, "value", parsed.Value);
        if (parsed.Actions.Count > 0)
        {
            node["supportedActions"] = new JsonArray(
                parsed.Actions.Select(static action => JsonValue.Create(action)).ToArray());
        }

        if (parsed.Selected.HasValue)
        {
            node["selected"] = parsed.Selected.Value;
        }
        if (parsed.Checked.HasValue)
        {
            node["checked"] = parsed.Checked.Value;
        }
        if (parsed.Focused.HasValue)
        {
            node["focused"] = parsed.Focused.Value;
        }

        return node;
    }

    private static ParsedAccessibilityNode ParseIos(
        XElement element,
        double viewportWidth,
        double viewportHeight)
    {
        var type = Attribute(element, "type") ?? element.Name.LocalName;
        var role = IosRole(type);
        var visible = BooleanAttribute(element, "visible", fallback: true);
        var enabled = BooleanAttribute(element, "enabled", fallback: true);
        var x = NumberAttribute(element, "x");
        var y = NumberAttribute(element, "y");
        var width = NumberAttribute(element, "width");
        var height = NumberAttribute(element, "height");
        var actions = IosActions(role, type, enabled);
        var label = Attribute(element, "label");
        var name = Attribute(element, "identifier") ?? Attribute(element, "name");
        var value = Attribute(element, "value");
        var isRoot = type.Contains("Application", StringComparison.OrdinalIgnoreCase)
                     || type.Contains("Window", StringComparison.OrdinalIgnoreCase);
        var meaningful = visible
                         && width > 0
                         && height > 0
                         && (isRoot
                             || actions.Count > 0
                             || BooleanAttribute(element, "accessible", fallback: false)
                             || FirstNonEmpty(label, name, value) is not null);
        return new ParsedAccessibilityNode(
            type,
            role,
            name,
            label ?? value,
            label,
            value,
            Normalize(x, viewportWidth),
            Normalize(y, viewportHeight),
            NormalizeSize(width, viewportWidth),
            NormalizeSize(height, viewportHeight),
            visible,
            enabled,
            meaningful,
            actions,
            NullableBooleanAttribute(element, "selected"),
            NullableBooleanAttribute(element, "checked"),
            NullableBooleanAttribute(element, "focused"));
    }

    private static ParsedAccessibilityNode ParseAndroid(
        XElement element,
        double viewportWidth,
        double viewportHeight)
    {
        var type = Attribute(element, "class") ?? element.Name.LocalName;
        var clickable = BooleanAttribute(element, "clickable", fallback: false);
        var scrollable = BooleanAttribute(element, "scrollable", fallback: false);
        var checkable = BooleanAttribute(element, "checkable", fallback: false);
        var role = AndroidRole(type, checkable, scrollable);
        var visible = !string.Equals(Attribute(element, "visible-to-user"), "false", StringComparison.OrdinalIgnoreCase);
        var enabled = BooleanAttribute(element, "enabled", fallback: true);
        var bounds = ParseAndroidBounds(Attribute(element, "bounds"));
        var actions = AndroidActions(role, clickable, scrollable, enabled);
        var text = Attribute(element, "text");
        var label = Attribute(element, "content-desc");
        var automationId = Attribute(element, "resource-id");
        var meaningful = visible
                         && bounds.Width > 0
                         && bounds.Height > 0
                         && (actions.Count > 0
                             || checkable
                             || FirstNonEmpty(text, label, automationId) is not null);
        return new ParsedAccessibilityNode(
            type,
            role,
            automationId,
            text ?? label,
            label,
            text,
            Normalize(bounds.X, viewportWidth),
            Normalize(bounds.Y, viewportHeight),
            NormalizeSize(bounds.Width, viewportWidth),
            NormalizeSize(bounds.Height, viewportHeight),
            visible,
            enabled,
            meaningful,
            actions,
            NullableBooleanAttribute(element, "selected"),
            NullableBooleanAttribute(element, "checked"),
            NullableBooleanAttribute(element, "focused"));
    }

    private static ParsedAccessibilityNode ParseAxe(
        JsonObject element,
        double viewportWidth,
        double viewportHeight)
    {
        var type = ReadAxeString(element, "type")
                   ?? ReadAxeString(element, "role")
                   ?? "Element";
        var sourceRole = ReadAxeString(element, "role")
                         ?? ReadAxeString(element, "role_description")
                         ?? string.Empty;
        var role = AxeRole(type, sourceRole);
        var frame = ReadAxeFrame(element);
        var enabled = ReadAxeBoolean(element, "enabled") ?? true;
        var visible = ReadAxeBoolean(element, "visible")
                      ?? !(ReadAxeBoolean(element, "hidden") ?? false);
        var label = ReadAxeString(element, "AXLabel")
                    ?? ReadAxeString(element, "title");
        var value = ReadAxeScalar(element, "AXValue");
        var automationId = ReadAxeString(element, "AXUniqueId")
                           ?? ReadAxeString(element, "AXIdentifier");
        var customActions = ReadAxeCustomActions(element);
        var actions = AxeActions(role, enabled, customActions.Count > 0);
        var isRoot = role == "application" || type.Contains("Window", StringComparison.OrdinalIgnoreCase);
        var meaningful = visible
                         && frame.Width > 0
                         && frame.Height > 0
                         && (isRoot
                             || actions.Count > 0
                             || FirstNonEmpty(label, value, automationId) is not null);
        return new ParsedAccessibilityNode(
            type,
            role,
            automationId,
            label ?? value,
            label,
            value,
            Normalize(frame.X, viewportWidth),
            Normalize(frame.Y, viewportHeight),
            NormalizeSize(frame.Width, viewportWidth),
            NormalizeSize(frame.Height, viewportHeight),
            visible,
            enabled,
            meaningful,
            actions,
            ReadAxeBoolean(element, "selected"),
            ReadAxeBoolean(element, "checked"),
            ReadAxeBoolean(element, "focused"));
    }

    private static IReadOnlyList<string> IosActions(string role, string type, bool enabled)
    {
        if (!enabled)
        {
            return [];
        }

        if (role == "textbox")
        {
            return ["tap", "typeText", "focus"];
        }
        if (role == "scrollview")
        {
            return ["scroll", "swipe"];
        }
        if (role is "button" or "switch" or "checkbox" or "radio" or "link" or "tab"
            || type.Contains("Key", StringComparison.OrdinalIgnoreCase))
        {
            return ["tap"];
        }

        return [];
    }

    private static IReadOnlyList<string> AndroidActions(
        string role,
        bool clickable,
        bool scrollable,
        bool enabled)
    {
        if (!enabled)
        {
            return [];
        }

        var actions = new List<string>();
        if (clickable || role is "button" or "switch" or "checkbox" or "radio")
        {
            actions.Add("tap");
        }
        if (role == "textbox")
        {
            actions.AddRange(["tap", "typeText", "focus"]);
        }
        if (scrollable)
        {
            actions.AddRange(["scroll", "swipe"]);
        }
        return actions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> AxeActions(
        string role,
        bool enabled,
        bool hasCustomActions)
    {
        if (!enabled)
        {
            return [];
        }

        if (role == "textbox")
        {
            return ["tap", "typeText", "focus"];
        }
        if (role == "scrollview")
        {
            return ["scroll", "swipe"];
        }
        if (role == "slider")
        {
            return ["tap", "adjust"];
        }
        if (role is "button" or "switch" or "checkbox" or "radio" or "link" or "tab" or "cell"
            || hasCustomActions)
        {
            return ["tap"];
        }

        return [];
    }

    private static string IosRole(string type)
    {
        var normalized = type.ToLowerInvariant();
        if (normalized.Contains("textfield", StringComparison.Ordinal)
            || normalized.Contains("textview", StringComparison.Ordinal)
            || normalized.Contains("searchfield", StringComparison.Ordinal))
        {
            return "textbox";
        }
        if (normalized.Contains("scrollview", StringComparison.Ordinal)
            || normalized.Contains("collectionview", StringComparison.Ordinal)
            || normalized.Contains("table", StringComparison.Ordinal))
        {
            return "scrollview";
        }
        if (normalized.Contains("button", StringComparison.Ordinal)) return "button";
        if (normalized.Contains("switch", StringComparison.Ordinal)) return "switch";
        if (normalized.Contains("checkbox", StringComparison.Ordinal)) return "checkbox";
        if (normalized.Contains("link", StringComparison.Ordinal)) return "link";
        if (normalized.Contains("tab", StringComparison.Ordinal)) return "tab";
        if (normalized.Contains("cell", StringComparison.Ordinal)) return "cell";
        if (normalized.Contains("image", StringComparison.Ordinal)) return "image";
        if (normalized.Contains("statictext", StringComparison.Ordinal)) return "text";
        if (normalized.Contains("application", StringComparison.Ordinal)) return "application";
        return "view";
    }

    private static string AndroidRole(string type, bool checkable, bool scrollable)
    {
        var normalized = type.ToLowerInvariant();
        if (normalized.Contains("edittext", StringComparison.Ordinal)) return "textbox";
        if (scrollable) return "scrollview";
        if (normalized.Contains("button", StringComparison.Ordinal)) return "button";
        if (normalized.Contains("switch", StringComparison.Ordinal)) return "switch";
        if (normalized.Contains("radiobutton", StringComparison.Ordinal)) return "radio";
        if (checkable || normalized.Contains("checkbox", StringComparison.Ordinal)) return "checkbox";
        if (normalized.Contains("image", StringComparison.Ordinal)) return "image";
        if (normalized.Contains("text", StringComparison.Ordinal)) return "text";
        return "view";
    }

    private static bool IsIosKeyboardElement(XElement element)
        => IsIosKeyboardType(Attribute(element, "type") ?? element.Name.LocalName);

    private static bool IsIosKeyboardElement(JsonObject element)
        => IsIosKeyboardType(
            ReadAxeString(element, "type")
            ?? ReadAxeString(element, "role")
            ?? ReadAxeString(element, "role_description"));

    private static bool IsIosKeyboardType(string? value)
        => value?.Contains("keyboard", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsAndroidKeyboardElement(XElement element)
    {
        var packageName = Attribute(element, "package");
        var resourceId = Attribute(element, "resource-id");
        var type = Attribute(element, "class");
        return ContainsAndroidKeyboardMarker(packageName)
               || ContainsAndroidKeyboardMarker(resourceId)
               || type?.Contains("inputmethodservice.Keyboard", StringComparison.OrdinalIgnoreCase) == true
               || type?.Contains("KeyboardView", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsAndroidKeyboardMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("inputmethod", StringComparison.OrdinalIgnoreCase)
               || value.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
               || value.Contains("honeyboard", StringComparison.OrdinalIgnoreCase)
               || value.Contains("swiftkey", StringComparison.OrdinalIgnoreCase)
               || value.Contains("touchtype", StringComparison.OrdinalIgnoreCase);
    }

    private static string AxeRole(string type, string role)
    {
        var normalized = $"{type} {role}".ToLowerInvariant();
        if (normalized.Contains("textfield", StringComparison.Ordinal)
            || normalized.Contains("text field", StringComparison.Ordinal)
            || normalized.Contains("textarea", StringComparison.Ordinal)
            || normalized.Contains("text area", StringComparison.Ordinal)
            || normalized.Contains("searchfield", StringComparison.Ordinal)
            || normalized.Contains("search field", StringComparison.Ordinal)
            || normalized.Contains("securetext", StringComparison.Ordinal))
        {
            return "textbox";
        }
        if (normalized.Contains("scroll", StringComparison.Ordinal)
            || normalized.Contains("collection", StringComparison.Ordinal)
            || normalized.Contains("table", StringComparison.Ordinal))
        {
            return "scrollview";
        }
        if (normalized.Contains("button", StringComparison.Ordinal)) return "button";
        if (normalized.Contains("switch", StringComparison.Ordinal)
            || normalized.Contains("toggle", StringComparison.Ordinal)) return "switch";
        if (normalized.Contains("checkbox", StringComparison.Ordinal)) return "checkbox";
        if (normalized.Contains("radio", StringComparison.Ordinal)) return "radio";
        if (normalized.Contains("link", StringComparison.Ordinal)) return "link";
        if (normalized.Contains("tab", StringComparison.Ordinal)) return "tab";
        if (normalized.Contains("slider", StringComparison.Ordinal)) return "slider";
        if (normalized.Contains("cell", StringComparison.Ordinal)) return "cell";
        if (normalized.Contains("menuitem", StringComparison.Ordinal)
            || normalized.Contains("menu item", StringComparison.Ordinal)) return "button";
        if (normalized.Contains("image", StringComparison.Ordinal)) return "image";
        if (normalized.Contains("statictext", StringComparison.Ordinal)
            || normalized.Contains("static text", StringComparison.Ordinal)) return "text";
        if (normalized.Contains("application", StringComparison.Ordinal)) return "application";
        return "view";
    }

    private static IEnumerable<JsonObject> EnumerateAxeNodes(JsonObject root)
    {
        yield return root;
        foreach (var child in ReadAxeChildren(root))
        {
            foreach (var descendant in EnumerateAxeNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<JsonObject> ReadAxeChildren(JsonObject element)
        => element["children"] is JsonArray children
            ? children.OfType<JsonObject>()
            : [];

    private static (double X, double Y, double Width, double Height) ReadAxeFrame(JsonObject element)
    {
        if (element["frame"] is JsonObject frame)
        {
            return (
                ReadAxeNumber(frame, "x"),
                ReadAxeNumber(frame, "y"),
                ReadAxeNumber(frame, "width"),
                ReadAxeNumber(frame, "height"));
        }

        var serializedFrame = ReadAxeString(element, "AXFrame");
        var match = serializedFrame is null ? Match.Empty : AxeFramePattern().Match(serializedFrame);
        if (!match.Success)
        {
            return default;
        }

        return (
            ParseInvariantDouble(match.Groups[1].Value),
            ParseInvariantDouble(match.Groups[2].Value),
            ParseInvariantDouble(match.Groups[3].Value),
            ParseInvariantDouble(match.Groups[4].Value));
    }

    private static IReadOnlyList<string> ReadAxeCustomActions(JsonObject element)
    {
        if (element["custom_actions"] is not JsonArray actions)
        {
            return [];
        }

        return actions
            .Select(action => action switch
            {
                JsonValue => ReadAxeScalar(action),
                JsonObject value => ReadAxeString(value, "name")
                                    ?? ReadAxeString(value, "label"),
                _ => null
            })
            .Where(static action => !string.IsNullOrWhiteSpace(action))
            .Select(static action => action!)
            .ToArray();
    }

    private static string? ReadAxeString(JsonObject element, string propertyName)
        => ReadAxeScalar(element[propertyName]);

    private static string? ReadAxeScalar(JsonObject element, string propertyName)
        => ReadAxeScalar(element[propertyName]);

    private static string? ReadAxeScalar(JsonNode? value)
    {
        if (value is not JsonValue scalar)
        {
            return null;
        }
        if (scalar.TryGetValue<string>(out var text))
        {
            return FirstNonEmpty(text);
        }
        if (scalar.TryGetValue<double>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }
        if (scalar.TryGetValue<bool>(out var boolean))
        {
            return boolean ? "true" : "false";
        }

        return null;
    }

    private static bool? ReadAxeBoolean(JsonObject element, string propertyName)
    {
        if (element[propertyName] is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }
        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean)
            ? boolean
            : null;
    }

    private static double ReadAxeNumber(JsonObject element, string propertyName)
    {
        if (element[propertyName] is not JsonValue value)
        {
            return 0;
        }
        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }
        return value.TryGetValue<string>(out var text)
               && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }

    private static double ParseInvariantDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static (double X, double Y, double Width, double Height) ParseAndroidBounds(string? value)
    {
        var match = value is null ? Match.Empty : AndroidBoundsPattern().Match(value);
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var top)
            || !double.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var right)
            || !double.TryParse(match.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bottom))
        {
            return default;
        }

        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static int ResolveTypeId(
        string type,
        Dictionary<string, int> typeIds,
        List<string> types)
    {
        if (typeIds.TryGetValue(type, out var existing))
        {
            return existing;
        }

        var created = types.Count;
        typeIds[type] = created;
        types.Add(type);
        return created;
    }

    private static JsonObject Bounds(double x, double y, double width, double height)
        => new()
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height
        };

    private static void AddString(JsonObject node, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            node[propertyName] = value.Trim();
        }
    }

    private static string? Attribute(XElement element, string name)
        => FirstNonEmpty(element.Attribute(name)?.Value);

    private static double NumberAttribute(XElement element, string name)
        => double.TryParse(
            element.Attribute(name)?.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;

    private static bool BooleanAttribute(XElement element, string name, bool fallback)
        => NullableBooleanAttribute(element, name) ?? fallback;

    private static bool? NullableBooleanAttribute(XElement element, string name)
        => bool.TryParse(element.Attribute(name)?.Value, out var value) ? value : null;

    private static double Normalize(double value, double viewportSize)
        => Math.Clamp(value / viewportSize, 0, 1);

    private static double NormalizeSize(double value, double viewportSize)
        => Math.Clamp(value / viewportSize, 0, 1);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    [GeneratedRegex(@"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidBoundsPattern();

    [GeneratedRegex(@"\{\{\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\}\s*,\s*\{\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex AxeFramePattern();

    private sealed class NormalizationState(
        int maxNodes,
        int maxDepth,
        Dictionary<string, int> typeIds,
        List<string> types,
        Func<XElement, double, double, ParsedAccessibilityNode> parser,
        double viewportWidth,
        double viewportHeight)
    {
        public int MaxNodes { get; } = maxNodes;
        public int MaxDepth { get; } = maxDepth;
        public Dictionary<string, int> TypeIds { get; } = typeIds;
        public List<string> Types { get; } = types;
        public Func<XElement, double, double, ParsedAccessibilityNode> Parser { get; } = parser;
        public double ViewportWidth { get; } = viewportWidth;
        public double ViewportHeight { get; } = viewportHeight;
        public int ReturnedNodeCount { get; set; }
        public bool Truncated { get; set; }
    }

    private sealed class AxeNormalizationState(
        int maxNodes,
        int maxDepth,
        Dictionary<string, int> typeIds,
        List<string> types,
        double viewportWidth,
        double viewportHeight)
    {
        public int MaxNodes { get; } = maxNodes;
        public int MaxDepth { get; } = maxDepth;
        public Dictionary<string, int> TypeIds { get; } = typeIds;
        public List<string> Types { get; } = types;
        public double ViewportWidth { get; } = viewportWidth;
        public double ViewportHeight { get; } = viewportHeight;
        public int ReturnedNodeCount { get; set; }
        public bool Truncated { get; set; }
    }

    private sealed record ParsedAccessibilityNode(
        string Type,
        string Role,
        string? AutomationId,
        string? Text,
        string? Label,
        string? Value,
        double X,
        double Y,
        double Width,
        double Height,
        bool Visible,
        bool Enabled,
        bool IsMeaningful,
        IReadOnlyList<string> Actions,
        bool? Selected,
        bool? Checked,
        bool? Focused);
}
