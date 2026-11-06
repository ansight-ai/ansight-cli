using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

public sealed record AppInteractionResult(
    string Schema,
    string? Id,
    string Command,
    string SessionId,
    bool Succeeded,
    bool? InputSucceeded,
    string? Error,
    string Message,
    AppInteractionScreenshot? PreviousScreenshot,
    AppInteractionScreenshot? Screenshot,
    AppInteractionTiming Timing,
    RepositoryTaskRunResult? Task = null,
    RepositoryTaskCatalog? TaskCatalog = null,
    AppInteractionUi? Ui = null);
