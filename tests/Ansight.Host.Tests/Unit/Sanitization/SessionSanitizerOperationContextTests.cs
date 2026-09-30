using System.Text.Json;

namespace Ansight.Host.Tests.Unit.Sanitization;

public sealed class SessionSanitizerOperationContextTests
{
    [Theory]
    [InlineData(SessionSanitizerShareAudience.Team, "team")]
    [InlineData(SessionSanitizerShareAudience.PublicWithSignIn, "public_with_auth")]
    [InlineData(SessionSanitizerShareAudience.Public, "public_no_auth")]
    public void ShareAudience_SerializesToWorkspaceContract(
        SessionSanitizerShareAudience audience,
        string expected)
    {
        var teamId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var context = new SessionSanitizerOperationContext
        {
            Kind = "share",
            Share = new SessionSanitizerShareContext
            {
                Audience = audience,
                TeamId = teamId
            }
        };

        var node = JsonSerializer.SerializeToNode(
            context,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Equal("share", node?["kind"]?.GetValue<string>());
        Assert.Equal(expected, node?["share"]?["audience"]?.GetValue<string>());
        Assert.Equal(teamId.ToString(), node?["share"]?["teamId"]?.GetValue<string>());
    }
}
