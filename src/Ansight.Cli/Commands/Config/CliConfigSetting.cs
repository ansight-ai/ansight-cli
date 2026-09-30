namespace Ansight.Cli.Commands.Config;

internal sealed record CliConfigSetting(
    string Name,
    object? Value,
    bool IsSet,
    string Status);
