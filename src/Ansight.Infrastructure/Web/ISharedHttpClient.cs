namespace Ansight.Infrastructure.Web;

public interface ISharedHttpClient
{
    HttpClient HttpClient { get; }

    HttpClient FileTransferHttpClient => HttpClient;
}
