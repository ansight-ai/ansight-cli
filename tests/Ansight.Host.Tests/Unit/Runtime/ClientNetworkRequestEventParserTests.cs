namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ClientNetworkRequestEventParserTests
{
    [Fact]
    public void ParseText_ParsesAndRedactsNetworkRequest()
    {
        var payload = """
                      {
                        "type": "CLIENT_NETWORK_REQUEST",
                        "request": {
                          "schema": "ansight.network-request.v1",
                          "id": "request-001",
                          "source": "dotnet.httpclient",
                          "startedAtUtc": "2026-08-23T00:00:00Z",
                          "completedAtUtc": "2026-08-23T00:00:00.125Z",
                          "durationMilliseconds": 125,
                          "method": "get",
                          "url": "https://blob.test/orders?sv=1&sp=rw&se=tomorrow&sig=azure-secret&view=full",
                          "protocol": "2.0",
                          "requestHeaders": [
                            { "name": "Authorization", "value": "Bearer secret" },
                            { "name": "Accept", "value": "application/json" }
                          ],
                          "requestBody": {
                            "contentType": "application/json",
                            "encoding": "utf8",
                            "data": "{\"token\":\"body-secret\",\"visible\":\"yes\"}",
                            "capturedBytes": 39,
                            "totalBytes": 39,
                            "truncated": false
                          },
                          "statusCode": 200,
                          "errorMessage": "retry https://example.test/orders?token=error-secret",
                          "responseHeaders": []
                        }
                      }
                      """;

        var parsed = ClientEventParser.ParseText(payload);

        Assert.Equal(WebSocketClientEventConstants.ClientNetworkRequest, parsed.Type);
        var request = Assert.IsType<SessionNetworkRequest>(parsed.NetworkRequest);
        Assert.Equal("request-001", request.Id);
        Assert.Equal("GET", request.Method);
        Assert.Equal(200, request.StatusCode);
        Assert.Equal(125, request.DurationMilliseconds);
        Assert.DoesNotContain("azure-secret", request.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("tomorrow", request.Url, StringComparison.Ordinal);
        Assert.Contains("view=full", request.Url, StringComparison.Ordinal);
        Assert.Equal("<redacted>", request.RequestHeaders[0].Value);
        Assert.Equal("application/json", request.RequestHeaders[1].Value);
        Assert.DoesNotContain("error-secret", request.ErrorMessage, StringComparison.Ordinal);
        Assert.NotNull(request.RequestBody);
        Assert.DoesNotContain("body-secret", request.RequestBody.Data, StringComparison.Ordinal);
        Assert.Contains("visible", request.RequestBody.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseText_RejectsUnknownNetworkSchema()
    {
        var payload = """
                      {
                        "type": "CLIENT_NETWORK_REQUEST",
                        "request": {
                          "schema": "ansight.network-request.v2",
                          "id": "request-001",
                          "source": "test",
                          "startedAtUtc": "2026-08-23T00:00:00Z",
                          "completedAtUtc": "2026-08-23T00:00:01Z",
                          "durationMilliseconds": 1000,
                          "method": "GET",
                          "url": "https://example.test"
                        }
                      }
                      """;

        var parsed = ClientEventParser.ParseText(payload);

        Assert.Null(parsed.NetworkRequest);
    }

    [Fact]
    public void ParseText_PreservesRawNetworkValuesWhenRedactionIsExplicitlyDisabled()
    {
        var payload = """
                      {
                        "type": "CLIENT_NETWORK_REQUEST",
                        "request": {
                          "schema": "ansight.network-request.v1",
                          "id": "request-raw",
                          "source": "apple.nsurlprotocol",
                          "startedAtUtc": "2026-08-23T00:00:00Z",
                          "completedAtUtc": "2026-08-23T00:00:00.125Z",
                          "durationMilliseconds": 125,
                          "method": "GET",
                          "url": "https://example.test/orders?access_token=raw-token",
                          "redactSensitiveData": false,
                          "requestHeaders": [
                            { "name": "Authorization", "value": "Bearer raw-token" }
                          ],
                          "requestBody": {
                            "contentType": "application/json",
                            "encoding": "utf8",
                            "data": "{\"token\":\"raw-token\"}",
                            "capturedBytes": 21,
                            "totalBytes": 21,
                            "truncated": false
                          }
                        }
                      }
                      """;

        var request = Assert.IsType<SessionNetworkRequest>(
            ClientEventParser.ParseText(payload).NetworkRequest);

        Assert.False(request.RedactSensitiveData);
        Assert.Contains("access_token=raw-token", request.Url, StringComparison.Ordinal);
        Assert.Equal("Bearer raw-token", request.RequestHeaders[0].Value);
        Assert.Contains("raw-token", request.RequestBody!.Data, StringComparison.Ordinal);
    }
}
