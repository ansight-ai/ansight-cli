namespace Ansight.Cli.Desktop;

internal static class CliDesktopProtocol
{
    public const string CommandSchema = "ansight.desktop-command/v1";
    public const string ActionSchema = "ansight.desktop-action/v1";
}

internal sealed record CliDesktopCommand(
    string Schema,
    string Kind,
    string? Url = null,
    string? Identifier = null,
    string? Title = null,
    string? Body = null,
    string? SessionId = null,
    double? DeliveryDelaySeconds = null,
    double? RepeatIntervalSeconds = null,
    bool PreserveExisting = false,
    string? NotificationActionIdentifier = null,
    string? NotificationActionTitle = null,
    double? NotificationActionDeferralSeconds = null,
    bool? UpdateAvailable = null,
    string? RunnerState = null,
    string? RunnerDetail = null);

internal sealed record CliDesktopAction(
    string Schema,
    string Action,
    string? SessionId = null,
    string? Token = null);

internal enum CliDesktopRequestedAction
{
    Stop,
    Restart,
    Update
}
