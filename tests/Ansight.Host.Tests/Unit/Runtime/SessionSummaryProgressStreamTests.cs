using Ansight.Host.Explorer;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionSummaryProgressStreamTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryStream_SendsLoadingAndStageHeartbeatsBeforeTheResult(bool fail)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        var address = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(address.ToString());
        listener.Start();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        Assert.Equal("application/x-ndjson", context.Request.Headers["Accept"]);
        Assert.True(context.Request.AcceptTypes?.Contains("application/x-ndjson") == true,
            $"AcceptTypes: {string.Join(",", context.Request.AcceptTypes ?? [])}");
        var allowEvidence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ExplorerServer.StreamSummaryOperationAsync(context.Response, async report =>
        {
            await allowEvidence.Task.WaitAsync(timeout.Token);
            report(new("analysis", "Analysing section with 3 screenshots…"));
            await allowCompletion.Task.WaitAsync(timeout.Token);
            if (fail) throw new InvalidOperationException("Provider unavailable.");
            return new { isSuccess = true, comment = "Generated annotation." };
        }, timeout.Token);

        try
        {
            using var response = await responseTask.WaitAsync(timeout.Token);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var loading = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
            Assert.Equal("loading", loading["progress"]?["stage"]?.GetValue<string>());
            Assert.False(server.IsCompleted);

            allowEvidence.SetResult();
            var analysing = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
            Assert.Equal("analysis", analysing["progress"]?["stage"]?.GetValue<string>());
            var heartbeat = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
            Assert.Equal("analysis", heartbeat["progress"]?["stage"]?.GetValue<string>());
            Assert.False(server.IsCompleted);

            allowCompletion.SetResult();
            var terminal = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
            Assert.Equal(fail ? "error" : "success", terminal["status"]?.GetValue<string>());
            if (fail) Assert.Equal("Provider unavailable.", terminal["message"]?.GetValue<string>());
            else Assert.Equal("Generated annotation.", terminal["result"]?["comment"]?.GetValue<string>());
            Assert.Null(await reader.ReadLineAsync(timeout.Token));
        }
        finally
        {
            allowEvidence.TrySetResult();
            allowCompletion.TrySetResult();
            await server;
        }
    }
}
