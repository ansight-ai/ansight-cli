namespace Ansight.Host.Runtime.Tasks;

public sealed record MaestroFlowRefinement(string Source, string Model);

public static class MaestroFlowRefiner
{
    public static async Task<MaestroFlowRefinement> RefineAsync(
        RuntimeCoordinator runtime,
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title,
        string source,
        string reasoningMode,
        string model,
        string? workspaceOverride,
        CancellationToken cancellationToken)
    {
        var result = await ExternalScriptRefiner.RefineAsync(runtime, snapshot, startUtc, endUtc, title,
            source, "maestro", reasoningMode, model, workspaceOverride, cancellationToken).ConfigureAwait(false);
        return new MaestroFlowRefinement(result.Source, result.Model);
    }

    public static void ValidateSource(string source, string appId)
        => ExternalScriptValidator.ValidateMaestro(source, appId);
}
