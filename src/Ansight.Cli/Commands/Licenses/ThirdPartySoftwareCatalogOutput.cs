namespace Ansight.Cli.Commands.Licenses;

internal sealed record ThirdPartySoftwareCatalogOutput(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ThirdPartySoftwareEntry> Software,
    IReadOnlyList<string> Warnings);
