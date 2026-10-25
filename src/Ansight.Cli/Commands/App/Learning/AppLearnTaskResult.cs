namespace Ansight.Cli.Commands.App.Learning;

internal sealed record AppLearnTaskResult(
    AppLearnCandidate Candidate,
    string Status,
    string Message,
    LocalTaskExtractionSnapshot? Extraction = null);
