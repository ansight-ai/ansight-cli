namespace Ansight.Host.Runtime.Feedback;

internal static class AnnotatedFeedbackSessionImporter
{
    public static async Task<OperationResult> ImportAsync(
        IRuntimeState runtimeState,
        AnnotatedFeedbackTransferCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(completion);

        string? artifactDirectoryPath = null;
        try
        {
            await using var stream = new FileStream(
                completion.BundlePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!AnnotatedFeedbackBundleReader.TryRead(stream, completion.BundlePath, out var content, out var error)
                || content is null)
            {
                return OperationResult.Failure(error ?? "The annotated feedback bundle could not be read.");
            }

            if (!string.Equals(content.AnnotationId, completion.ClientAnnotationId, StringComparison.Ordinal))
            {
                return OperationResult.Failure("The annotated feedback bundle annotation id did not match its transfer descriptor.");
            }

            var importFailures = new List<string>();
            string? screenshotFrameId = null;
            if (content.Screenshot is not null)
            {
                try
                {
                    var screenshotFrame = await runtimeState.AddSessionEvidenceImageAsync(
                        completion.SessionId,
                        content.Screenshot.CapturedAtUtc,
                        content.Screenshot.Format,
                        content.Screenshot.Width,
                        content.Screenshot.Height,
                        quality: 90,
                        content.Screenshot.Bytes);
                    screenshotFrameId = screenshotFrame?.FrameId;
                    if (screenshotFrame is null)
                    {
                        importFailures.Add("Ansight could not persist the annotated feedback screenshot.");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    importFailures.Add($"Ansight could not persist the annotated feedback screenshot: {exception.Message}");
                }
            }

            foreach (var visualTree in content.CreateVisualTreeSnapshots(screenshotFrameId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = runtimeState.AddSessionVisualTreeSnapshot(completion.SessionId, visualTree);
                    if (!result.IsSuccess)
                    {
                        importFailures.Add(result.Message);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    importFailures.Add($"Ansight could not persist visual tree '{visualTree.Source}': {exception.Message}");
                }
            }

            var artifactSnapshot = content.CreateArtifactSnapshot();
            if (artifactSnapshot is not null)
            {
                try
                {
                    artifactDirectoryPath = Path.Combine(
                        Path.GetTempPath(),
                        "AnsightHost",
                        "annotated-feedback-artifacts",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(artifactDirectoryPath);
                    foreach (var artifact in content.Artifacts.Where(artifact => artifact.Bytes is { Length: > 0 }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await File.WriteAllBytesAsync(
                            Path.Combine(artifactDirectoryPath, artifact.FileName),
                            artifact.Bytes!,
                            cancellationToken);
                    }

                    var result = runtimeState.AddSessionArtifactSnapshot(
                        completion.SessionId,
                        artifactSnapshot,
                        artifactDirectoryPath);
                    if (!result.IsSuccess)
                    {
                        importFailures.Add(result.Message);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    importFailures.Add($"Ansight could not persist annotated feedback artifacts: {exception.Message}");
                }
            }

            var annotation = content.CreateAnnotation(screenshotFrameId);
            if (importFailures.Count > 0)
            {
                annotation = new SessionAnnotation
                {
                    AnnotationId = annotation.AnnotationId,
                    StartUtc = annotation.StartUtc,
                    EndUtc = annotation.EndUtc,
                    Label = annotation.Label,
                    Source = annotation.Source,
                    Notes = annotation.Notes,
                    CaptureGroupId = annotation.CaptureGroupId,
                    CustomData = annotation.CustomData,
                    Evidence = annotation.Evidence,
                    HookFailures = annotation.HookFailures.Concat(importFailures).ToArray(),
                    Geometry = annotation.Geometry,
                    Target = annotation.Target
                };
            }

            return runtimeState.UpsertSessionAnnotation(completion.SessionId, annotation);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult.Failure($"Could not import annotated feedback: {exception.Message}");
        }
        finally
        {
            TryDeleteFile(completion.BundlePath);
            TryDeleteDirectory(artifactDirectoryPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }
    }
}
