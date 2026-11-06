
namespace Ansight.Host;

public interface ISessionVideoEncoder
{
    string Name { get; }

    Task<SessionVideoEncodingResult> EncodeAsync(
        SessionVideoEncodingRequest request,
        CancellationToken cancellationToken = default);
}
