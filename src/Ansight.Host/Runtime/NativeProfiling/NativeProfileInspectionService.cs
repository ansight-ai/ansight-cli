using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed partial class NativeProfileInspectionService
{
    internal const string AnalyzerVersion = "native-profile-inspection-v1";
    private const int MaximumSqlLength = 32 * 1024;
    private readonly NativeProfileCaptureStore captureStore;
    private readonly INativeProfilingProcessRunner processRunner;
    private readonly string? traceProcessorPathOverride;

    public NativeProfileInspectionService(
        NativeProfileCaptureStore captureStore,
        INativeProfilingProcessRunner processRunner,
        string? traceProcessorPathOverride = null)
    {
        this.captureStore = captureStore ?? throw new ArgumentNullException(nameof(captureStore));
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.traceProcessorPathOverride = string.IsNullOrWhiteSpace(traceProcessorPathOverride)
            ? null
            : traceProcessorPathOverride.Trim();
    }

    public NativeProfileArtifactLocation? GetArtifact(string captureId, string? artifactKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        var normalizedCaptureId = captureId.Trim();
        if (!captureStore.TryLoadManifest(normalizedCaptureId, out var manifest)
            || manifest is null)
        {
            return null;
        }

        var artifact = string.IsNullOrWhiteSpace(artifactKind)
            ? manifest.Artifacts.FirstOrDefault(static candidate => candidate.Authoritative)
            : manifest.Artifacts.FirstOrDefault(candidate => string.Equals(
                candidate.Kind,
                artifactKind.Trim(),
                StringComparison.Ordinal));
        if (artifact is null
            || !captureStore.TryResolveArtifactPath(manifest, artifact.Kind, out var path)
            || path is null)
        {
            return null;
        }

        return new NativeProfileArtifactLocation(
            manifest.CaptureId,
            manifest.Platform,
            path,
            artifact);
    }

    public async Task<NativeProfileCaptureInspection> InspectAsync(
        string captureId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        var normalizedCaptureId = captureId.Trim();
        if (!captureStore.TryLoadManifest(normalizedCaptureId, out var manifest)
            || manifest is null)
        {
            throw new KeyNotFoundException($"Native profile capture '{normalizedCaptureId}' was not found.");
        }

        var artifacts = manifest.Artifacts
            .Select(artifact => CreateLocation(manifest, artifact))
            .Where(static location => location is not null)
            .Cast<NativeProfileArtifactLocation>()
            .ToArray();
        var authoritativeArtifact = artifacts.FirstOrDefault(static artifact => artifact.Artifact.Authoritative);
        var warnings = manifest.Warnings.ToList();
        var resolvedArtifactKinds = artifacts
            .Select(static artifact => artifact.Artifact.Kind)
            .ToHashSet(StringComparer.Ordinal);
        warnings.AddRange(manifest.Artifacts
            .Where(artifact => !resolvedArtifactKinds.Contains(artifact.Kind))
            .Select(artifact =>
                $"Artifact '{artifact.Kind}' is listed in the manifest but is missing or outside the capture directory."));
        InstrumentsTocInspection? instruments = null;
        PerfettoCaptureInspection? perfetto = null;
        if (string.Equals(manifest.Platform, NativeProfilePlatforms.Ios, StringComparison.Ordinal))
        {
            try
            {
                instruments = InspectInstrumentsToc(normalizedCaptureId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or XmlException)
            {
                warnings.Add($"The Instruments table of contents could not be inspected: {ex.Message}");
            }
        }
        else if (string.Equals(manifest.Platform, NativeProfilePlatforms.Android, StringComparison.Ordinal))
        {
            perfetto = await InspectPerfettoAsync(
                manifest,
                cancellationToken).ConfigureAwait(false);
            warnings.AddRange(perfetto.Warnings);
        }

        double? captureElapsedSeconds = manifest.StartedUtc is not null && manifest.CompletedUtc is not null
            ? Math.Max(0, (manifest.CompletedUtc.Value - manifest.StartedUtc.Value).TotalSeconds)
            : null;
        return new NativeProfileCaptureInspection(
            AnalyzerVersion,
            manifest.CaptureId,
            manifest.Platform,
            manifest.Engine,
            manifest.Preset,
            NormalizeEnum(manifest.State),
            manifest.RequestedDurationSeconds,
            captureElapsedSeconds,
            captureStore.GetCapturePath(manifest.CaptureId),
            authoritativeArtifact,
            artifacts,
            instruments,
            perfetto,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public InstrumentsTocInspection InspectInstrumentsToc(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        var normalizedCaptureId = captureId.Trim();
        if (!captureStore.TryLoadManifest(normalizedCaptureId, out var manifest)
            || manifest is null
            || !string.Equals(manifest.Platform, NativeProfilePlatforms.Ios, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException($"iOS native profile capture '{normalizedCaptureId}' was not found.");
        }

        if (!captureStore.TryResolveArtifactPath(
                manifest,
                InstrumentsProfileCaptureAdapter.TableOfContentsArtifactKind,
                out var tocPath)
            || tocPath is null)
        {
            throw new InvalidOperationException(
                $"iOS capture '{normalizedCaptureId}' does not contain an Instruments table of contents artifact.");
        }

        var document = LoadXml(tocPath);
        var runElements = document.Descendants()
            .Where(static element => HasLocalName(element, "run"))
            .ToArray();
        var tableElements = document.Descendants()
            .Where(static element => HasLocalName(element, "table"))
            .ToArray();
        var schemaCounts = tableElements
            .Select(ReadSchemaName)
            .Where(static schema => !string.IsNullOrWhiteSpace(schema))
            .GroupBy(static schema => schema!, StringComparer.Ordinal)
            .Select(group => new InstrumentsSchemaInspection(group.Key, group.Count()))
            .OrderByDescending(static schema => schema.Occurrences)
            .ThenBy(static schema => schema.Schema, StringComparer.Ordinal)
            .ToArray();
        var processes = document.Descendants()
            .Where(static element => HasLocalName(element, "process"))
            .Select(CreateProcessInspection)
            .Where(static process => process is not null)
            .Cast<InstrumentsProcessInspection>()
            .Distinct()
            .OrderBy(static process => process.Name, StringComparer.Ordinal)
            .ThenBy(static process => process.ProcessId, StringComparer.Ordinal)
            .ToArray();

        return new InstrumentsTocInspection(
            tocPath,
            document.Root?.Name.LocalName ?? string.Empty,
            runElements.Length,
            tableElements.Length,
            schemaCounts,
            processes);
    }

    public async Task<NativeProfileInspectionToolchain> GetPerfettoToolchainAsync(
        CancellationToken cancellationToken = default)
    {
        var path = ResolveTraceProcessorPath();
        if (path is null)
        {
            return new NativeProfileInspectionToolchain(
                false,
                null,
                null,
                "Perfetto Trace Processor was not found. Install trace_processor or trace_processor_shell, or set ANSIGHT_TRACE_PROCESSOR_PATH.");
        }

        try
        {
            var result = await processRunner.RunAsync(
                new NativeProfilingProcessRequest(
                    path,
                    ["--version"],
                    Environment.CurrentDirectory,
                    TimeSpan.FromSeconds(15)),
                outputReceived: null,
                cancellationToken).ConfigureAwait(false);
            return new NativeProfileInspectionToolchain(
                result.IsSuccess,
                path,
                ReadOutput(result),
                result.IsSuccess
                    ? "Perfetto Trace Processor inspection is available."
                    : "Perfetto Trace Processor was found, but its version could not be read.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new NativeProfileInspectionToolchain(
                false,
                path,
                null,
                $"Perfetto Trace Processor could not be started: {ex.Message}");
        }
    }

    public async Task<PerfettoQueryInspection> QueryPerfettoAsync(
        string captureId,
        string sql,
        int maximumRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ValidateSql(sql);
        if (maximumRows is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows),
                "Perfetto query row limits must be between 1 and 10000.");
        }

        var normalizedCaptureId = captureId.Trim();
        if (!captureStore.TryLoadManifest(normalizedCaptureId, out var manifest)
            || manifest is null
            || !string.Equals(manifest.Platform, NativeProfilePlatforms.Android, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException($"Android native profile capture '{normalizedCaptureId}' was not found.");
        }

        if (!captureStore.TryResolveArtifactPath(
                manifest,
                PerfettoProfileCaptureAdapter.TraceArtifactKind,
                out var tracePath)
            || tracePath is null)
        {
            throw new InvalidOperationException(
                $"Android capture '{normalizedCaptureId}' does not contain a Perfetto trace artifact.");
        }

        var toolchain = await GetPerfettoToolchainAsync(cancellationToken).ConfigureAwait(false);
        if (!toolchain.IsAvailable || toolchain.ToolPath is null)
        {
            throw new InvalidOperationException(toolchain.Message);
        }

        var normalizedSql = sql.Trim().TrimEnd(';').Trim();
        var boundedSql = $"SELECT * FROM ({normalizedSql}) LIMIT {maximumRows + 1}";
        var result = await processRunner.RunAsync(
            new NativeProfilingProcessRequest(
                toolchain.ToolPath,
                ["query", tracePath, boundedSql],
                captureStore.GetCapturePath(normalizedCaptureId),
                TimeSpan.FromSeconds(90)),
            outputReceived: null,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var detail = ReadOutput(result);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? "Perfetto Trace Processor could not execute the query."
                    : $"Perfetto Trace Processor could not execute the query: {detail}");
        }

        var table = PerfettoCsvParser.Parse(result.StandardOutput);
        var wasTruncated = table.Rows.Count > maximumRows;
        var rows = table.Rows.Take(maximumRows).ToArray();
        return new PerfettoQueryInspection(
            normalizedCaptureId,
            toolchain,
            normalizedSql,
            table.Columns,
            rows,
            wasTruncated,
            rows.Length);
    }

    private async Task<PerfettoCaptureInspection> InspectPerfettoAsync(
        NativeProfileCaptureManifest manifest,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        string? configPath = null;
        IReadOnlyList<string> dataSources = [];
        int? configuredDurationMilliseconds = null;
        if (captureStore.TryResolveArtifactPath(
                manifest,
                PerfettoProfileCaptureAdapter.ConfigArtifactKind,
                out var resolvedConfigPath)
            && resolvedConfigPath is not null)
        {
            configPath = resolvedConfigPath;
            var config = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            dataSources = DataSourceNameRegex().Matches(config)
                .Cast<Match>()
                .Select(match => match.Groups["name"].Value)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var durationMatch = DurationMillisecondsRegex().Match(config);
            if (durationMatch.Success
                && int.TryParse(
                    durationMatch.Groups["duration"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var duration))
            {
                configuredDurationMilliseconds = duration;
            }
        }
        else
        {
            warnings.Add("The retained Perfetto capture configuration is missing.");
        }

        var toolchain = await GetPerfettoToolchainAsync(cancellationToken).ConfigureAwait(false);
        PerfettoQueryInspection? traceOverview = null;
        if (toolchain.IsAvailable)
        {
            try
            {
                traceOverview = await QueryPerfettoAsync(
                    manifest.CaptureId,
                    """
                    SELECT
                      start_ts,
                      end_ts,
                      end_ts - start_ts AS duration_ns,
                      (SELECT COUNT(*) FROM process) AS process_count,
                      (SELECT COUNT(*) FROM thread) AS thread_count,
                      (SELECT COUNT(*) FROM slice) AS slice_count,
                      (SELECT COUNT(*) FROM counter) AS counter_count,
                      (SELECT COUNT(*) FROM sched) AS scheduling_slice_count
                    FROM trace_bounds
                    """,
                    1,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                warnings.Add($"Perfetto trace metadata could not be queried: {ex.Message}");
            }
        }
        else
        {
            warnings.Add(toolchain.Message);
        }

        return new PerfettoCaptureInspection(
            configPath,
            configuredDurationMilliseconds,
            dataSources,
            toolchain,
            traceOverview,
            warnings);
    }

    private NativeProfileArtifactLocation? CreateLocation(
        NativeProfileCaptureManifest manifest,
        NativeProfileArtifact artifact)
    {
        return captureStore.TryResolveArtifactPath(manifest, artifact.Kind, out var path)
               && path is not null
            ? new NativeProfileArtifactLocation(manifest.CaptureId, manifest.Platform, path, artifact)
            : null;
    }

    private string? ResolveTraceProcessorPath()
    {
        if (traceProcessorPathOverride is not null)
        {
            return ResolveConfiguredExecutable(traceProcessorPathOverride);
        }

        var environmentOverride = Environment.GetEnvironmentVariable("ANSIGHT_TRACE_PROCESSOR_PATH");
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return ResolveConfiguredExecutable(environmentOverride.Trim());
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "trace_processor.exe", "trace_processor_shell.exe", "trace_processor", "trace_processor_shell" }
            : new[] { "trace_processor", "trace_processor_shell" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string? ResolveConfiguredExecutable(string configuredPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (File.Exists(expandedPath))
        {
            return Path.GetFullPath(expandedPath);
        }

        if (!Directory.Exists(expandedPath))
        {
            return null;
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "trace_processor.exe", "trace_processor_shell.exe", "trace_processor", "trace_processor_shell" }
            : new[] { "trace_processor", "trace_processor_shell" };
        return executableNames
            .Select(name => Path.Combine(expandedPath, name))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    private static void ValidateSql(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (sql.Length > MaximumSqlLength)
        {
            throw new ArgumentException(
                $"Perfetto SQL must not exceed {MaximumSqlLength} characters.",
                nameof(sql));
        }

        var validationText = SqlStringOrCommentRegex().Replace(sql, " ");
        var statements = validationText
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (statements.Length != 1)
        {
            throw new ArgumentException(
                "Perfetto inspection accepts exactly one read-only SQL statement.",
                nameof(sql));
        }

        var firstToken = SqlFirstTokenRegex()
            .Match(statements[0])
            .Groups["token"]
            .Value
            .ToUpperInvariant();
        if (firstToken is not ("SELECT" or "WITH"))
        {
            throw new ArgumentException(
                "Perfetto inspection SQL must begin with SELECT or WITH.",
                nameof(sql));
        }

        if (SqlMutationKeywordRegex().IsMatch(validationText))
        {
            throw new ArgumentException(
                "Perfetto inspection SQL must be read-only.",
                nameof(sql));
        }
    }

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string? ReadSchemaName(XElement element)
        => ReadAttribute(element, "schema")
           ?? ReadAttribute(element, "name")
           ?? ReadAttribute(element, "id");

    private static InstrumentsProcessInspection? CreateProcessInspection(XElement element)
    {
        var processId = ReadAttribute(element, "pid") ?? ReadAttribute(element, "process-id");
        var name = ReadAttribute(element, "name")
                   ?? ReadAttribute(element, "process-name")
                   ?? ReadAttribute(element, "label");
        var path = ReadAttribute(element, "path");
        return processId is null && name is null && path is null
            ? null
            : new InstrumentsProcessInspection(processId, name, path);
    }

    private static string? ReadAttribute(XElement element, string localName)
        => element.Attributes()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Name.LocalName,
                localName,
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static bool HasLocalName(XElement element, string localName)
        => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static string? ReadOutput(NativeProfilingProcessResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    private static string NormalizeEnum<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }

    [GeneratedRegex("\\bname\\s*:\\s*\"(?<name>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex DataSourceNameRegex();

    [GeneratedRegex(@"\bduration_ms\s*:\s*(?<duration>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationMillisecondsRegex();

    [GeneratedRegex("'(?:''|[^'])*'|\"(?:\"\"|[^\"])*\"|--[^\\r\\n]*|/\\*[\\s\\S]*?\\*/", RegexOptions.CultureInvariant)]
    private static partial Regex SqlStringOrCommentRegex();

    [GeneratedRegex(@"^\s*(?<token>[A-Za-z]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SqlFirstTokenRegex();

    [GeneratedRegex(@"\b(ATTACH|DETACH|CREATE|DROP|INSERT|UPDATE|DELETE|REPLACE|ALTER|VACUUM|REINDEX)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SqlMutationKeywordRegex();
}

internal sealed record NativeProfileArtifactLocation(
    string CaptureId,
    string Platform,
    string Path,
    NativeProfileArtifact Artifact);

internal sealed record NativeProfileCaptureInspection(
    string AnalyzerVersion,
    string CaptureId,
    string Platform,
    string Engine,
    string Preset,
    string State,
    int RequestedDurationSeconds,
    double? CaptureElapsedSeconds,
    string CaptureDirectoryPath,
    NativeProfileArtifactLocation? AuthoritativeArtifact,
    IReadOnlyList<NativeProfileArtifactLocation> Artifacts,
    InstrumentsTocInspection? Instruments,
    PerfettoCaptureInspection? Perfetto,
    IReadOnlyList<string> Warnings);

internal sealed record InstrumentsTocInspection(
    string Path,
    string RootElement,
    int RunCount,
    int TableCount,
    IReadOnlyList<InstrumentsSchemaInspection> Schemas,
    IReadOnlyList<InstrumentsProcessInspection> Processes);

internal sealed record InstrumentsSchemaInspection(string Schema, int Occurrences);

internal sealed record InstrumentsProcessInspection(string? ProcessId, string? Name, string? Path);

internal sealed record NativeProfileInspectionToolchain(
    bool IsAvailable,
    string? ToolPath,
    string? Version,
    string Message);

internal sealed record PerfettoCaptureInspection(
    string? ConfigPath,
    int? ConfiguredDurationMilliseconds,
    IReadOnlyList<string> DataSources,
    NativeProfileInspectionToolchain Toolchain,
    PerfettoQueryInspection? TraceOverview,
    IReadOnlyList<string> Warnings);

internal sealed record PerfettoQueryInspection(
    string CaptureId,
    NativeProfileInspectionToolchain Toolchain,
    string Sql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool WasTruncated,
    int RowCount);

internal sealed record PerfettoCsvTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows);

internal static class PerfettoCsvParser
{
    public static PerfettoCsvTable Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new PerfettoCsvTable([], []);
        }

        var records = ParseRecords(csv);
        if (records.Count == 0)
        {
            return new PerfettoCsvTable([], []);
        }

        var columns = records[0].Select(static value => value ?? string.Empty).ToArray();
        var rows = records.Skip(1)
            .Where(record => record.Count > 1 || !string.IsNullOrEmpty(record[0]))
            .Select(record => (IReadOnlyList<string?>)NormalizeRecord(record, columns.Length))
            .ToArray();
        return new PerfettoCsvTable(columns, rows);
    }

    private static IReadOnlyList<List<string?>> ParseRecords(string csv)
    {
        var records = new List<List<string?>>();
        var record = new List<string?>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < csv.Length && csv[index + 1] == '\n')
                    {
                        index++;
                    }

                    CompleteRecord(records, record, field);
                    break;
                case '\n':
                    CompleteRecord(records, record, field);
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            CompleteRecord(records, record, field);
        }

        return records;
    }

    private static void CompleteRecord(
        ICollection<List<string?>> records,
        List<string?> record,
        StringBuilder field)
    {
        record.Add(field.ToString());
        records.Add([.. record]);
        record.Clear();
        field.Clear();
    }

    private static IReadOnlyList<string?> NormalizeRecord(IReadOnlyList<string?> record, int columnCount)
    {
        var normalized = new string?[columnCount];
        for (var index = 0; index < normalized.Length && index < record.Count; index++)
        {
            normalized[index] = string.Equals(record[index], "[NULL]", StringComparison.Ordinal)
                ? null
                : record[index];
        }

        return normalized;
    }
}
