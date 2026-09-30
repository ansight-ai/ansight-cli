namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class MauiVisualTreeRootScope
{
    public const string CurrentPage = "currentPage";
    public const string RootPage = "rootPage";
    public const string Window = "window";
    public const string Default = CurrentPage;

    public const string ValidationMessage =
        "MAUI root must be one of: currentPage, rootPage, window.";

    public static bool TryNormalize(string? value, out string rootScope)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            rootScope = Default;
            return true;
        }

        var key = value.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        switch (key)
        {
            case "root":
            case "currentpage":
                rootScope = CurrentPage;
                return true;
            case "rootpage":
                rootScope = RootPage;
                return true;
            case "window":
                rootScope = Window;
                return true;
            default:
                rootScope = Default;
                return false;
        }
    }
}
