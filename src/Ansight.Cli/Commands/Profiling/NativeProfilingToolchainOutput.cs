using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingToolchainOutput(
    string Schema,
    NativeProfilingToolchain Toolchain,
    NativeProfileInspectionToolchain? InspectionToolchain = null);
