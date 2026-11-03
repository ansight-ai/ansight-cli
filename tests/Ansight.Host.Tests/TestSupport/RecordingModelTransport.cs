using Ansight.Host.SimulatorAgent;

namespace Ansight.Host.Tests.TestSupport;

internal sealed class RecordingModelTransport : IModelExecutionTransport, IModelUsageRecorder
{
    public List<SimulatorAgentModelPassUsage> Usage { get; } = [];
    public Exception? RecordingFailure { get; init; }
    public HttpClient? Client { get; init; }
    public string Description => "recording-test";
    public bool SupportsWebSockets => true;

    public void RecordUsage(SimulatorAgentModelPassUsage usage)
    {
        if (RecordingFailure is not null)
        {
            throw RecordingFailure;
        }

        Usage.Add(usage);
    }

    public ValueTask<string> ResolveAccessKeyAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult("test-key");
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Client!.SendAsync(request, cancellationToken);
    }

    public string DescribeFailure(int statusCode, string responseContent)
    {
        return "Test provider failure";
    }
}
