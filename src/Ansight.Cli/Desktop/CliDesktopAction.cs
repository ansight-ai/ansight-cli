namespace Ansight.Cli.Desktop;

internal sealed record CliDesktopAction(
    string Schema,
    string Action,
    string? SessionId = null,
    string? Token = null);
