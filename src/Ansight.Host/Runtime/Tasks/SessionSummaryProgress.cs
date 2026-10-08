namespace Ansight.Host.Runtime.Tasks;

internal sealed record SessionSummaryProgress(string Stage, string Message, int? Completed = null, int? Total = null);
