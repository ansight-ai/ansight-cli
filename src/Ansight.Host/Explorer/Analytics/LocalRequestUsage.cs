using System.Diagnostics;
using System.Net;
using Ansight.Analytics;

namespace Ansight.Host.Explorer.Analytics;

// Transport measurements only: a 2xx can acknowledge asynchronous work, not complete it.
internal sealed class LocalRequestUsage : IDisposable
{
    private readonly ProductAnalytics analytics;
    private readonly string? accountId;
    private readonly string? feature;
    private readonly HttpListenerResponse response;
    private readonly Stopwatch timer = Stopwatch.StartNew();
    public LocalRequestUsage(ProductAnalytics analytics, string route, string method, HttpListenerResponse response)
    {
        this.analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
        accountId = AnalyticsAccount.Read(analytics.DataDirectoryPath);
        this.response = response;
        feature = Classify(route, method);
    }
    internal static string? Classify(string route, string method)
    {
        // Match route shape locally; never transmit route segments, queries, IDs or request/response bodies.
        var segments = route.Split('/');
        if (segments.Length < 2 || segments[0] != "api") return null;
        if (method == "GET")
            return segments is ["api", "sessions", _, "export"] ? "export"
                : segments is ["api", "sessions", _, "files", "content"] ? "files"
                : segments is ["api", "sessions", _, "artifacts", "content"] ? "artifacts" : null;
        if (method != "POST") return null;
        return segments[1] switch
        {
            "devices" => "device", "location" => "location", "task-extractions" => "task_extract",
            "app-graph-recordings" => "graph_recording", "tests" => "test", "apps" => "app",
            "cloud" => "cloud",
            "sessions" when segments is ["api", "sessions", "import"] => "import",
            "sessions" when segments.LastOrDefault() == "share" => "share",
            _ => null
        };
    }
    public void Dispose()
    {
        if (feature is null) return;
        try
        {
            using var actor = AnalyticsAccount.Capture(analytics.DataDirectoryPath, accountId);
            analytics.RecordUsage(feature, "local_api",
                response.StatusCode >= 500 ? "failed" : response.StatusCode >= 400 ? "blocked" : "observed",
                timer.Elapsed.TotalSeconds);
        }
        catch { /* A closed HTTP response cannot affect the completed request. */ }
    }
}
