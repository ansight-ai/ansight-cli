namespace Ansight.Host.SimulatorAgent;

/// <summary>An optional transport owns its connection details and execution credentials.</summary>
public interface IModelExecutionTransport
{
    string Description { get; }
    bool SupportsWebSockets { get; }
    ValueTask<string> ResolveAccessKeyAsync(CancellationToken cancellationToken);
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    string DescribeFailure(int statusCode, string responseContent);
}
