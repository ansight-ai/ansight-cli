namespace Ansight.Infrastructure.Web;

public sealed class SharedHttpClient : ISharedHttpClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FileTransferTimeout = TimeSpan.FromMinutes(15);

    public SharedHttpClient()
    {
        HttpClient = CreateHttpClient(RequestTimeout);
        FileTransferHttpClient = CreateHttpClient(FileTransferTimeout);
    }

    public HttpClient HttpClient { get; }

    public HttpClient FileTransferHttpClient { get; }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ansight/1.0");
        return client;
    }
}
