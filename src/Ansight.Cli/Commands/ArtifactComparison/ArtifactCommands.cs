using System.Globalization;

namespace Ansight.Cli.Commands.ArtifactComparison;

internal static class ArtifactCommands
{
    internal const string Help = """
        Usage:
          ansight artifact list --session-id <id> [--search <text>] [--provider <id>] [--format <extension>]
            [--from <ISO-time>] [--to <ISO-time>] [--min-bytes <bytes>] [--max-bytes <bytes>]
            [--sort captured-at|newest|name|size] [--offset <n>] [--limit <1-1000>] [--json]
          ansight artifact diff --artifact <session-id>/<artifact-id> --artifact <session-id>/<artifact-id> ...
            [--path <member-path>] [--mode auto|text] [--array-key <property-or-column>]
            [--ignore-whitespace] [--offset <n>] [--limit <1-1000>] [--fail-on-change] [--json]

        References identify captured snapshots, not provider export definitions. Copy them from list.
        Argument order defines the journey: N captures produce N-1 adjacent hops, across any sessions.
        List pagination is by capture; diff pagination is by change within each file. All hops remain visible.
        Differences succeed by default. --fail-on-change returns 12 for changes; incomplete comparisons return 1.
        Auto supports JSON, XML, SQLite, ZIP, text, Markdown and keyed CSV/TSV (--array-key).
        Limits: 32 captures, 32 MiB/file, 100,000 database rows, 20,000 changes/file. Limits are reported explicitly.
        """;

    public static async Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments)) return CliCommandHelp.Write(output, Help);
        arguments.EnsurePositionalCount(2, Help);
        var action = arguments.RequirePositional(1, "artifact action");
        if (action is not ("list" or "diff")) throw new CliUsageException("Artifact action must be list or diff.");
        await using var lease = await CliRuntimeLease.CreateAsync(CliRuntime.ResolveOptions(arguments), start: false, cancellationToken).ConfigureAwait(false);
        try
        {
            if (action == "list")
            {
                var request = new ArtifactListRequest(arguments.RequireOption("session-id"), arguments.GetOption("search"), arguments.GetOption("provider"), arguments.GetOption("format"),
                    Timestamp(arguments, "from"), Timestamp(arguments, "to"), Bytes(arguments, "min-bytes"), Bytes(arguments, "max-bytes"),
                    arguments.GetIntOption("offset", 0, 0, int.MaxValue), arguments.GetIntOption("limit", 200, 1, 1000), arguments.GetOption("sort") ?? "captured-at");
                var result = await lease.Runtime.ArtifactComparisons.ListAsync(request, cancellationToken).ConfigureAwait(false);
                output.Write(result, () => FormatList(result));
                return CliExitCodes.Success;
            }
            var references = ReadReferences(arguments);
            var comparison = await lease.Runtime.ArtifactComparisons.CompareAsync(new(references, arguments.GetOption("path"), arguments.GetOption("mode") ?? "auto",
                arguments.GetOption("array-key"), arguments.HasFlag("ignore-whitespace"), arguments.GetIntOption("offset", 0, 0, int.MaxValue), arguments.GetIntOption("limit", 500, 1, 1000)), cancellationToken).ConfigureAwait(false);
            output.Write(comparison, () => FormatDiff(comparison));
            if (!comparison.IsComplete) return CliExitCodes.Failure;
            return arguments.HasFlag("fail-on-change") && comparison.Hops.Any(hop => hop.Status == "changed") ? CliExitCodes.ArtifactDifferenceDetected : CliExitCodes.Success;
        }
        catch (ArgumentException exception) { throw new CliUsageException(exception.Message); }
    }

    internal static IReadOnlyList<ArtifactReference> ReadReferences(CliArguments arguments)
    {
        // GetOptions omits valueless occurrences; reject those instead of silently dropping a hop.
        for (var index = 0; index < arguments.OriginalArguments.Count; index++)
            if (arguments.OriginalArguments[index].Equals("--artifact", StringComparison.OrdinalIgnoreCase) &&
                (index + 1 == arguments.OriginalArguments.Count || arguments.OriginalArguments[index + 1].StartsWith("--", StringComparison.Ordinal)))
                throw new CliUsageException("Every --artifact requires <session-id>/<artifact-id>.");
        try
        {
            var references = arguments.GetOptions("artifact").Select(ArtifactReference.Parse).ToArray();
            if (references.Length is < 2 or > 32) throw new CliUsageException("Diff requires 2–32 --artifact <session-id>/<artifact-id> arguments.");
            return references;
        }
        catch (ArgumentException exception) { throw new CliUsageException(exception.Message); }
    }

    internal static string FormatList(ArtifactListResult result)
    {
        var lines = new List<string> { "REFERENCE\tCAPTURED AT\tNAME\tPROVIDER / LOGICAL ID\tFORMAT\tFILES\tBYTES\tCOMPLETE" };
        lines.AddRange(result.Items.Select(item => $"{item.Reference}\t{item.CapturedAtUtc:O}\t{item.Name}\t{item.Provider}/{item.LogicalId}\t{string.Join(',', item.Formats)}\t{item.FileCount}\t{item.SizeBytes}\t{!item.Truncated}"));
        lines.Add($"{result.Items.Count} of {result.Total} matching artifacts." + (result.NextOffset is { } next ? $" Next: --offset {next}" : ""));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatDiff(ArtifactDiffResult result)
    {
        var lines = new List<string> { $"{result.Artifacts.Count} captures · {result.Hops.Count} hops · {(result.IsComplete ? "complete" : "INCOMPLETE")}", result.Summary };
        foreach (var hop in result.Hops)
        {
            lines.Add($"\nHop {hop.Index}: {hop.Before.Reference} → {hop.After.Reference}");
            lines.Add($"{hop.Before.CapturedAtUtc:O} → {hop.After.CapturedAtUtc:O} · {hop.Status}");
            foreach (var file in hop.Files)
            {
                lines.Add($"  {file.Path} [{file.Format}] {file.Status} · {file.TotalChanges} changes");
                if (file.Message is not null) lines.Add($"  {file.Message}");
                foreach (var change in file.Changes)
                {
                    lines.Add($"  {change.Kind}: {change.Path}");
                    if (change.Before is not null) lines.Add("    - " + change.Before.Replace("\n", "\n    - "));
                    if (change.After is not null) lines.Add("    + " + change.After.Replace("\n", "\n    + "));
                }
                if (file.NextOffset is { } next) lines.Add($"  More changes: --offset {next}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static DateTimeOffset? Timestamp(CliArguments arguments, string name)
        => arguments.GetOption(name) is not { } value ? null : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : throw new CliUsageException($"--{name} must be an ISO timestamp.");
    private static long? Bytes(CliArguments arguments, string name)
        => arguments.GetOption(name) is not { } value ? null : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new CliUsageException($"--{name} must be a nonnegative byte count.");
}
