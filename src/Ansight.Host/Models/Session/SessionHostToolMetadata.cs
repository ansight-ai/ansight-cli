namespace Ansight.Host.Models.Session;

public sealed record SessionHostToolMetadata(
    string Name,
    string Status,
    string? Version = null,
    string? Build = null,
    string? Path = null,
    string? SelectedVia = null);
