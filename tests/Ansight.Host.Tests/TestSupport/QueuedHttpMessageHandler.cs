using System.Net;

namespace Ansight.Host.Tests.TestSupport;

internal sealed class QueuedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses;

    public QueuedHttpMessageHandler(params string[] responseBodies)
    {
        responses = new Queue<HttpResponseMessage>(responseBodies.Select(body => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        }));
    }

    public List<HttpMethod> Methods { get; } = [];

    public List<Uri> RequestUris { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public List<string?> AuthorizationSchemes { get; } = [];

    public List<string?> AuthorizationParameters { get; } = [];

    public List<IReadOnlyDictionary<string, string[]>> RequestHeaders { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Methods.Add(request.Method);
        RequestUris.Add(request.RequestUri!);
        AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
        AuthorizationParameters.Add(request.Headers.Authorization?.Parameter);
        RequestHeaders.Add(request.Headers.ToDictionary(
            static header => header.Key,
            static header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase));
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        return responses.Count > 0
            ? responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
    }
}
