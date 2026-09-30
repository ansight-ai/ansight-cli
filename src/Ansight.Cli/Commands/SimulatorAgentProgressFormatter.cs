using Ansight.Host;
using System.Globalization;

namespace Ansight.Cli.Commands;

internal static class SimulatorAgentProgressFormatter
{
    public static string Format(SimulatorAgentProgress progress)
    {
        var prefix = $"[{progress.InstructionIndex}/{progress.InstructionCount}] [{progress.Stage}]";
        if (progress.TaskDiscovery is not { } discovery)
        {
            return $"{prefix} {progress.Message}";
        }

        var lines = new List<string> { $"{prefix} {progress.Message}" };
        if (discovery.AvailableTaskCount is { } availableCount)
        {
            lines.Add($"  Available tasks ({availableCount}): {string.Join(", ", discovery.AvailableTaskIds)}");
        }
        foreach (var candidate in discovery.Candidates)
        {
            var score = candidate.Score?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
            var coverage = candidate.Coverage?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
            lines.Add($"  {candidate.TaskId}: score={score}, coverage={coverage}; {string.Join(", ", candidate.Reasons)}");
        }
        if (discovery.DiagnosticsTruncated)
        {
            lines.Add("  Inventory and candidate diagnostics are truncated to the first 200 tasks.");
        }
        if (!string.IsNullOrWhiteSpace(discovery.Message))
        {
            lines.Add($"  {discovery.Message}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
