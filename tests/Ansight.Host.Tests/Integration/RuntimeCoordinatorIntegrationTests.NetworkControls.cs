using System.Net.Http.Json;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 30000)]
    public async Task NetworkPanelRoutesShareHostLeaseValidationAndIdempotentReset()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        using var client = new HttpClient { BaseAddress = Assert.IsType<Uri>(explorer.ExplorerUrl) };

        using var missing = await client.PostAsJsonAsync("api/network/get", new { leaseId = "missing-panel-lease" });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Contains("absent or expired", await missing.Content.ReadAsStringAsync());

        using var reset = await client.PostAsJsonAsync("api/network/reset", new { leaseId = "missing-panel-lease" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains("stopped", await reset.Content.ReadAsStringAsync());

        using var untargeted = await client.PostAsJsonAsync("api/network/set", new { profile = new { blocked = true } });
        Assert.Equal(HttpStatusCode.Conflict, untargeted.StatusCode);

        using var unknown = await client.PostAsJsonAsync("api/network/unrecognized", new { });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
