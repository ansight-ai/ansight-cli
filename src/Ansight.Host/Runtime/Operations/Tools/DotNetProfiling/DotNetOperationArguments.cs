using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal static class DotNetOperationArguments
{
    public static Dictionary<string, Ansight.Tools.ToolSchema> CaptureSelectionProperties()
    {
        return new Dictionary<string, Ansight.Tools.ToolSchema>
        {
            ["captureId"] = Ansight.Tools.ToolSchema.String("Required .NET trace capture id."),
            ["startMilliseconds"] = Ansight.Tools.ToolSchema.Number("Optional inclusive start offset in milliseconds from trace start.", nullable: true),
            ["endMilliseconds"] = Ansight.Tools.ToolSchema.Number("Optional inclusive end offset in milliseconds from trace start.", nullable: true),
            ["processId"] = Ansight.Tools.ToolSchema.Integer("Optional process id filter.", nullable: true),
            ["threadId"] = Ansight.Tools.ToolSchema.Integer("Optional managed or operating-system thread id filter.", nullable: true)
        };
    }

    public static bool TryReadSelection(
        JsonObject? arguments,
        out string captureId,
        out TraceSelectionWindow selection,
        out string? error)
    {
        captureId = arguments?["captureId"]?.GetValue<string>()?.Trim() ?? string.Empty;
        selection = TraceSelectionWindow.EntireTrace;
        error = null;
        if (string.IsNullOrWhiteSpace(captureId))
        {
            error = "captureId is required.";
            return false;
        }

        if (!TryReadOptionalDouble(arguments, "startMilliseconds", out var start, out error)
            || !TryReadOptionalDouble(arguments, "endMilliseconds", out var end, out error)
            || !ArgumentReader.TryReadOptionalIntegerArgument(arguments, "processId", out var processId, out error)
            || !ArgumentReader.TryReadOptionalIntegerArgument(arguments, "threadId", out var threadId, out error))
        {
            return false;
        }

        if (start is < 0 || end is < 0)
        {
            error = "Trace selection offsets cannot be negative.";
            return false;
        }

        if (start is not null && end is not null && end < start)
        {
            error = "endMilliseconds must be greater than or equal to startMilliseconds.";
            return false;
        }

        selection = new TraceSelectionWindow(start, end, processId, threadId);
        return true;
    }

    public static IReadOnlyList<string> ReadStringArray(JsonObject? arguments, string propertyName)
    {
        if (arguments?[propertyName] is not JsonArray values)
        {
            return [];
        }

        return values
            .Select(value => value?.GetValue<string>())
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
    }

    private static bool TryReadOptionalDouble(
        JsonObject? arguments,
        string propertyName,
        out double? value,
        out string? error)
    {
        value = null;
        error = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<double>(out var parsed)
                || double.TryParse(
                    jsonValue.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed)))
        {
            value = parsed;
            return true;
        }

        error = $"'{propertyName}' must be a number.";
        return false;
    }
}
