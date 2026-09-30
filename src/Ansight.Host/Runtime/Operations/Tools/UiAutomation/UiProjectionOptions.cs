namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

/// <summary>Controls presentation size without changing the canonical tree or selector identities.</summary>
internal sealed record UiProjectionOptions(
    int MaximumNodes = 40,
    int MaximumMatches = 12,
    int MaximumCharacters = 8_000,
    int MaximumTextCharacters = 240,
    int MaximumAncestors = 3,
    bool ShareAncestors = true)
{
    public static UiProjectionOptions Model { get; } = new();
    public static UiProjectionOptions Cli { get; } = new(256, 100, 32_000, 240, 3);

    // Interaction DTO text is an exact selector, and cannot carry display-truncation metadata.
    public static UiProjectionOptions Interaction { get; } = new(128, 128, 16_000, int.MaxValue, 3, false);
}
