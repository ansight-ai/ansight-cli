namespace Ansight.Host.Runtime.State;

internal interface IRuntimeState :
    IRuntimeNotifications,
    ISessionReader,
    ISessionLifecycle,
    ISessionIngestion,
    ISessionEditor,
    ISessionAnnotationMutator
{
    OperationResult UpdateRecordingTranscript(
        string sessionId,
        string recordingId,
        Func<SessionAnalysisRecord?, SessionAnalysisRecord?> update)
        => OperationResult.Failure("Recording transcripts are unavailable on this host.");

    void ConfigureHostMetadataCollector(ISessionHostMetadataCollector collector)
    {
    }
}
