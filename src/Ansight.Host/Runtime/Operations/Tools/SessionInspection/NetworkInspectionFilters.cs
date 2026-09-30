using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class NetworkInspectionFilters
{
    private static readonly string[] names = ["startUtc", "endUtc", "methods", "statuses", "host", "query", "failedOnly"];

    private NetworkInspectionFilters(JsonObject? arguments)
    {
        StartUtc = NetworkInspectionArguments.ReadTimestamp(arguments, "startUtc");
        EndUtc = NetworkInspectionArguments.ReadTimestamp(arguments, "endUtc");
        if (StartUtc > EndUtc)
        {
            throw new ArgumentException("endUtc must be greater than or equal to startUtc.");
        }

        Methods = NetworkInspectionArguments.ReadValues(arguments, "methods");
        Statuses = NetworkInspectionArguments.ReadValues(arguments, "statuses", statuses: true);
        Host = NetworkInspectionArguments.ReadString(arguments, "host")?.ToLowerInvariant();
        Query = NetworkInspectionArguments.ReadString(arguments, "query")?.ToLowerInvariant();
        FailedOnly = NetworkInspectionArguments.ReadBoolean(arguments, "failedOnly");
    }

    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtc { get; }
    public string[] Methods { get; }
    public string[] Statuses { get; }
    public string? Host { get; }
    public string? Query { get; }
    public bool FailedOnly { get; }

    public static NetworkInspectionFilters Read(JsonObject? arguments) => new(arguments);

    public bool Matches(SessionNetworkRequest request)
        => (!StartUtc.HasValue || request.StartedAtUtc >= StartUtc.Value)
           && (!EndUtc.HasValue || request.StartedAtUtc <= EndUtc.Value)
           && (Methods.Length == 0 || Methods.Contains(request.Method, StringComparer.OrdinalIgnoreCase))
           && (Statuses.Length == 0 || Statuses.Any(status => SessionNetworkRequestFilter.MatchesStatus(request, status)))
           && (Host is null || SessionNetworkRequestFilter.MatchesHost(request.Url, Host))
           && (Query is null || SessionNetworkRequestFilter.MatchesQuery(request, Query))
           && (!FailedOnly || SessionNetworkRequestFilter.IsFailed(request));

    public JsonObject ToJson() => new()
    {
        ["startUtc"] = StartUtc?.ToString("O", CultureInfo.InvariantCulture),
        ["endUtc"] = EndUtc?.ToString("O", CultureInfo.InvariantCulture),
        ["methods"] = new JsonArray(Methods.Select(method => (JsonNode?)JsonValue.Create(method)).ToArray()),
        ["statuses"] = new JsonArray(Statuses.Select(status => int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? (JsonNode?)JsonValue.Create(code) : JsonValue.Create(status)).ToArray()),
        ["host"] = Host,
        ["query"] = Query,
        ["failedOnly"] = FailedOnly
    };

    public void ValidateContinuation(JsonObject? arguments)
    {
        // Omitted filters inherit the snapshot. Supplied filters must mean the same thing.
        var effective = ToJson();
        foreach (var name in names)
        {
            if (arguments?.ContainsKey(name) == true)
            {
                effective[name] = arguments[name]?.DeepClone();
            }
        }

        if (!JsonNode.DeepEquals(ToJson(), Read(effective).ToJson()))
        {
            throw new ArgumentException("The cursor filters conflict with the initial network query.");
        }
    }
}
