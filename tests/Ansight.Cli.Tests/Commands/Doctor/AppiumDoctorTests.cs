using System.Net;
using System.Text;

namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class AppiumDoctorTests
{
    [Theory]
    [InlineData("{\"xcuitest\":{\"version\":\"9.0.0\"}}")]
    [InlineData("{\"installed\":[{\"name\":\"appium-xcuitest-driver\"}]}")]
    [InlineData("xcuitest@9.0.0")]
    public void ContainsXcuiTestDriver_FindsInstalledDriver(string output)
    {
        Assert.True(AppiumDoctor.ContainsXcuiTestDriver(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"installed\":[]}")]
    [InlineData("{\"uiautomator2\":{\"version\":\"4.0.0\"}}")]
    public void ContainsXcuiTestDriver_RejectsMissingDriver(string output)
    {
        Assert.False(AppiumDoctor.ContainsXcuiTestDriver(output));
    }

    [Fact]
    public async Task CheckXcuiTestDriverAsync_WhenAppiumIsMissing_IsOptionalWarning()
    {
        var check = await AppiumDoctor.CheckXcuiTestDriverAsync(null, CancellationToken.None);

        Assert.Equal("device.ios.appium.xcuitest", check.Name);
        Assert.Equal("not-checked", check.Status);
        Assert.False(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Contains("appium driver install xcuitest", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckServerAsync_WhenReady_ReturnsSuccessfulCheck()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"value\":{\"ready\":true,\"message\":\"ready\"}}");
        using var httpClient = new HttpClient(handler);

        var check = await AppiumDoctor.CheckServerAsync(
            "http://appium.example.test:4723/wd/hub",
            httpClient,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("device.ios.appium.server", check.Name);
        Assert.Equal("available", check.Status);
        Assert.True(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Equal(
            "http://appium.example.test:4723/wd/hub/status",
            handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task CheckServerAsync_WhenServerReportsNotReady_ReturnsOptionalWarning()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"value\":{\"ready\":false,\"message\":\"No drivers available\"}}"));

        var check = await AppiumDoctor.CheckServerAsync(
            null,
            httpClient,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("not-ready", check.Status);
        Assert.False(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Contains("No drivers available", check.Message, StringComparison.Ordinal);
        Assert.Equal(AppiumDoctor.DefaultServerUrl, check.Path);
    }

    [Fact]
    public async Task CheckServerAsync_WhenUrlIsInvalid_DoesNotSendRequest()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);

        var check = await AppiumDoctor.CheckServerAsync(
            "not-a-url",
            httpClient,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("invalid-configuration", check.Status);
        Assert.False(check.IsSuccess);
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class StubHttpMessageHandler(
        HttpStatusCode statusCode,
        string content) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
