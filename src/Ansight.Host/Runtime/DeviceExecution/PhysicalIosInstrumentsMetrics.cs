using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record PhysicalIosActivitySample(
    DateTimeOffset CapturedAtUtc,
    long? CpuMillicores,
    long? PhysicalFootprintBytes,
    long? RealMemoryBytes);

internal sealed record PhysicalIosInstrumentsMetricResult(
    int RowCount,
    int CpuSampleCount,
    int MemorySampleCount,
    string TableSchema);

internal static class PhysicalIosInstrumentsMetrics
{
    private const string Source = "xcode-instruments";

    internal static IReadOnlyList<PhysicalIosActivitySample> Parse(
        string xmlPath, DateTimeOffset startedUtc, int expectedProcessId)
        => Parse(xmlPath, startedUtc, expectedProcessId, out _);

    private static IReadOnlyList<PhysicalIosActivitySample> Parse(
        string xmlPath, DateTimeOffset startedUtc, int expectedProcessId, out string tableSchema)
    {
        using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader);
        var node = document.Root?.Elements("node").FirstOrDefault(candidate =>
            string.Equals((string?)candidate.Element("schema")?.Attribute("name"),
                "activity-monitor-process-live", StringComparison.Ordinal))
            ?? document.Root?.Elements("node").FirstOrDefault(candidate =>
                string.Equals((string?)candidate.Element("schema")?.Attribute("name"),
                    "sysmon-process", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The Instruments XML has no Activity Monitor process table.");
        tableSchema = (string)node.Element("schema")!.Attribute("name")!;
        var rawProcessTable = tableSchema == "sysmon-process";
        var timeColumn = rawProcessTable ? "time" : "start";
        var realMemoryColumn = rawProcessTable ? "memory-resident-size" : "memory-real";
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var columnIndex = 0;
        foreach (var column in node.Element("schema")!.Elements("col"))
        {
            var name = (string?)column.Element("mnemonic");
            if (!string.IsNullOrWhiteSpace(name)) columns.Add(name, columnIndex);
            columnIndex++;
        }
        foreach (var required in new[] { timeColumn, "pid" })
            if (!columns.ContainsKey(required))
                throw new InvalidDataException($"The Instruments process table has no '{required}' column.");

        var references = document.Descendants()
            .Where(element => element.Attribute("id") is not null)
            .ToDictionary(element => (string)element.Attribute("id")!, StringComparer.Ordinal);
        var samples = new List<PhysicalIosActivitySample>();
        foreach (var row in node.Elements("row"))
        {
            var values = row.Elements().ToArray();
            if (!TryReadLong("pid", out var pid) || pid != expectedProcessId
                || !TryReadLong(timeColumn, out var startNanoseconds) || startNanoseconds < 0)
                continue;
            DateTimeOffset timestamp;
            try { timestamp = startedUtc.AddTicks(checked(startNanoseconds / 100)); }
            catch (ArgumentOutOfRangeException) { continue; }
            catch (OverflowException) { continue; }

            long? cpuMillicores = null;
            if (TryReadDecimal("cpu-percent", out var cpuPercent) && cpuPercent >= 0
                && cpuPercent <= long.MaxValue / 10m)
                cpuMillicores = (long)Math.Round(cpuPercent * 10m, MidpointRounding.AwayFromZero);
            long? footprint = TryReadLong("memory-physical-footprint", out var footprintBytes)
                && footprintBytes >= 0 ? footprintBytes : null;
            long? realMemory = TryReadLong(realMemoryColumn, out var realMemoryBytes)
                && realMemoryBytes >= 0 ? realMemoryBytes : null;
            if (cpuMillicores is null && footprint is null && realMemory is null) continue;
            samples.Add(new PhysicalIosActivitySample(timestamp, cpuMillicores, footprint, realMemory));

            bool TryReadLong(string name, out long value)
                => long.TryParse(ReadValue(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            bool TryReadDecimal(string name, out decimal value)
                => decimal.TryParse(ReadValue(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            string? ReadValue(string name)
            {
                if (!columns.TryGetValue(name, out var index) || index >= values.Length) return null;
                var element = values[index];
                for (var depth = 0; depth < 8 && element.Attribute("ref") is { } reference; depth++)
                {
                    if (!references.TryGetValue(reference.Value, out var referenced)) return null;
                    element = referenced;
                }
                return element.Name.LocalName == "sentinel" ? null : element.Value;
            }
        }
        return samples;
    }

    internal static PhysicalIosInstrumentsMetricResult Ingest(
        IRuntimeState state, string sessionId, string xmlPath, DateTimeOffset startedUtc, int processId)
    {
        var samples = Parse(xmlPath, startedUtc, processId, out var tableSchema);
        if (samples.Count == 0)
            throw new InvalidDataException("The Instruments process table has no CPU or memory samples for the watched PID.");

        byte? cpuChannel = samples.Any(sample => sample.CpuMillicores.HasValue)
            ? HostSessionEvents.AllocateChannel(state, sessionId, "Process CPU", "cpu", "millicores", Source, "cpu-millicores")
            : null;
        byte? footprintChannel = samples.Any(sample => sample.PhysicalFootprintBytes.HasValue)
            ? HostSessionEvents.AllocateChannel(state, sessionId, "Process physical footprint", "memory", "bytes", Source, "physical-footprint")
            : null;
        byte? realMemoryChannel = samples.Any(sample => sample.RealMemoryBytes.HasValue)
            ? HostSessionEvents.AllocateChannel(state, sessionId, "Process real memory", "memory", "bytes", Source, "real-memory")
            : null;
        var metrics = new List<SessionMetricSample>(samples.Count * 3);
        foreach (var sample in samples)
        {
            Add(cpuChannel, sample.CpuMillicores);
            Add(footprintChannel, sample.PhysicalFootprintBytes);
            Add(realMemoryChannel, sample.RealMemoryBytes);
            void Add(byte? channel, long? value)
            {
                if (channel is { } id && value is { } amount)
                    metrics.Add(new SessionMetricSample { ChannelId = id, Value = amount, CapturedAtUtc = sample.CapturedAtUtc });
            }
        }
        state.AddSessionMetrics(sessionId, metrics, 1);
        return new PhysicalIosInstrumentsMetricResult(samples.Count,
            samples.Count(sample => sample.CpuMillicores.HasValue),
            samples.Count(sample => sample.PhysicalFootprintBytes.HasValue || sample.RealMemoryBytes.HasValue),
            tableSchema);
    }

    internal static void MarkIngested(IRuntimeState state, string sessionId,
        PhysicalIosInstrumentsMetricResult result)
    {
        if (!state.TryGetSessionSnapshot(sessionId, out var snapshot)) return;
        var properties = snapshot!.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
        var instruments = properties["instruments"] as JsonObject ?? new JsonObject();
        instruments["sampleMetricsIngested"] = true;
        instruments["metricRowCount"] = result.RowCount;
        instruments["cpuSampleCount"] = result.CpuSampleCount;
        instruments["memorySampleCount"] = result.MemorySampleCount;
        instruments["processTableSchema"] = result.TableSchema;
        if (instruments.Parent is null) properties["instruments"] = instruments;
        if (properties["deviceExecution"] is JsonObject execution
            && execution["capabilities"] is JsonObject capabilities)
        {
            capabilities["telemetry.process.cpu"] = new JsonObject
            {
                ["available"] = result.CpuSampleCount > 0,
                ["provider"] = Source,
                ["reason"] = result.CpuSampleCount > 0 ? null : "The trace has no CPU samples."
            };
            capabilities["telemetry.process.memory"] = new JsonObject
            {
                ["available"] = result.MemorySampleCount > 0,
                ["provider"] = Source,
                ["reason"] = result.MemorySampleCount > 0 ? null : "The trace has no memory samples."
            };
        }
        state.SetSessionCustomProperties(sessionId, properties);
        HostSessionEvents.Publish(state, sessionId,
            "instruments.metrics.ingested", "host.instruments.metrics.ingested", result.RowCount.ToString(CultureInfo.InvariantCulture));
    }
}
