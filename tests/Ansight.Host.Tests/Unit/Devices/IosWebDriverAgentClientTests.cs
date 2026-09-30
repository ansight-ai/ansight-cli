using System.Net;
using System.Text;
using System.Text.Json;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class IosWebDriverAgentClientTests
{
    [Fact]
    public async Task GetPageSourceAsync_UsesDirectWdaProtocolWithoutAppiumCapabilities()
    {
        var handler = new RecordingWdaHandler();
        using var client = new IosWebDriverAgentClient(
            new IosWebDriverAgentOptions("http://127.0.0.1:8100/"),
            handler);

        var source = await client.GetPageSourceAsync(
            "com.example.target",
            CancellationToken.None);

        Assert.Equal(400, source.ViewportWidth);
        Assert.Equal(800, source.ViewportHeight);
        Assert.Equal(["/session", "/session/wda-session/source"], handler.Paths);
        using var request = JsonDocument.Parse(handler.SessionRequestBody!);
        var capabilities = request.RootElement
            .GetProperty("capabilities")
            .GetProperty("alwaysMatch");
        Assert.Equal("com.example.target", capabilities.GetProperty("bundleId").GetString());
        Assert.DoesNotContain(
            capabilities.EnumerateObject(),
            static property => property.Name.StartsWith("appium:", StringComparison.Ordinal));
    }

    private sealed class RecordingWdaHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public string? SessionRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/session")
            {
                SessionRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse("""{"value":{"sessionId":"wda-session"}}""");
            }

            if (path.EndsWith("/source", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"value":"<AppiumAUT><XCUIElementTypeApplication x=\"0\" y=\"0\" width=\"400\" height=\"800\" /></AppiumAUT>"}""");
            }

            return JsonResponse("""{"value":null}""");
        }

        private static HttpResponseMessage JsonResponse(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
    }
}
