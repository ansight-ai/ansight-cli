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
    public string Signal => Checks.Any(static check => !check.IsSuccess && check.IsRequired)
        ? "red"
        : Checks.Any(static check => !check.IsSuccess)
            ? "amber"
            : "green";
}
