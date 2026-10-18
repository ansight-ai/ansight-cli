using System.Text.Json;
using Ansight.Host.Activity;

namespace Ansight.Cli.Tests.HostConnection;

public sealed class ActivityCallerTests
{
    [Fact]
    public void LegacyControlRequestsDefaultToUnknownCaller()
    {
        var request = JsonSerializer.Deserialize<ControlRequest>("""{"schema":"ansight.control.request/v1","arguments":["session","show","id"],"secretValue":null}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(request);
        Assert.Null(request.Caller);
        Assert.Equal("unknown", ActivityCaller.Declared(request.Caller).Kind);
    }

    [Fact]
    public void ForwardingPreservesDeclaredClientAndWorkButCannotPromoteItsIdentity()
    {
        var caller = new ActivityCaller
        {
            Kind = "agent", Name = "codex", Ingress = "mcp", ClientName = "external-client", WorkId = "chat-id",
            PrincipalId = "forged", PrincipalAttribution = "authenticated", ExecutorAttribution = "host-generated"
        };
        var original = new ControlRequest(ControlProtocol.RequestSchema, ["session", "show", "id"], null, Caller: caller);
        var request = JsonSerializer.Deserialize<ControlRequest>(JsonSerializer.Serialize(original))!;
        var received = ActivityCaller.Declared(request.Caller);
        Assert.Equal("agent", received.Kind);
        Assert.Equal("mcp", received.Ingress);
        Assert.Equal("external-client", received.ClientName);
        Assert.Equal("chat-id", received.WorkId);
        Assert.Equal("caller-declared", received.ExecutorAttribution);
        Assert.Null(received.PrincipalId);
    }
}
