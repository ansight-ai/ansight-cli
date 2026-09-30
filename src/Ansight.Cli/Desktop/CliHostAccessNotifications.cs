namespace Ansight.Cli.Desktop;

internal sealed class CliHostAccessNotifications(INotificationSink? sink)
{
    internal const string Identifier = "host-access-required";
    private string? lastReason;

    public async Task UpdateAsync(AccessDecision decision, CancellationToken cancellationToken)
    {
        var reason = decision.IsAuthorized ? "active" : decision.Reason;
        if (reason == lastReason) return;
        lastReason = reason;
        if (sink is null) return;
        try
        {
            if (decision.IsAuthorized || reason != "product_access_required")
            {
                await sink.RemoveAsync(Identifier, cancellationToken).ConfigureAwait(false);
                return;
            }

            const string title = "Ansight access required";
            await sink.ShowAsync(new Notification(Identifier, title,
                "The local host is running. Open the player to restore access."), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // An unavailable desktop helper must not take away account recovery.
        }
    }
}
