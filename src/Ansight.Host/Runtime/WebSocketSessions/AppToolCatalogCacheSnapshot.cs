namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

internal sealed record AppToolCatalogCacheSnapshot(
    string? Revision,
    string? AvailabilityRevision,
    JsonObject? IndexCatalog,
    long UpdateVersion);
