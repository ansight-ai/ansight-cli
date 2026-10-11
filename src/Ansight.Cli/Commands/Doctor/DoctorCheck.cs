using Ansight.Infrastructure.Dependencies;

namespace Ansight.Cli.Commands.Doctor;

internal sealed record DoctorCheck(
    string Name,
    string Status,
    bool IsSuccess,
    bool IsRequired,
    string Message,
    string? Path)
{
    public DependencyVersionAdvice? VersionAdvice { get; init; }

    public string Justification => DoctorCheckGuidance.GetJustification(Name);

    public string InstallInstructions => Status == "not-applicable"
        ? "No installation is needed on this platform."
        : DoctorCheckGuidance.GetInstallInstructions(Name);

    public string Signal => IsSuccess
        ? "green"
        : IsRequired
            ? "red"
            : "amber";
}
