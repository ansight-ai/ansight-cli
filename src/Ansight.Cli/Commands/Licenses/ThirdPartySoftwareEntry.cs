namespace Ansight.Cli.Commands.Licenses;

internal sealed record ThirdPartySoftwareEntry(
    string Name,
    string? Version,
    string License,
    string Source,
    string Distribution,
    string Usage,
    string? ProjectUrl,
    string? LicenseUrl);
