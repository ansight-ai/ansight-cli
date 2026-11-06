namespace Ansight.Host.Profiling;

public sealed record PerfettoQueryInspection(
    string CaptureId,
    NativeProfileInspectionToolchain Toolchain,
    string Sql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool WasTruncated,
    int RowCount);
