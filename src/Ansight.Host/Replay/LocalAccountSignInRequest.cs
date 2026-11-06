
namespace Ansight.Host.Replay;

public sealed record LocalAccountSignInRequest(
    string Email,
    string Secret,
    string Mode = "password");
