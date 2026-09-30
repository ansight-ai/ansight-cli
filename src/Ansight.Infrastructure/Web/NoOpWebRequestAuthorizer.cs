namespace Ansight.Infrastructure.Web;

public sealed class NoOpWebRequestAuthorizer : IWebRequestAuthorizer
{
    public ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}
