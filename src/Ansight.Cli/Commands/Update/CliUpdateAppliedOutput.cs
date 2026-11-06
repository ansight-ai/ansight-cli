using Ansight.Infrastructure.Dependencies;

namespace Ansight.Cli.Commands.Update;

internal sealed record CliUpdateAppliedOutput(
    string Schema,
    string PreviousVersion,
    long PreviousBuildNumber,
    string InstalledVersion,
    long InstalledBuildNumber,
    string Channel,
    bool WasUpdated,
    bool ChannelChanged,
    string Message);
