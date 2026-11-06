using System.Text.Json;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Explorer.TaskExtraction;

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
