using System.Text.Json;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Explorer.TaskExtraction;

public sealed record WorkspaceTestDraft(
    string DraftId,
    string SessionId,
    string AppId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Title,
    WorkspaceTestExtraction Extraction,
    string Source,
    IReadOnlyList<string> TaskSectionIds,
    bool SkipTaskSections,
    string Assertions,
    string GenerationNotes,
    string Reasoning,
    bool NeedsRegeneration,
    string? LastExecutionId,
    string? LastTraceRunId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkspaceTestDraftSaveRequest(
    string? DraftId,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Title,
    WorkspaceTestExtraction Extraction,
    string Source,
    IReadOnlyList<string>? TaskSectionIds,
    bool SkipTaskSections,
    string? Assertions,
    string? GenerationNotes,
    string Reasoning,
    bool NeedsRegeneration,
    string? LastExecutionId,
    string? LastTraceRunId);

internal sealed class WorkspaceTestDraftStore(string applicationDataPath)
{
    private const int MaximumSourceCharacters = 500_000;
    private const int MaximumTaskSections = 100;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directoryPath = Path.Combine(applicationDataPath, "explorer", "test-drafts");
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WorkspaceTestDraft>> ListAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(directoryPath)) return [];
            var drafts = new List<WorkspaceTestDraft>();
            foreach (var path in Directory.EnumerateFiles(directoryPath, "*.json"))
            {
                var draft = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (draft is not null && string.Equals(draft.SessionId, sessionId, StringComparison.Ordinal))
                    drafts.Add(draft);
            }
            return drafts.OrderByDescending(static draft => draft.UpdatedAtUtc).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkspaceTestDraft> SaveAsync(string appId, WorkspaceTestDraftSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.EndUtc <= request.StartUtc)
            throw new InvalidDataException("A valid session and selected period are required for a test draft.");
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > MaximumSourceCharacters)
            throw new InvalidDataException("The YAML draft is empty or too large to save.");
        if (request.Extraction is null || string.IsNullOrWhiteSpace(request.Title))
            throw new InvalidDataException("The test draft needs its generated result and title.");
        if (request.TaskSectionIds?.Count > MaximumTaskSections)
            throw new InvalidDataException("Too many selected task sections were supplied.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var isUpdate = !string.IsNullOrWhiteSpace(request.DraftId);
            if (isUpdate && !Guid.TryParseExact(request.DraftId, "N", out _))
                throw new InvalidDataException("The test draft ID is invalid.");
            var draftId = isUpdate ? request.DraftId! : Guid.NewGuid().ToString("N");
            var path = Path.Combine(directoryPath, draftId + ".json");
            var previous = isUpdate ? await ReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
            if (isUpdate && previous is null)
                throw new FileNotFoundException("The test draft no longer exists.", path);
            if (previous is not null && (!string.Equals(previous.SessionId, request.SessionId, StringComparison.Ordinal)
                || !string.Equals(previous.AppId, appId, StringComparison.Ordinal)))
                throw new InvalidDataException("The test draft belongs to a different session or app.");

            var now = DateTimeOffset.UtcNow;
            var draft = new WorkspaceTestDraft(
                draftId, request.SessionId, appId, request.StartUtc, request.EndUtc,
                request.Title.Trim(), request.Extraction, request.Source,
                request.TaskSectionIds?.Distinct(StringComparer.Ordinal).ToArray() ?? [],
                request.SkipTaskSections, request.Assertions ?? string.Empty,
                request.GenerationNotes ?? string.Empty, request.Reasoning, request.NeedsRegeneration,
                request.LastExecutionId, request.LastTraceRunId,
                previous?.CreatedAtUtc ?? now, now);
            Directory.CreateDirectory(directoryPath);
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(draft, jsonOptions), cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return draft;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DiscardAsync(string sessionId, string draftId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(draftId, "N", out _)) return false;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(directoryPath, draftId + ".json");
            var draft = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (draft is null || !string.Equals(draft.SessionId, sessionId, StringComparison.Ordinal)) return false;
            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<WorkspaceTestDraft?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkspaceTestDraft>(stream, jsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
