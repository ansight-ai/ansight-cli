using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>A reviewable Maestro flow derived from one recorded session interval.</summary>
public sealed record MaestroFlowExtraction(
    string SuggestedName,
    string Source,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics);

public static class MaestroFlowExtractor
{
    public static MaestroFlowExtraction Extract(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var extracted = TimelineTaskExtractor.Extract(
            snapshot,
            startUtc,
            endUtc,
            title,
            includeCoordinateFallbackTaps: true);
        var diagnostics = new List<string>(extracted.Diagnostics);
        var commands = new StringBuilder();
        var generatedCount = 0;

        foreach (var action in extracted.Actions)
        {
            var command = RenderAction(action, diagnostics);
            if (command is null)
            {
                continue;
            }

            commands.Append(command);
            generatedCount++;
        }

        if (generatedCount == 0)
        {
            diagnostics.Add("No Maestro actions could be generated from the selected period.");
        }

        var source = new StringBuilder()
            .Append("# Draft from Ansight session ").AppendLine(Comment(snapshot.SessionId))
            .Append("# Selected period: ")
            .Append(startUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .Append(" to ")
            .AppendLine(endUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .Append("# ").AppendLine(Comment(title))
            .Append("appId: ").AppendLine(Quote(snapshot.AppId))
            .AppendLine("---")
            .AppendLine("- launchApp")
            .Append(commands)
            .AppendLine()
            .AppendLine("# Review before running:")
            .AppendLine("# - Confirm the starting state and add an outcome assertion.");
        if (diagnostics.Any(static diagnostic => diagnostic.Contains("viewport coordinate", StringComparison.Ordinal)))
        {
            source.AppendLine("# - Verify coordinate taps on the target device.");
        }

        if (extracted.Actions.Count > generatedCount)
        {
            source.AppendLine("# - Review interactions that could not be converted.");
        }

        return new MaestroFlowExtraction(
            extracted.SuggestedName,
            source.ToString(),
            generatedCount,
            diagnostics);
    }

    private static string? RenderAction(
        TimelineExtractedAction action,
        ICollection<string> diagnostics)
    {
        if (action.Kind == "swipe")
        {
            if (action.StartNormalizedX is not { } startX
                || action.StartNormalizedY is not { } startY
                || action.EndNormalizedX is not { } endX
                || action.EndNormalizedY is not { } endY)
            {
                diagnostics.Add($"Swipe at {action.CapturedAtUtc:O} has incomplete coordinates.");
                return null;
            }

            return $"- swipe:\n    start: {Percent(startX)}, {Percent(startY)}\n"
                   + $"    end: {Percent(endX)}, {Percent(endY)}\n"
                   + $"    duration: {Math.Clamp(action.DurationMilliseconds, 50, 2_000).ToString(CultureInfo.InvariantCulture)}\n";
        }

        var selector = RenderSelector(action.Selector);
        if (selector is null)
        {
            if (action.Kind == "tap"
                && action.StartNormalizedX is { } tapX
                && action.StartNormalizedY is { } tapY)
            {
                diagnostics.Add($"Tap at {action.CapturedAtUtc:O} uses a viewport coordinate; confirm it on the target device.");
                return "- tapOn:\n    point: " + Quote($"{Percent(tapX)},{Percent(tapY)}") + "\n";
            }

            diagnostics.Add($"{action.Description} at {action.CapturedAtUtc:O} has no Maestro-compatible ID or text selector.");
            return null;
        }

        if (action.Kind == "tap")
        {
            return "- tapOn:\n" + selector;
        }

        if (action.Kind == "input")
        {
            diagnostics.Add($"Text replacement at {action.CapturedAtUtc:O} clears up to 100 characters; check the field's initial value.");
            return "- tapOn:\n" + selector
                   + "- eraseText: 100\n"
                   + (string.IsNullOrEmpty(action.InputValue)
                       ? string.Empty
                       : "- inputText: " + Quote(action.InputValue) + "\n");
        }

        diagnostics.Add($"Unsupported action {action.Kind} at {action.CapturedAtUtc:O} was omitted.");
        return null;
    }

    private static string? RenderSelector(JsonObject? selector)
    {
        var id = ReadString(selector, "automationId");
        if (id is not null)
        {
            return "    id: " + Quote(ExactPattern(id)) + "\n";
        }

        var text = ReadString(selector, "text");
        return text is null ? null : "    text: " + Quote(ExactPattern(text)) + "\n";
    }

    private static string? ReadString(JsonObject? value, string key)
        => value?[key] is JsonValue node && node.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static string ExactPattern(string value) => "^" + Regex.Escape(value) + "$";

    private static string Percent(double value)
        => Math.Round(Math.Clamp(value, 0, 1) * 100, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string Comment(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ');
}
