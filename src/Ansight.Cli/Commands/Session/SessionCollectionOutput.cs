namespace Ansight.Cli.Commands.Session;

internal sealed record SessionCollectionOutput<T>(
    string Schema,
    string SessionId,
    string Collection,
    int TotalCount,
    IReadOnlyList<T> Items);
