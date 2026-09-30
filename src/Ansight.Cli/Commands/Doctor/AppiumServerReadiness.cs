namespace Ansight.Cli.Commands.Doctor;

internal sealed record AppiumServerReadiness(bool? IsReady, string? Message);
