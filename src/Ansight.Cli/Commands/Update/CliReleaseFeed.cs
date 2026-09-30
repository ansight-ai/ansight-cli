namespace Ansight.Cli.Commands.Update;

internal sealed record CliReleaseFeed(
    string? Channel,
    string Version,
    long BuildNumber,
    string? CliVersion,
    long? CliBuildNumber,
    string? CliSummary,
    DateTimeOffset? CliPublishedAt,
    string? Summary,
    DateTimeOffset? PublishedAt,
    string? CommitSha,
    string? Product = null);
