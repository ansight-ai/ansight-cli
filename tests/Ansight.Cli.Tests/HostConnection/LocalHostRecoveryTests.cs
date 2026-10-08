using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Notifications;

namespace Ansight.Cli.Tests.HostConnection;

public sealed class LocalHostRecoveryTests
{
    [Theory(Timeout = 15000)]
    [InlineData("application/x-ndjson")]
    [InlineData("text/event-stream")]
    public async Task AuthorizedGatewayPreservesStreamNegotiationAndForwardsProgressBeforeCompletion(string mediaType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var upstream = new HttpListener();
        var upstreamUrl = new Uri($"http://127.0.0.1:{port}/source/");
        upstream.Prefixes.Add(upstreamUrl.AbsoluteUri);
        upstream.Start();
        await using var lease = await CliAccessLease.CreateAsync(TestAccessAuthorizer.Allow, CancellationToken.None);
        var access = new CliLocalHostAccess();
        access.Allow(upstreamUrl, lease);
        await using var server = new CliLocalHostServer(0, "/player/", access, (_, _) => Task.CompletedTask, CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.Url };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sessions/example/annotation-summary")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd(mediaType);
        var requestTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var context = await upstream.GetContextAsync().WaitAsync(timeout.Token);
        Assert.Equal(mediaType, context.Request.Headers["Accept"]);
        context.Response.ContentType = mediaType;
        context.Response.SendChunked = true;
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("progress\n"), timeout.Token);
        await context.Response.OutputStream.FlushAsync(timeout.Token);

