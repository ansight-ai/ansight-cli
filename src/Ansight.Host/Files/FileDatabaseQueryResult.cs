namespace Ansight.Host.Files;

public sealed record FileDatabaseQueryResult(
    bool IsSuccess,
    string Message,
    string Sql,
    IReadOnlyList<FileDatabaseColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool IsTruncated)
{
    public static FileDatabaseQueryResult Failure(string message)
        => new(false, message, string.Empty, [], [], false);
}
