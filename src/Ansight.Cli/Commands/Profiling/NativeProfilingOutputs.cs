using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record NativeProfilingToolchainOutput(
    string Schema,
    NativeProfilingToolchain Toolchain,
    NativeProfileInspectionToolchain? InspectionToolchain = null);

internal sealed record NativeProfilingCaptureOutput(
    string Schema,
    string Operation,
    string CaptureId,
    bool IsFound,
    NativeCaptureSnapshot? Capture);

internal sealed record NativeProfilingCaptureListOutput(
    string Schema,
    string Platform,
    IReadOnlyList<NativeCaptureManifest> Captures);

internal sealed record NativeProfilingManifestOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeCaptureManifest? Manifest);

internal sealed record NativeProfilingCancellationOutput(
    string Schema,
    string CaptureId,
    bool CancellationRequested);

internal sealed record NativeProfilingArtifactOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeProfileArtifactLocation? Artifact);

internal sealed record NativeProfilingInspectionOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    NativeProfileCaptureInspection? Inspection);

internal sealed record InstrumentsTocInspectionOutput(
    string Schema,
    string CaptureId,
    bool IsFound,
    InstrumentsTocInspection? TableOfContents);

internal sealed record PerfettoQueryInspectionOutput(
    string Schema,
    string CaptureId,
    PerfettoQueryInspection Query);
