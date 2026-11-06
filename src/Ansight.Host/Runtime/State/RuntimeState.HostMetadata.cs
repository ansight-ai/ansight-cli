namespace Ansight.Host.Runtime.State;

internal sealed partial class RuntimeState
{
    private ISessionHostMetadataCollector? hostMetadataCollector;

    public void ConfigureHostMetadataCollector(ISessionHostMetadataCollector collector)
    {
        hostMetadataCollector = collector;
    }

    private void BeginHostMetadataCapture(SessionState session, string? platform = null)
    {
        session.HostMetadata = SessionHostMetadataCollector.CreateSnapshot(platform);

        if (hostMetadataCollector is null)
        {
            session.HostMetadata = session.HostMetadata with
            {
                Status = "not-checked"
            };
        }

        session.ShouldCollectHostMetadata = true;
    }

    private void CollectHostMetadata(string sessionId, string? platform = null)
    {
        SessionHostMetadata snapshot;
        var collector = hostMetadataCollector;

        if (collector is null)
        {
            return;
        }

        lock (gate)
        {
            var canCollect = sessionsById.TryGetValue(sessionId, out var candidate)
                && candidate.ShouldCollectHostMetadata
                && candidate.HostMetadata is not null;

            if (!canCollect)
            {
                return;
            }

            var session = sessionsById[sessionId];
            var targetPlatform = SessionHostMetadataCollector.NormalizePlatform(platform);

            if (targetPlatform is not null)
            {
                if (session.HostMetadata!.TargetPlatform == targetPlatform)
                {
                    return;
                }

                session.HostMetadata = session.HostMetadata! with
                {
                    TargetPlatform = targetPlatform,
                    Status = "collecting"
                };
            }

            snapshot = session.HostMetadata!;
        }

        // Do not block pairing or device capture on optional tool discovery.
        _ = Task.Run(async () =>
        {
            SessionHostMetadata result;

            try
            {
                result = await collector.CollectAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception)
            {
                result = snapshot with
                {
                    Status = "partial",
                    CollectionCompletedUtc = DateTimeOffset.UtcNow
                };
            }

            lock (gate)
            {
                // A later device profile may have started a more specific probe. Never overwrite it.
                if (!sessionsById.TryGetValue(sessionId, out var session)
                    || !ReferenceEquals(session.HostMetadata, snapshot))
                {
                    return;
                }

                session.HostMetadata = result;
            }

            QueueSessionUpdated(sessionId, shouldPersist: true);
        });
    }
}
