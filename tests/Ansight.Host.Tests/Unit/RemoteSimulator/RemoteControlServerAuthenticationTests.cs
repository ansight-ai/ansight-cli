using System.Net;
using System.Net.Http.Headers;
using Ansight.RemoteSimulator.Core.Access;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Streaming;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class RemoteControlServerAuthenticationTests
{
    private const string SessionToken = "remote-auth-test-token";

    private static RemoteControlServer CreateServer(IRemoteAccessAuthorizer authorizer)
        => new(
            new FakeRuntimeSource(),
            new FakeRemoteSimulatorFrameSource(),
            new FakeRemoteSimulatorInputSink(),
            requestedPort: 0,
            sessionToken: SessionToken,
            accessAuthorizer: authorizer,
            allowUnauthenticatedLoopback: false);

    private static HttpClient CreateAuthenticatedClient(string bearerToken)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    private static string Endpoint(RemoteControlServer server)
        => $"http://127.0.0.1:{server.Port}/api/state?token={SessionToken}";

    private sealed class FakeRemoteAccessAuthorizer(RemoteAccessAuthorization authorization)
        : IRemoteAccessAuthorizer
    {
        public int CallCount { get; private set; }

        public string LastBearerToken { get; private set; } = string.Empty;

        public ValueTask<RemoteAccessAuthorization> AuthorizeAsync(
            string bearerToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastBearerToken = bearerToken;
            return ValueTask.FromResult(authorization);
        }
    }

    private sealed class FakeRuntimeSource : IRemoteRuntimeSource
    {
        public RemoteRuntimeSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            Array.Empty<RemoteRuntimeDevice>(),
            null);
    }

}
