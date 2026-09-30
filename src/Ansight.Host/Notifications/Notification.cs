namespace Ansight.Host.Notifications;

public sealed record Notification(
    string Identifier,
    string Title,
    string Body,
    string? SessionId = null,
    NotificationSchedule? Schedule = null,
    NotificationAction? Action = null);

public sealed record NotificationSchedule(
    TimeSpan Delay,
    TimeSpan? RepeatInterval = null,
    bool PreserveExisting = false);

public sealed record NotificationAction(
    string Identifier,
    string Title,
    TimeSpan DeferralInterval);
