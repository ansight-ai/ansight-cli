namespace Ansight.Cli.Desktop;

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
