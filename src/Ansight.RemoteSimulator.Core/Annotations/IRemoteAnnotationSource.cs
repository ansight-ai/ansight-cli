using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.RemoteSimulator.Core.Annotations;

public interface IRemoteAnnotationSource
{
    Task<IReadOnlyList<RemoteAnnotationSession>> ListLiveSessionsAsync(
        CancellationToken cancellationToken = default);

    Task<RemoteAnnotationResult> CreateAnnotationAsync(
        RemoteAnnotationRequest request,
        CancellationToken cancellationToken = default);

    Task<RemoteAnnotationResult> UpdateAnnotationAsync(
        RemoteAnnotationUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<RemoteOperationResult> DeleteAnnotationAsync(
        RemoteAnnotationDeleteRequest request,
        CancellationToken cancellationToken = default);

    Task<RemoteAnnotationBatchStartResult> StartAnnotationBatchAsync(
        RemoteAnnotationBatchStartRequest request,
        CancellationToken cancellationToken = default);
}
