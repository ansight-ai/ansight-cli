using System.Globalization;

namespace Ansight.Host.Models.Session;

/// <summary>Common retained-request matching rules for host inspection and CLI queries.</summary>
public static class SessionNetworkRequestFilter
{
    public static bool IsFailed(SessionNetworkRequest request)
        => request.StatusCode is >= 400 || request.ErrorType is not null;

    public static bool MatchesStatus(SessionNetworkRequest request, string filter)
    {
        var normalized = filter.Trim().ToLowerInvariant();
        if (normalized is "failed" or "error")
        {
            return IsFailed(request);
        }

        if (normalized.Length == 3 && normalized[1] == 'x' && normalized[2] == 'x'
            && char.IsDigit(normalized[0]))
        {
            return request.StatusCode / 100 == normalized[0] - '0';
        }

        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode)
               && request.StatusCode == statusCode;
    }

    public static bool MatchesHost(string url, string host)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Contains(host.Trim(), StringComparison.OrdinalIgnoreCase)
            : url.Contains(host.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool MatchesQuery(SessionNetworkRequest request, string query)
        => request.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
           || request.Method.Contains(query, StringComparison.OrdinalIgnoreCase)
           || request.ErrorMessage?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
