namespace Ansight.Host.Notifications;

public sealed record Notification(
    string Identifier,
    string Title,
    string Body,
    string? SessionId = null,
    NotificationSchedule? Schedule = null,
    NotificationAction? Action = null);
