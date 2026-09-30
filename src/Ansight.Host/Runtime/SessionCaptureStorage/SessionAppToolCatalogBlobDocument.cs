namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Host;

internal sealed class SessionAppToolCatalogBlobDocument
{
    public const string SchemaName = "ansight.session-app-tool-catalog.v1";

    public string Schema { get; init; } = SchemaName;
    public required DateTimeOffset SavedAtUtc { get; init; }
    public required SessionAppToolCatalogSnapshot Catalog { get; init; }
}
