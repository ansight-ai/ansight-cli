namespace Ansight.Host.Sessions;

public sealed record SessionTimelineTrimProgress(string Message, int? Completed = null, int? Total = null);
