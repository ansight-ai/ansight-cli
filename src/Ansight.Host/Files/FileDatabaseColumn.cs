namespace Ansight.Host.Files;

public sealed record FileDatabaseColumn(
    string Name,
    string Key,
    string DeclaredType,
    bool IsNullable,
    bool IsPrimaryKey,
    string DefaultValue);
