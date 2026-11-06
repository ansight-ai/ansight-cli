namespace Ansight.Host.Replay;

public sealed record SessionConnectionDiagnostic(string Code, string Message, string Recovery, string? Candidates);
