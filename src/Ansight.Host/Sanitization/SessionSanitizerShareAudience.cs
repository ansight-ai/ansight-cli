using System.Text.Json.Serialization;

namespace Ansight.Host.Sanitization;

[JsonConverter(typeof(JsonStringEnumConverter<SessionSanitizerShareAudience>))]
public enum SessionSanitizerShareAudience
{
    [JsonStringEnumMemberName("team")]
    Team,

    [JsonStringEnumMemberName("public_with_auth")]
    PublicWithSignIn,

    [JsonStringEnumMemberName("public_no_auth")]
    Public
}
