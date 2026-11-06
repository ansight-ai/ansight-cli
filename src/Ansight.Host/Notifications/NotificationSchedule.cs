namespace Ansight.Host.Notifications;

public sealed record NotificationSchedule(
    TimeSpan Delay,
    TimeSpan? RepeatInterval = null,
    bool PreserveExisting = false);
