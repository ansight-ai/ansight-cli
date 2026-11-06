using Ansight.Infrastructure.Dependencies;

namespace Ansight.Cli.Commands.Update;

internal sealed record CliVersionOutput(
    string Schema,
    string Version,
    long BuildNumber,
    string InformationalVersion,
    string CommitSha,
    string Channel,
    string Rid,
    string DataDirectory,
    string ExecutablePath,
    bool IsInstallerManaged,
    string? ReceiptPath,
    string? UpdateFeed)
{
    public DependencyBaselineSnapshot DependencyBaseline => Ansight.Infrastructure.Dependencies.DependencyBaseline.Bundled;
}
