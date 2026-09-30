using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Ansight.Cli.Commands.Update;
using Ansight.Host;

namespace Ansight.Cli;

/// <summary>
/// Stable loopback entry point. Only assets and account recovery are served without access;
/// product requests stream to a runtime whose lease also bounds the entire request.
/// </summary>
internal sealed class CliLocalHostServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpListener listener = new();
    private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly CancellationTokenSource shutdown;
    private readonly CliLocalHostAccess access;
    private readonly Func<Uri, CancellationToken, Task> signIn;
    private readonly ConcurrentDictionary<long, Task> requests = new();
    private readonly object loginGate = new();
    private Task loginTask = Task.CompletedTask;
    private string? loginError;
    private long nextRequest;
    private readonly Task serverTask;

    public CliLocalHostServer(int port, string? path, CliLocalHostAccess access,
        Func<Uri, CancellationToken, Task> signIn, CancellationToken cancellationToken)
    {
        this.access = access;
        this.signIn = signIn;
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (port == 0)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var segment = path?.Trim('/') ?? "";
        Url = new Uri($"http://127.0.0.1:{port}/{(segment.Length == 0 ? "" : segment + "/")}");
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        serverTask = RunAsync();
    }

    public Uri Url { get; }

    private async Task RunAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(shutdown.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref nextRequest);
                var task = HandleSafelyAsync(context);
                requests[id] = task;
                _ = task.ContinueWith(_ => { requests.TryRemove(id, out var ignored); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (HttpListenerException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context)
    {
        try { await HandleAsync(context, shutdown.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException
            or HttpListenerException or IOException or ObjectDisposedException)
        {
            context.Response.Abort();
        }
        finally { context.Response.Close(); }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob: https://*.mapbox.com; script-src 'self' 'wasm-unsafe-eval' https://api.mapbox.com; style-src 'self' 'unsafe-inline' https://api.mapbox.com; connect-src 'self' blob: https://api.mapbox.com https://events.mapbox.com; worker-src 'self' blob:; child-src blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        if (request.Url is null || request.Url.Authority != Url.Authority
            || !request.Url.AbsolutePath.StartsWith(Url.AbsolutePath, StringComparison.Ordinal))
        {
            response.StatusCode = 404;
            return;
        }
        var route = request.Url.AbsolutePath[Url.AbsolutePath.Length..];
        var isRead = request.HttpMethod is "GET" or "HEAD";
        var contentType = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        var isArchive = route == "api/sessions/import" && contentType is "application/zip" or "application/octet-stream";
        if (request.HttpMethod == "POST" &&
            (contentType != "application/json" && !isArchive
             || request.Headers["Origin"] is { } origin && origin != Url.GetLeftPart(UriPartial.Authority)
             || request.Headers["Sec-Fetch-Site"] == "cross-site"))
        {
            response.StatusCode = 403;
            return;
        }
        if (isRead && route == "api/access")
        {
            var state = access.State;
            var identity = CliReleaseIdentity.Current;
            bool pending;
            string? error;
            lock (loginGate) { pending = !loginTask.IsCompleted; error = loginError; }
            await WriteJsonAsync(response, new { state.Decision.IsAuthorized, state.Decision.Reason,
                loginPending = pending, loginError = error, cliVersion = identity.Version,
                cliBuildNumber = identity.BuildNumber }, request.HttpMethod == "HEAD", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.HttpMethod == "POST" && route is "api/access/sign-in" or "api/access/recheck")
        {
            if (route == "api/access/sign-in")
            {
                lock (loginGate)
                {
                    if (loginTask.IsCompleted)
                    {
                        loginError = null;
                        loginTask = Task.Run(LoginAsync, shutdown.Token);
                    }
                }
            }
            else access.RequestRetry();
            response.StatusCode = 202;
            await WriteJsonAsync(response, new { isSuccess = true }, false, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (isRead && !route.Contains('/') &&
            (route is "" or "index.html" || route.StartsWith("session-replay", StringComparison.Ordinal)))
        {
            var name = route is "" or "index.html" ? "session-replay.html" : route;
            await using var asset = typeof(RuntimeCoordinator).Assembly.GetManifestResourceStream("Ansight.Host.Replay.Assets." + name);
            if (asset is null) { response.StatusCode = 404; return; }
            response.ContentType = Path.GetExtension(name) switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8", ".wasm" => "application/wasm",
                ".ttf" => "font/ttf", ".woff2" => "font/woff2", ".woff" => "font/woff",
                ".png" => "image/png", _ => "application/octet-stream"
            };
            response.ContentLength64 = asset.Length;
            if (request.HttpMethod != "HEAD") await asset.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
            return;
        }
        var current = access.State;
        if (!current.Decision.IsAuthorized || current.ExplorerUrl is null || current.Lease is null)
        {
            response.StatusCode = current.Decision.Reason == "authentication_required" ? 401
                : current.Decision.Reason == "product_access_required" ? 403 : 503;
            await WriteJsonAsync(response, new { error = current.Decision.Reason }, request.HttpMethod == "HEAD", cancellationToken).ConfigureAwait(false);
            return;
        }
        // UriBuilder preserves the trusted upstream authority even for unusual request paths.
        var upstream = new UriBuilder(current.ExplorerUrl)
        {
            Path = current.ExplorerUrl.AbsolutePath + route, Query = request.Url.Query
        }.Uri;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Lease.Token);
        using var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), upstream);
        if (request.HasEntityBody)
        {
            message.Content = new StreamContent(request.InputStream);
            if (request.ContentType is not null) message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            if (request.ContentLength64 >= 0) message.Content.Headers.ContentLength = request.ContentLength64;
        }
        foreach (var header in new[] { "Range", "If-None-Match", "If-Modified-Since" })
            if (request.Headers[header] is { } value) message.Headers.TryAddWithoutValidation(header, value);
        using var result = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, lifetime.Token).ConfigureAwait(false);
        response.StatusCode = (int)result.StatusCode;
        foreach (var header in result.Headers.Concat(result.Content.Headers))
        {
            if (header.Key is "Transfer-Encoding" or "Connection" or "Keep-Alive" or "Content-Length") continue;
            response.Headers[header.Key] = string.Join(", ", header.Value);
        }
        if (result.Content.Headers.ContentLength is { } length) response.ContentLength64 = length;
        else response.SendChunked = true;
        if (request.HttpMethod != "HEAD")
        {
            await using var body = await result.Content.ReadAsStreamAsync(lifetime.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await body.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false)) > 0)
            {
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, count), lifetime.Token).ConfigureAwait(false);
                await response.OutputStream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task LoginAsync()
    {
        try
        {
            await signIn(Url, shutdown.Token).ConfigureAwait(false);
            access.RequestRetry();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (loginGate) loginError = GetLoginErrorMessage(exception);
        }
    }

    private static string GetLoginErrorMessage(Exception exception)
    {
        const string fallback = "Sign-in did not complete. Please try again.";
        if (exception is not InvalidOperationException || string.IsNullOrWhiteSpace(exception.Message))
        {
            return fallback;
        }

        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 300 ? message : fallback;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value, bool isHead, CancellationToken token)
    {
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions);
        response.ContentLength64 = bytes.Length;
        if (!isHead) await response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        listener.Close();
        await serverTask.ConfigureAwait(false);
        await Task.WhenAll(requests.Values).ConfigureAwait(false);
        await loginTask.ConfigureAwait(false);
        client.Dispose();
        shutdown.Dispose();
    }
}
