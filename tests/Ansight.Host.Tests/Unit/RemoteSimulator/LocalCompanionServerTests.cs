using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Access;
using Ansight.RemoteSimulator.Core.Server;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class LocalCompanionServerTests
{
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, 0)]
    [InlineData("wrong", HttpStatusCode.Forbidden, 0)]
    [InlineData("approved", HttpStatusCode.OK, 1)]
    public async Task PinnedTls_RequiresAuthorizationBeforeDispatch(string? token, HttpStatusCode expected, int expectedCalls)
    {
        var calls = 0;
        await using var server = new LocalCompanionServer(new TestAuthorizer(), (request, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new LocalCompanionResponse(HttpStatusCode.OK, JsonSerializer.SerializeToElement(new { local = true })));
        });
        server.Start();
        using var client = CreateClient(server, server.CertificateSha256);
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.GetAsync("/api/state");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task PinnedTls_AcceptsIpv6Loopback()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            return;
        }

        await using var server = new LocalCompanionServer(new TestAuthorizer(), (_, _) =>
            Task.FromResult(new LocalCompanionResponse(HttpStatusCode.OK, JsonSerializer.SerializeToElement(new { local = true }))));
        server.Start();
        using var client = CreateClient(server, server.CertificateSha256);
        client.BaseAddress = new Uri($"https://[::1]:{server.Port}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "approved");

        using var response = await client.GetAsync("/api/state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PinnedTls_RejectsImpersonatedCertificate()
    {
        await using var server = new LocalCompanionServer(new TestAuthorizer(), (_, _) => throw new InvalidOperationException("Must not dispatch"));
        server.Start();
        using var client = CreateClient(server, new string('0', 64));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "approved");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/api/state"));
    }

    [Fact]
    public async Task PinnedTls_RejectsQueryCredentialsAndOversizedRequests()
    {
        var calls = 0;
        await using var server = new LocalCompanionServer(new TestAuthorizer(), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Must not dispatch");
        });
        server.Start();
        using var client = CreateClient(server, server.CertificateSha256);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "approved");

        using var query = await client.GetAsync("/api/state?token=secret");
        using var oversized = await client.PostAsync("/api/command", new StringContent(new string('x', 65537)));

        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(0, calls);
    }

    private static HttpClient CreateClient(LocalCompanionServer server, string fingerprint)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null
                && Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase)
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{server.Port}"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private sealed class TestAuthorizer : IRemoteAccessAuthorizer
    {
        public ValueTask<RemoteAccessAuthorization> AuthorizeAsync(string token, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(token == "approved"
                ? RemoteAccessAuthorization.Allow()
                : RemoteAccessAuthorization.Deny("Not approved"));
        }
    }
}
