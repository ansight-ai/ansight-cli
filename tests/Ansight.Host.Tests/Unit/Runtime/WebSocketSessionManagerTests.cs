using System.Text.Json.Nodes;
using Ansight.Host.Runtime.WebSocketSessions;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class WebSocketSessionManagerTests
{
    [Fact]
    public void CreateCallToolEnvelope_ClonesCallerOwnedArgumentsAndAfterEvidence()
    {
        var arguments = new JsonObject
        {
            ["includeHidden"] = false
        };
        var after = new JsonObject
        {
            ["include"] = new JsonArray("visualTree")
        };
        var owner = new JsonObject
        {
            ["arguments"] = arguments,
            ["after"] = after
        };

        var envelope = WebSocketSessionManager.CreateCallToolEnvelope(
            "session-123",
            "redpoint.3d_player.list_views",
            arguments,
            after);

        var payload = Assert.IsType<JsonObject>(envelope.Payload);
        var clonedArguments = Assert.IsType<JsonObject>(payload["arguments"]);
        var clonedAfter = Assert.IsType<JsonObject>(payload["after"]);
        Assert.NotSame(arguments, clonedArguments);
        Assert.NotSame(after, clonedAfter);
        Assert.Same(owner, arguments.Parent);
        Assert.Same(owner, after.Parent);
        Assert.False(clonedArguments["includeHidden"]?.GetValue<bool>());
        Assert.Equal("visualTree", clonedAfter["include"]?[0]?.GetValue<string>());
    }
}
