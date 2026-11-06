namespace Ansight.Host.Notifications;

public sealed record NotificationAction(
    string Identifier,
    string Title,
    TimeSpan DeferralInterval);
