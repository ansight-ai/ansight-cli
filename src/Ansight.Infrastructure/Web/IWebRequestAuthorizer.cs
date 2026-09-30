namespace Ansight.Infrastructure.Web;

public interface IWebRequestAuthorizer
{
    ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}
