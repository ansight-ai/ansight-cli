
namespace Ansight.Host;

public sealed record SessionVideoEncodingResult(
    string EncoderName,
    IReadOnlyList<long> WrittenPresentationTimesUs);