        using var response = await requestTask.WaitAsync(timeout.Token);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("progress", await reader.ReadLineAsync(timeout.Token));
        // The upstream response remains open until the client has received progress.
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("complete\n"), timeout.Token);
        context.Response.Close();
        Assert.Equal("complete", await reader.ReadLineAsync(timeout.Token));
        Assert.Null(await reader.ReadLineAsync(timeout.Token));
    }

    [Fact]
    public async Task AuthorizedGatewayStreamsBinaryUploadsAndPreservesTheResponse()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var upstream = new HttpListener();
        var upstreamUrl = new Uri($"http://127.0.0.1:{port}/source/");
        upstream.Prefixes.Add(upstreamUrl.AbsoluteUri);
        upstream.Start();
        await using var lease = await CliAccessLease.CreateAsync(TestAccessAuthorizer.Allow, CancellationToken.None);
        var access = new CliLocalHostAccess();
        access.Allow(upstreamUrl, lease);
        await using var server = new CliLocalHostServer(0, "/player/", access, (_, _) => Task.CompletedTask, CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.Url };
        using var upload = new ByteArrayContent([1, 2, 3]);
        upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        var requestTask = client.PostAsync("api/sessions/import", upload);
        var context = await upstream.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("/source/api/sessions/import", context.Request.Url!.AbsolutePath);
        Assert.Equal("application/zip", context.Request.ContentType);
        Assert.Equal(3, context.Request.ContentLength64);
        using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body);
        Assert.Equal(new byte[] { 1, 2, 3 }, body.ToArray());
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";
        var reply = Encoding.UTF8.GetBytes("{\"error\":\"invalid_test_archive\"}");
        context.Response.ContentLength64 = reply.Length;
        await context.Response.OutputStream.WriteAsync(reply);
        context.Response.Close();
        using var result = await requestTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("invalid_test_archive", JsonNode.Parse(await result.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("authentication_required")]
    [InlineData("authorization_unavailable")]
    [InlineData("checking")]
    public async Task AuthenticationStatesRefreshWithoutPushNotifications(string reason)
    {
        var sink = new RecordingNotificationSink();
        var notifications = new CliHostAccessNotifications(sink);
        await notifications.UpdateAsync(new AccessDecision(false, reason), CancellationToken.None);
        await notifications.UpdateAsync(new AccessDecision(false, reason), CancellationToken.None);
        Assert.Empty(sink.Notifications);
        Assert.Single(sink.Removed);
    }

    [Fact]
    public async Task ProductAccessNotificationIsRemovedWhenAuthenticationNeedsRefreshing()
    {
        var sink = new RecordingNotificationSink();
        var notifications = new CliHostAccessNotifications(sink);
        await notifications.UpdateAsync(AccessDecision.ProductAccessRequired, CancellationToken.None);
        await notifications.UpdateAsync(AccessDecision.ProductAccessRequired, CancellationToken.None);
        Assert.Single(sink.Notifications);
        await notifications.UpdateAsync(AccessDecision.AuthenticationRequired, CancellationToken.None);
        Assert.Single(sink.Removed);
        Assert.Single(sink.Notifications);
        await notifications.UpdateAsync(AccessDecision.ProductAccessRequired, CancellationToken.None);
        Assert.Equal(2, sink.Notifications.Count);
        await notifications.UpdateAsync(TestAccessAuthorizer.Active(), CancellationToken.None);
        Assert.Equal(2, sink.Removed.Count);
    }

    [Fact]
    public async Task DeniedAccessIsCachedForFiveMinutesButManualRecheckIsImmediate()
    {
        var clock = new ManualTimeProvider();
        var access = new CliLocalHostAccess();
        access.Block(AccessDecision.ProductAccessRequired);
        var retry = access.WaitForRetryAsync(clock, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(retry.IsCompleted);
        access.RequestRetry();
        await retry.WaitAsync(TimeSpan.FromSeconds(5));

        retry = access.WaitForRetryAsync(clock, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TemporaryAuthorizationFailureRetriesAfterFifteenSeconds()
    {
        var clock = new ManualTimeProvider();
        var access = new CliLocalHostAccess();
        access.Block(AccessDecision.Unavailable);
        var retry = access.WaitForRetryAsync(clock, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(retry.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AccessEndpointReportsVerificationInProgressUntilCheckCompletes()
    {
        var access = new CliLocalHostAccess();
        access.Block(AccessDecision.Unavailable);
        await using var server = new CliLocalHostServer(0, "/recovery/", access,
            (_, _) => Task.CompletedTask, CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.Url };
        access.BeginCheck();
        var checking = JsonNode.Parse(await client.GetStringAsync("api/access"))!;
        Assert.Equal("checking", checking["reason"]!.GetValue<string>());
        Assert.False(checking["isAuthorized"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(checking["cliVersion"]!.GetValue<string>()));
        Assert.True(checking["cliBuildNumber"]!.GetValue<long>() >= 0);
        access.Block(AccessDecision.AuthenticationRequired);
        var completed = JsonNode.Parse(await client.GetStringAsync("api/access"))!;
        Assert.Equal("authentication_required", completed["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task RecoveryLoginIsSingleFlightAndRejectsCrossOriginRequests()
    {
        var access = new CliLocalHostAccess();
        access.Block(AccessDecision.AuthenticationRequired);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var server = new CliLocalHostServer(0, "/recovery/", access, async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
        }, CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.Url };
        using var hostile = new HttpRequestMessage(HttpMethod.Post, "api/access/sign-in")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        hostile.Headers.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(hostile)).StatusCode);
        Assert.Equal(0, calls);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("api/access/sign-in",
                new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        }
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls);
        var status = JsonNode.Parse(await client.GetStringAsync("api/access"))!;
        Assert.True(status["loginPending"]!.GetValue<bool>());
        Assert.False(status["isAuthorized"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/sessions")).StatusCode);
        finish.SetResult();
    }

    [Fact]
    public async Task RecoveryLoginReportsAnActionableRegistrationFailure()
    {
        var access = new CliLocalHostAccess();
        access.Block(AccessDecision.AuthenticationRequired);
        await using var server = new CliLocalHostServer(0, "/recovery/", access,
            (_, _) => Task.FromException(new InvalidOperationException(
                "This subscription supports up to 3 registered machines.")),
            CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.Url };

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("api/access/sign-in",
            new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);

        JsonNode? status = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            status = JsonNode.Parse(await client.GetStringAsync("api/access"));
            if (status?["loginPending"]?.GetValue<bool>() == false) break;
            await Task.Delay(20);
        }

        Assert.NotNull(status);
        Assert.False(status["loginPending"]!.GetValue<bool>());
        Assert.Equal(
            "This subscription supports up to 3 registered machines.",
            status["loginError"]!.GetValue<string>());
    }

    private sealed class RecordingNotificationSink : INotificationSink
    {
        public List<Notification> Notifications { get; } = [];
        public List<string> Removed { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string identifier, CancellationToken cancellationToken = default)
        {
            Removed.Add(identifier);
            return Task.CompletedTask;
        }
    }
}
