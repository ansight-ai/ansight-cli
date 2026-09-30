namespace Ansight.Host.Apps;

internal interface IKnownAppStore
{
    event EventHandler? Changed;

    IReadOnlyList<KnownAppDefinition> GetSnapshot();

    bool TryGet(string appId, out KnownAppDefinition? app);

    void Upsert(KnownAppDefinition app);

    bool Remove(string appId);

    void EnsureKnown(
        string appId,
        string? name,
        string iconGlyph,
        string? codebasePath = null,
        DateTimeOffset? seenAtUtc = null,
        string? sourceKind = null,
        string? sourceTeamId = null,
        string? sourceTeamName = null);
}
