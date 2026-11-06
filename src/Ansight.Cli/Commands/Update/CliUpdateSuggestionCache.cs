using System.Text.Json;

namespace Ansight.Cli.Commands.Update;

internal sealed record CliUpdateSuggestionCache(
    string Schema,
    DateTimeOffset CheckedAtUtc,
    string CurrentVersion,
    long CurrentBuildNumber,
    string LatestVersion,
    long LatestBuildNumber,
    string Channel,
    bool IsUpdateAvailable);
