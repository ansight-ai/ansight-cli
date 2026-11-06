namespace Ansight.Host.Files;

public sealed record FileDatabaseObject(
    string Name,
    string Type,
    string Sql,
    IReadOnlyList<FileDatabaseColumn> Columns);
