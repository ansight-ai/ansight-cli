
namespace Ansight.Host.Runtime.State;

internal static class SessionLogStreamFactory
{
    internal static SessionLogStreamState GetOrCreateLogStream(SessionState session, string streamId)
    {
        if (session.LogStreams.TryGetValue(streamId, out var stream))
        {
            return stream;
        }

        stream = new SessionLogStreamState
        {
            StreamId = streamId,
            Kind = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => SessionLogStreamKinds.AnsightSdk,
                SessionLogStreamIds.AndroidLogcat => SessionLogStreamKinds.AndroidLogcat,
                SessionLogStreamIds.AppleUnifiedLog => SessionLogStreamKinds.AppleUnifiedLog,
                _ => SessionLogStreamKinds.Imported
            },
            DisplayName = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => "Ansight SDK",
                SessionLogStreamIds.AndroidLogcat => "Android Logcat",
                SessionLogStreamIds.AppleUnifiedLog => "Apple Unified Log",
                _ => streamId
            },
            Status = SessionLogStreamStatuses.Pending
        };
        session.LogStreams[streamId] = stream;

        return stream;
    }
}
