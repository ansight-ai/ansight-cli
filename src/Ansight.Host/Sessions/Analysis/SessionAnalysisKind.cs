namespace Ansight.Host.Models.Session;

public static class SessionAnalysisKind
{
    public const string General = "General";
    public const string FlowChart = "FlowChart";

    public static string Normalize(string? value)
    {
        return string.Equals(value, FlowChart, StringComparison.OrdinalIgnoreCase)
            ? FlowChart
            : General;
    }

    public static string ToDisplayName(string? value)
    {
        return Normalize(value) == FlowChart
            ? "Flow Chart"
            : "Analysis";
    }
}
