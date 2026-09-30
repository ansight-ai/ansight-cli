namespace Ansight.Host.Notifications;

public interface INotificationSink
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task ShowAsync(
        Notification notification,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string notificationIdentifier,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
