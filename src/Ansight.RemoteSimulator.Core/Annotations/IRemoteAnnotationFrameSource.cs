using Ansight.RemoteSimulator.Core.Recording;

namespace Ansight.RemoteSimulator.Core.Annotations;

public interface IRemoteAnnotationFrameSource
{
    Task<RemoteRecordingFrame?> GetBatchFrameAsync(
        string deviceIdentifier,
        string batchId,
        string sessionId,
        string frameId,
        CancellationToken cancellationToken = default);
}
