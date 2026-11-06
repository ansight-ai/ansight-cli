using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record InstrumentsTocInspectionOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    InstrumentsTocInspection? TableOfContents);
