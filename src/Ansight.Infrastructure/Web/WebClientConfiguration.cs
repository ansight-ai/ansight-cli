namespace Ansight.Infrastructure.Web;

public sealed class WebClientConfiguration : IWebClientConfiguration
{
    public WebClientConfiguration(Uri baseUri)
    {
        BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
    }

    public Uri BaseUri { get; }
}
