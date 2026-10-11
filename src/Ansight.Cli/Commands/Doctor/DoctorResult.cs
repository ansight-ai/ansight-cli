using Ansight.Infrastructure.Dependencies;

namespace Ansight.Cli.Commands.Doctor;

internal sealed record DoctorResult(
    string Schema,
    string OperatingSystem,
    string Architecture,
    string DotNetVersion,
    string DataDirectory,
    IReadOnlyList<DoctorCheck> Checks,
    bool IsHealthy,
    DateTimeOffset CapturedUtc)
{
    public DependencyBaselineSnapshot DependencyBaseline => Ansight.Infrastructure.Dependencies.DependencyBaseline.Bundled;

    public string Signal => Checks.Any(static check => !check.IsSuccess && check.IsRequired)
        ? "red"
        : Checks.Any(static check => !check.IsSuccess)
            ? "amber"
            : "green";
}
