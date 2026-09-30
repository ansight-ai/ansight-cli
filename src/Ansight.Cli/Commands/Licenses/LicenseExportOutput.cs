namespace Ansight.Cli.Commands.Licenses;

internal sealed record LicenseExportOutput(
    string SchemaVersion,
    string Path,
    string Format,
    int SoftwareCount,
    IReadOnlyList<string> Warnings);
