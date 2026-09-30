using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Cli.Commands.Licenses;

internal static class LicensesCommands
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CliCommandHelp.IsRequested(arguments))
        {
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));
        }

        var action = arguments.Positionals.Count >= 2
            ? arguments.Positionals[1].ToLowerInvariant()
            : "list";
        return Task.FromResult(action switch
        {
            "list" => List(arguments, output),
            "export" => Export(arguments, output),
            _ => throw new CliUsageException(
                $"Unknown licenses action '{action}'. Expected list or export.")
        });
    }

    private static int List(CliArguments arguments, CliOutput output)
    {
        arguments.EnsurePositionalCount(
            arguments.Positionals.Count == 1 ? 1 : 2,
            "ansight licenses list [--json]");
        var catalog = ThirdPartySoftwareCatalog.Load();
        output.Write(catalog, () => RenderList(catalog));
        return CliExitCodes.Success;
    }

    private static int Export(CliArguments arguments, CliOutput output)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight licenses export <output.md|output.json> [--format <markdown|json>] [--force]");
        var path = Path.GetFullPath(arguments.RequirePositional(2, "output path"));
        if (File.Exists(path) && !arguments.HasFlag("force"))
        {
            throw new CliUsageException(
                $"Output file '{path}' already exists. Use --force to replace it.");
        }

        var format = ResolveFormat(path, arguments.GetOption("format"));
        var catalog = ThirdPartySoftwareCatalog.Load();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            format == "json"
                ? JsonSerializer.Serialize(catalog, jsonOptions) + Environment.NewLine
                : RenderMarkdown(catalog));
        output.Write(
            new LicenseExportOutput(
                "ansight.third-party-software-export/v1",
                path,
                format,
                catalog.Software.Count,
                catalog.Warnings),
            () => $"Exported {catalog.Software.Count} software attribution(s) to {path}.");
        return CliExitCodes.Success;
    }

    private static string ResolveFormat(string path, string? requestedFormat)
    {
        var format = requestedFormat?.Trim().ToLowerInvariant();
        if (format is "md")
        {
            format = "markdown";
        }

        if (format is null or "")
        {
            format = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".json" => "json",
                ".md" or ".markdown" => "markdown",
                _ => throw new CliUsageException(
                    "Use an .md or .json output extension, or set --format markdown|json.")
            };
        }

        return format is "json" or "markdown"
            ? format
            : throw new CliUsageException("--format must be markdown or json.");
    }

    internal static string RenderMarkdown(ThirdPartySoftwareCatalogOutput catalog)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Ansight software attributions");
        builder.AppendLine();
        builder.AppendLine(
            "This inventory distinguishes software distributed with Ansight from external tools "
            + "that Ansight can invoke when installed separately.");
        builder.AppendLine(
            "It is an attribution inventory; release packaging must still preserve any complete "
            + "license and notice texts required by the upstream distributions.");
        builder.AppendLine();
        builder.AppendLine("| Software | Version | License | Distribution | Source | Usage | Project |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var entry in catalog.Software)
        {
            var project = string.IsNullOrWhiteSpace(entry.ProjectUrl)
                ? string.Empty
                : $"[link]({entry.ProjectUrl})";
            var license = string.IsNullOrWhiteSpace(entry.LicenseUrl)
                ? Escape(entry.License)
                : $"[{Escape(entry.License)}]({entry.LicenseUrl})";
            builder.AppendLine(
                $"| {Escape(entry.Name)} | {Escape(entry.Version ?? string.Empty)} | {license} | "
                + $"{Escape(entry.Distribution)} | {Escape(entry.Source)} | "
                + $"{Escape(entry.Usage)} | {project} |");
        }

        if (catalog.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Inventory notes");
            builder.AppendLine();
            foreach (var warning in catalog.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static string RenderList(ThirdPartySoftwareCatalogOutput catalog)
    {
        var lines = catalog.Software.Select(entry =>
            $"{entry.Name}\t{entry.Version ?? "-"}\t{entry.License}\t{entry.Distribution}").ToList();
        lines.AddRange(catalog.Warnings.Select(static warning => $"Warning: {warning}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string BuildHelp()
    {
        return """
               List and export software attribution and license information

               Usage:
                 ansight licenses list [--json]
                 ansight licenses export <output.md|output.json> [options]

               Commands:
                 list       Show bundled NuGet/native software and optional external tools
                 export     Write the inventory as Markdown or versioned JSON

               Export options:
                 --format <markdown|json>  Override the format inferred from the extension
                 --force                   Replace an existing output file

               Aliases: licence, licences, license, notice, notices, attribution, attributions

               Bundled software is resolved from the maintained single-file catalog and enriched
               from local runtime/NuGet metadata when available. External tools are marked
               external-not-redistributed so their use is not mistaken for redistribution.
               """;
    }
}
