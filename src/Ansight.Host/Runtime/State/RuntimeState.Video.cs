namespace Ansight.Host.Runtime.State;

internal sealed partial class RuntimeState
{
    public OperationResult AddSessionVideoRecording(string sessionId, SessionVideoRecording recording, string sourceFilePath)
    {
        if (!EnsureSessionLoaded(sessionId)) return OperationResult.Failure($"Session '{sessionId}' was not found.");
        try
        {
            recording.Validate();
            string appId;
            lock (gate)
            {
                if (!sessionsById.TryGetValue(sessionId, out var session))
                    return OperationResult.Failure($"Session '{sessionId}' was not found.");
                appId = session.AppId;
            }
            sessionCaptureStore.SaveVideoFile(appId, sessionId, recording, sourceFilePath);
            AppSessionSnapshot snapshot;
            lock (gate)
            {
                if (!sessionsById.TryGetValue(sessionId, out var session))
                    return OperationResult.Failure($"Session '{sessionId}' was not found.");
                session.VideoRecordings.RemoveAll(item => item.RecordingId == recording.RecordingId);
                session.VideoRecordings.Add(recording);
                session.VideoRecordings.Sort((left, right) => left.StartedUtc.CompareTo(right.StartedUtc));
                SessionStateMapper.TouchTimelineEnd(session, recording.EndedUtc);
                snapshot = SessionStateMapper.CreateSnapshot(session);
            }
            sessionCaptureStore.Save(snapshot);
            PersistAndBroadcast(sessionId);
            return OperationResult.Success("Video retained in the session replay.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult.Failure($"Could not save replay video: {exception.Message}");
        }
    }
}
