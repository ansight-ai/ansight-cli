namespace Ansight.Host.Sessions;

public sealed record SessionOptimizationProgress(string Message, int? Completed = null, int? Total = null);
