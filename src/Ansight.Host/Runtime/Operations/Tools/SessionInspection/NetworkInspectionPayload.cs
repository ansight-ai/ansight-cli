using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class NetworkInspectionPayload
{
    // Leave room for the dispatcher envelope below the task bridge's 524,288-character ceiling.
    public const int MaximumSerializedCharacters = 450_000;

    public static JsonObject Summary(SessionNetworkRequest request) => new()
    {
        ["id"] = request.Id,
        ["source"] = request.Source,
        ["method"] = request.Method,
        ["url"] = request.Url,
        ["startedAtUtc"] = request.StartedAtUtc,
        ["completedAtUtc"] = request.CompletedAtUtc,
        ["durationMilliseconds"] = request.DurationMilliseconds,
        ["statusCode"] = request.StatusCode,
        ["errorType"] = request.ErrorType,
        ["errorMessage"] = request.ErrorMessage,
        ["requestBodySizeBytes"] = request.RequestBodySizeBytes,
        ["responseBodySizeBytes"] = request.ResponseBodySizeBytes,
        ["requestBody"] = BodyMetadata(request.RequestBody),
        ["responseBody"] = BodyMetadata(request.ResponseBody)
    };

    public static JsonObject? BodyMetadata(SessionNetworkBody? body) => body is null ? null : new JsonObject
    {
        ["contentType"] = body.ContentType,
        ["encoding"] = body.Encoding,
        ["capturedBytes"] = body.CapturedBytes,
        ["totalBytes"] = body.TotalBytes,
        ["truncated"] = body.Truncated
    };

    public static JsonObject Identity(AppSessionSnapshot snapshot) => new()
    {
        ["sessionId"] = snapshot.SessionId,
        ["appId"] = snapshot.AppId
    };

    public static JsonObject Detail(AppSessionSnapshot snapshot, SessionNetworkRequest request)
    {
        var payload = Identity(snapshot);
        var detail = Summary(request);
        detail["protocol"] = request.Protocol;
        detail["reasonPhrase"] = request.ReasonPhrase;
        var requestHeaders = new JsonArray();
        var responseHeaders = new JsonArray();
        detail["requestHeaders"] = requestHeaders;
        detail["responseHeaders"] = responseHeaders;
        payload["request"] = detail;
        payload["headersTruncated"] = false;
        var serializedCharacters = payload.ToJsonString().Length + 1024;
        if (serializedCharacters > MaximumSerializedCharacters)
        {
            throw new ArgumentException("The network request metadata exceeds the serialized output budget.");
        }

        var omitted = AppendHeaders(request.RequestHeaders, requestHeaders, ref serializedCharacters);
        omitted |= AppendHeaders(request.ResponseHeaders, responseHeaders, ref serializedCharacters);
        payload["headersTruncated"] = omitted;
        return payload;
    }

    private static bool AppendHeaders(IReadOnlyList<SessionNetworkHeader> headers, JsonArray target, ref int serializedCharacters)
    {
        var omitted = false;
        foreach (var header in headers)
        {
            var item = new JsonObject { ["name"] = header.Name, ["value"] = header.Value };
            var size = item.ToJsonString().Length + 1;
            if (size > MaximumSerializedCharacters - serializedCharacters)
            {
                omitted = true;
                continue;
            }

            serializedCharacters += size;
            target.Add(item);
        }

        return omitted;
    }

    public static JsonObject ReadBody(AppSessionSnapshot snapshot, SessionNetworkRequest request, string side, int maximumBytes)
    {
        var payload = Identity(snapshot);
        payload["requestId"] = request.Id;
        payload["side"] = side;
        var body = side == "request" ? request.RequestBody : request.ResponseBody;
        payload["body"] = body is null ? null : BodyPreview(body, maximumBytes);
        return payload;
    }

    private static JsonObject BodyPreview(SessionNetworkBody body, int maximumBytes)
    {
        string data;
        int returnedBytes;
        bool isTruncated;
        if (body.Encoding == "utf8")
        {
            var characterCount = 0;
            returnedBytes = 0;
            while (characterCount < body.Data.Length)
            {
                var status = Rune.DecodeFromUtf16(body.Data.AsSpan(characterCount), out var rune, out var consumed);
                if (status != OperationStatus.Done)
                {
                    throw new ArgumentException("The retained network body contains invalid UTF-8 text.");
                }

                if (rune.Utf8SequenceLength > maximumBytes - returnedBytes)
                {
                    break;
                }

                returnedBytes += rune.Utf8SequenceLength;
                characterCount += consumed;
            }

            data = body.Data[..characterCount];
            isTruncated = characterCount < body.Data.Length;
        }
        else if (body.Encoding == "base64")
        {
            // Capture storage normalizes Base64, so decode only enough complete groups for this preview.
            var encodedCount = Math.Min(body.Data.Length, ((maximumBytes + 2) / 3) * 4);
            byte[] prefix;
            try
            {
                prefix = Convert.FromBase64String(body.Data[..encodedCount]);
            }
            catch (FormatException)
            {
                throw new ArgumentException("The retained network body contains invalid Base64.");
            }

            returnedBytes = Math.Min(prefix.Length, maximumBytes);
            data = Convert.ToBase64String(prefix, 0, returnedBytes);
            isTruncated = encodedCount < body.Data.Length || returnedBytes < prefix.Length;
        }
        else
        {
            throw new ArgumentException("The retained network body has an unsupported encoding.");
        }

        var payload = BodyMetadata(body)!;
        payload["data"] = data;
        payload["returnedByteCount"] = returnedBytes;
        payload["isTruncated"] = isTruncated;
        return payload;
    }

    public static RequestResult Result(JsonObject payload)
    {
        var result = RequestResult.ToolResult(payload, isError: false);
        return result.Payload!.ToJsonString().Length <= MaximumSerializedCharacters
            ? result
            : Error("The network result exceeds the serialized output budget. Narrow the query or request a smaller preview.");
    }

    public static RequestResult Error(string message)
        => RequestResult.ToolResult(new JsonObject { ["message"] = message }, isError: true);
}
