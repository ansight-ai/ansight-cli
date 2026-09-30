using System.Text.Json.Nodes;

namespace Ansight.Host.Diagnostics;

public static class SystemReportRenderer
{
    public static string Render(SystemReport report)
    {
        var lines = new List<string> { $"Ansight system report — {report.CapturedUtc:O}",
            $"Collection: {(report.IsComplete ? "complete" : "partial")}" };
        foreach (var section in report.Sections)
        {
            lines.Add($"\n{section.Key} [{section.Value.Status}]");
            if (section.Value.Message is not null) lines.Add(section.Value.Message);
            if (section.Value.Data is not null) RenderNode(section.Value.Data, "  ", lines);
        }
        if (report.Findings.Count > 0) { lines.Add("\nFindings"); lines.AddRange(report.Findings.Select(finding => "  - " + finding)); }
        return string.Join(Environment.NewLine, lines);
    }

    private static void RenderNode(JsonNode node, string indent, List<string> lines)
    {
        if (node is JsonObject obj)
            foreach (var item in obj)
            {
                if (item.Value is JsonArray or JsonObject) { lines.Add(indent + item.Key + ":"); RenderNode(item.Value, indent + "  ", lines); }
                else lines.Add(indent + item.Key + ": " + (item.Value?.ToString() ?? "unknown"));
            }
        else if (node is JsonArray array)
            foreach (var item in array)
            {
                if (item is JsonObject or JsonArray) { lines.Add(indent + "-"); RenderNode(item, indent + "  ", lines); }
                else lines.Add(indent + "- " + item);
            }
        else lines.Add(indent + node);
    }
}
