namespace Ansight.Host.Profiling;

public sealed record InstrumentsTocInspection(
    string Path,
    string RootElement,
    int RunCount,
    int TableCount,
    IReadOnlyList<InstrumentsSchemaInspection> Schemas,
    IReadOnlyList<InstrumentsProcessInspection> Processes);
