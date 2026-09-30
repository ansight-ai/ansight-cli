namespace Ansight.Cli.Commands.Config;

internal sealed record CliConfigOutput(
    string Schema,
    string Action,
    string SettingsPath,
    CliConfigSetting Setting,
    IReadOnlyList<CliConfigSetting> Settings);
