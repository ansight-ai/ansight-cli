using System.Net;
using System.Net.Http.Headers;

namespace Ansight.Host.Explorer;

internal sealed record HttpFileRange(HttpStatusCode StatusCode, long Offset, long Length)
{
    public static HttpFileRange Resolve(string? rangeHeader, long fileLength)
    {
        // Multiple ranges are optional; serve the complete file when they or an invalid header are supplied.
        if (!RangeHeaderValue.TryParse(rangeHeader, out var range)
            || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || range.Ranges.Count != 1)
        {
            return new HttpFileRange(HttpStatusCode.OK, 0, fileLength);
        }

        var requested = range.Ranges.Single();
        if (fileLength == 0 || requested.From >= fileLength || (requested.From is null && requested.To == 0))
        {
            return new HttpFileRange(HttpStatusCode.RequestedRangeNotSatisfiable, 0, 0);
        }

        var offset = requested.From ?? Math.Max(0, fileLength - requested.To!.Value);
        var end = requested.From is null ? fileLength - 1 : Math.Min(requested.To ?? fileLength - 1, fileLength - 1);
        return new HttpFileRange(HttpStatusCode.PartialContent, offset, end - offset + 1);
    }

    public void Apply(HttpListenerResponse response, long fileLength)
    {
        response.StatusCode = (int)StatusCode;
        response.ContentLength64 = Length;
        response.Headers["Accept-Ranges"] = "bytes";
        if (StatusCode == HttpStatusCode.PartialContent)
        {
            response.Headers["Content-Range"] = $"bytes {Offset}-{Offset + Length - 1}/{fileLength}";
        }
        else if (StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Headers["Content-Range"] = $"bytes */{fileLength}";
        }
    }
}
