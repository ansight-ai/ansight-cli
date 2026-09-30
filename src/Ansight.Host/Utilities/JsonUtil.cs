namespace Ansight.Host.Utilities;

internal static class JsonUtil
{
    public const int MaximumDepth = 256;

    public static readonly JsonDocumentOptions Document = new()
    {
        MaxDepth = MaximumDepth
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = MaximumDepth
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = MaximumDepth,
        WriteIndented = true
    };

    public static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
