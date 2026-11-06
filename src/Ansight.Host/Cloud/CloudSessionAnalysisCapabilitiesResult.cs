using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionAnalysisCapabilitiesResult(
    bool IsSuccess,
    string Message,
    CloudSessionAnalysisCapabilities? Capabilities)
{
    public static CloudSessionAnalysisCapabilitiesResult Success(
        CloudSessionAnalysisCapabilities capabilities)
        => new(true, string.Empty, capabilities);

    public static CloudSessionAnalysisCapabilitiesResult Failure(string message)
        => new(false, message, null);
}
