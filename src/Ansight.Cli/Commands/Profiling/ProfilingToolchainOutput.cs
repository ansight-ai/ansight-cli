using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingToolchainOutput(
    string Schema,
    DotNetProfilingToolchain Toolchain);
