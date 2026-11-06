using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Access;

namespace Ansight.RemoteSimulator.Core.Server;

/// <summary>An authenticated, pinned-TLS entry point for local companion signaling.</summary>
public sealed class LocalCompanionServer : IAsyncDisposable
{
    private readonly IRemoteAccessAuthorizer authorizer;
    private readonly Func<LocalCompanionRequest, CancellationToken, Task<LocalCompanionResponse>> dispatch;
    private readonly X509Certificate2 certificate;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly SemaphoreSlim capacity = new(16, 16);
    private TcpListener? listener;
    private Task? acceptTask;

    public LocalCompanionServer(
        IRemoteAccessAuthorizer authorizer,
        Func<LocalCompanionRequest, CancellationToken, Task<LocalCompanionResponse>> dispatch)
    {
        this.authorizer = authorizer;
        this.dispatch = dispatch;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ansight Companion", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
    }

    public string CertificateSha256 => Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    public int Port { get; private set; }

    public void Start()
    {
        if (listener is not null)
        {
            return;
        }

        // Bonjour can resolve the Mac to either address family. Accept both on
        // the advertised port so IPv6 Wi-Fi does not fall back to the relay.
        listener = new TcpListener(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        if (Socket.OSSupportsIPv6)
        {
            listener.Server.DualMode = true;
        }

        listener.Start(16);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptAsync(listener, lifetime.Token);
    }

    private async Task AcceptAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                if (!capacity.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                clients.TryAdd(client, 0);
                _ = HandleAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        await using (var stream = new SslStream(client.GetStream()))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, timeout.Token).ConfigureAwait(false);

                var request = await RemoteControlHttpTransport.ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var authorization = request.Headers.GetValueOrDefault("Authorization", string.Empty);
                var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authorization[7..].Trim()
                    : string.Empty;
                if (string.IsNullOrEmpty(token))
                {
                    await WriteErrorAsync(stream, HttpStatusCode.Unauthorized, "Sign in again", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var access = await authorizer.AuthorizeAsync(token, timeout.Token).ConfigureAwait(false);
                if (!access.IsAuthorized)
                {
                    await WriteErrorAsync(stream, HttpStatusCode.Forbidden, "Access unavailable", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var invalidTarget = !request.Target.StartsWith("/api/", StringComparison.Ordinal)
                    || request.Target.Contains('?')
                    || request.Target.Contains('#')
                    || request.Headers.ContainsKey("Transfer-Encoding");
                if (invalidTarget)
                {
                    await WriteErrorAsync(stream, HttpStatusCode.BadRequest, "Invalid request", timeout.Token).ConfigureAwait(false);
                    return;
                }

                JsonElement? body = request.Body.Length == 0
                    ? null
                    : JsonSerializer.Deserialize<JsonElement>(request.Body);
                var response = await dispatch(new LocalCompanionRequest(request.Method, request.Target, body), timeout.Token)
                    .ConfigureAwait(false);
                await RemoteControlHttpTransport.WriteJsonAsync(stream, response.StatusCode, response.Body, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException or OperationCanceledException)
            {
                // A disconnected, untrusted, or timed-out peer has no remaining work.
            }
            catch (Exception)
            {
                try
                {
                    await WriteErrorAsync(stream, HttpStatusCode.BadRequest, "Request unavailable", timeout.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The peer may already have closed its connection.
                }
            }
            finally
            {
                clients.TryRemove(client, out _);
                capacity.Release();
            }
        }
    }

    private static Task WriteErrorAsync(Stream stream, HttpStatusCode status, string message, CancellationToken cancellationToken)
    {
        return RemoteControlHttpTransport.WriteJsonAsync(stream, status, new { error = message }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        if (acceptTask is not null)
        {
            await acceptTask.ConfigureAwait(false);
        }

        foreach (var client in clients.Keys)
        {
            client.Dispose();
        }

        Port = 0;
        certificate.Dispose();
        lifetime.Dispose();
    }
}

public sealed record LocalCompanionRequest(string Method, string Path, JsonElement? Body);

public sealed record LocalCompanionResponse(HttpStatusCode StatusCode, JsonElement Body);
