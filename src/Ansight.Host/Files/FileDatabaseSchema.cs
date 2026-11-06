namespace Ansight.Host.Files;

public sealed record FileDatabaseSchema(
    string DatabasePath,
    IReadOnlyList<FileDatabaseObject> Objects);
