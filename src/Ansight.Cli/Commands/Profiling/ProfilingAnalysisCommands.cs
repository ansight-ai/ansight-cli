using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal static class ProfilingAnalysisCommands
{
    public static int Run(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        string action)
    {
        var analysisKind = NormalizeAction(action);
        arguments.EnsurePositionalCount(
            4,
            $"ansight profile dotnet {analysisKind} <capture-id> [options]");
        var request = CreateRequest(analysisKind, arguments, out var resultLimit);
        var analysisResult = runtime.Profiling.AnalyzeCapture(request);
        var result = CreateOutput(analysisKind, analysisResult, resultLimit);
        output.Write(result, () => Render(result));
        return CliExitCodes.Success;
    }

    internal static DotNetTraceAnalysisRequest CreateRequest(
        string action,
        CliArguments arguments,
        out int resultLimit)
    {
        var analysisKind = NormalizeAction(action);
        var selection = new DotNetTraceSelection(
            ReadOptionalOffset(arguments, "start-ms"),
            ReadOptionalOffset(arguments, "end-ms"),
            ReadOptionalIdentifier(arguments, "process-id"),
            ReadOptionalIdentifier(arguments, "thread-id"));
        if (selection.StartMilliseconds is { } start
            && selection.EndMilliseconds is { } end
            && end < start)
        {
            throw new CliUsageException("--end-ms must be greater than or equal to --start-ms.");
        }

        resultLimit = analysisKind switch
        {
            "cpu" => arguments.GetIntOption("limit", 50, 1, 500),
            "gc" or "jit" or "exceptions" => arguments.GetIntOption("limit", 100, 1, 1_000),
            _ => 0
        };
        var hotspotLimit = analysisKind == "cpu" ? resultLimit : 100;
        var callTreeDepth = analysisKind == "call-tree"
            ? arguments.GetIntOption("max-depth", 32, 1, 128)
            : 64;
        var callTreeChildLimit = analysisKind == "call-tree"
            ? arguments.GetIntOption("max-children", 50, 1, 200)
            : 100;
        return new DotNetTraceAnalysisRequest(
            arguments.RequirePositional(3, "capture identifier"),
            selection,
            hotspotLimit,
            callTreeDepth,
            callTreeChildLimit);
    }

    internal static ProfilingAnalysisOutput CreateOutput(
        string action,
        DotNetTraceAnalysisResult analysisResult,
        int resultLimit)
    {
        var analysisKind = NormalizeAction(action);
        var output = new ProfilingAnalysisOutput
        {
            Schema = "ansight.dotnet-analysis/v1",
            CaptureId = analysisResult.CaptureId,
            AnalyzerVersion = analysisResult.AnalyzerVersion,
            AnalysisKind = analysisKind,
            Selection = analysisResult.Selection,
            Evidence = analysisResult.Evidence,
            Warnings = analysisResult.Analysis.Warnings
        };
        return analysisKind switch
        {
            "overview" => output with { Overview = CreateOverview(analysisResult.Analysis) },
            "startup" => output with { StartupTimeline = CreateStartupTimeline(analysisResult.Analysis) },
            "cpu" => output with { Cpu = CreateCpuAnalysis(analysisResult.Analysis) },
            "call-tree" => output with { CallTree = analysisResult.Analysis.CallTree },
            "threads" => output with { Threads = analysisResult.Analysis.Threads },
            "gc" => output with { Gc = CreateGcAnalysis(analysisResult.Analysis, resultLimit) },
            "jit" => output with { Jit = CreateJitAnalysis(analysisResult.Analysis, resultLimit) },
            "exceptions" => output with
            {
                Exceptions = CreateExceptionAnalysis(analysisResult.Analysis, resultLimit)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown analysis action.")
        };
    }

    internal static string Render(ProfilingAnalysisOutput result)
    {
        var rendered = result.AnalysisKind switch
        {
            "overview" => RenderOverview(result.Overview!),
            "startup" => RenderStartupTimeline(result.StartupTimeline!),
            "cpu" => RenderCpu(result.Cpu!),
            "call-tree" => RenderCallTree(result.CallTree!),
            "threads" => RenderThreads(result.Threads!),
            "gc" => RenderGc(result.Gc!),
            "jit" => RenderJit(result.Jit!),
            "exceptions" => RenderExceptions(result.Exceptions!),
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.AnalysisKind,
                "Unknown analysis kind.")
        };
        if (result.Warnings.Count == 0)
        {
            return rendered;
        }

        return string.Join(
            Environment.NewLine,
            new[] { rendered }.Concat(result.Warnings.Select(static warning => $"Warning: {warning}")));
    }

    internal static string NormalizeAction(string action)
        => action.Trim().ToLowerInvariant() switch
        {
            "overview" => "overview",
            "startup" or "startup-timeline" or "timeline" => "startup",
            "cpu" or "hotspots" or "cpu-hotspots" => "cpu",
            "call-tree" or "calltree" => "call-tree",
            "threads" or "thread-activity" => "threads",
            "gc" or "gc-summary" => "gc",
            "jit" or "jit-summary" => "jit",
            "exceptions" => "exceptions",
            _ => throw new CliUsageException(
                "Analysis command must be overview, startup, cpu, call-tree, threads, gc, jit, or exceptions.")
        };

    private static double? ReadOptionalOffset(CliArguments arguments, string optionName)
    {
        if (!arguments.HasFlag(optionName))
        {
            return null;
        }

        var value = arguments.GetDoubleOption(optionName);
        if (value < 0)
        {
            throw new CliUsageException($"--{optionName} cannot be negative.");
        }

        return value;
    }

    private static int? ReadOptionalIdentifier(CliArguments arguments, string optionName)
        => arguments.HasFlag(optionName)
            ? arguments.GetRequiredIntOption(optionName, 0, int.MaxValue)
            : null;

    private static ProfilingOverviewOutput CreateOverview(DotNetTraceAnalysis analysis)
    {
        var overview = analysis.Overview;
        return new ProfilingOverviewOutput(
            overview.StartMilliseconds,
            overview.EndMilliseconds,
            overview.DurationMilliseconds,
            overview.EventCount,
            overview.CpuSampleCount,
            overview.ProcessCount,
            overview.ThreadCount,
            overview.GcCount,
            overview.TotalGcPauseMilliseconds,
            overview.JittedMethodCount,
            overview.ExceptionCount,
            overview.SymbolicatedSamplePercentage,
            analysis.ProviderEventCounts,
            analysis.EventNameCounts);
    }

    private static ProfilingStartupTimelineOutput CreateStartupTimeline(DotNetTraceAnalysis analysis)
    {
        var milestones = new List<ProfilingStartupMilestoneOutput>
        {
            new(
                "trace-first-event",
                analysis.Overview.StartMilliseconds,
                "First captured EventPipe event in the selected range")
        };
        if (analysis.JittedMethods.Count > 0)
        {
            milestones.Add(new ProfilingStartupMilestoneOutput(
                "first-jit",
                analysis.JittedMethods.Min(static method => method.TimestampMilliseconds),
                "First captured method JIT event"));
        }

        if (analysis.GcPauses.Count > 0)
        {
            milestones.Add(new ProfilingStartupMilestoneOutput(
                "first-gc",
                analysis.GcPauses.Min(static pause => pause.StartMilliseconds),
                "First captured garbage collection"));
        }

        if (analysis.Exceptions.Count > 0)
        {
            milestones.Add(new ProfilingStartupMilestoneOutput(
                "first-exception",
                analysis.Exceptions.Min(static exception => exception.FirstTimestampMilliseconds),
                "First captured thrown exception"));
        }

        milestones.AddRange(analysis.StartupMilestones.Select(static milestone =>
            new ProfilingStartupMilestoneOutput(
                milestone.Kind,
                milestone.TimestampMilliseconds,
                $"{milestone.Provider}/{milestone.EventName}")));
        milestones.Add(new ProfilingStartupMilestoneOutput(
            "trace-last-event",
            analysis.Overview.EndMilliseconds,
            "Last captured EventPipe event in the selected range"));
        var applicationReadyObserved = analysis.StartupMilestones.Any(static milestone =>
            milestone.Kind == "application-ready");
        return new ProfilingStartupTimelineOutput(
            analysis.Overview.DurationMilliseconds,
            milestones.OrderBy(static milestone => milestone.TimestampMilliseconds).ToArray(),
            applicationReadyObserved,
            applicationReadyObserved
                ?
                [
                    "The application-ready marker means the first MAUI page handler was observed; it is not a first-frame rendering measurement.",
                    "Diagnostic startup duration includes EventPipe and launch instrumentation overhead."
                ]
                :
                [
                    "No Ansight application-ready marker was captured, so trace boundaries are the startup proxy.",
                    "Diagnostic startup duration includes EventPipe and launch instrumentation overhead."
                ]);
    }

    private static ProfilingCpuAnalysisOutput CreateCpuAnalysis(DotNetTraceAnalysis analysis)
        => new(
            analysis.Overview.CpuSampleCount,
            analysis.CpuHotspots.Count,
            analysis.CpuHotspots);

    private static ProfilingGcAnalysisOutput CreateGcAnalysis(
        DotNetTraceAnalysis analysis,
        int limit)
    {
        var intervals = analysis.GcPauses
            .OrderByDescending(static pause => pause.DurationMilliseconds)
            .Take(limit)
            .ToArray();
        return new ProfilingGcAnalysisOutput(
            analysis.GcPauses.Count,
            analysis.GcPauses.Sum(static pause => pause.DurationMilliseconds),
            analysis.GcPauses.Count == 0
                ? 0
                : analysis.GcPauses.Max(static pause => pause.DurationMilliseconds),
            analysis.GcPauses
                .GroupBy(static pause => pause.Generation)
                .OrderBy(static group => group.Key)
                .ToDictionary(static group => $"generation-{group.Key}", static group => group.Count()),
            intervals);
    }

    private static ProfilingJitAnalysisOutput CreateJitAnalysis(
        DotNetTraceAnalysis analysis,
        int limit)
    {
        var methods = analysis.JittedMethods
            .OrderByDescending(static method => method.IlSize)
            .ThenBy(static method => method.TimestampMilliseconds)
            .Take(limit)
            .ToArray();
        return new ProfilingJitAnalysisOutput(
            analysis.JittedMethods.Count,
            analysis.JittedMethods.Sum(static method => (long)method.IlSize),
            methods);
    }

    private static ProfilingExceptionAnalysisOutput CreateExceptionAnalysis(
        DotNetTraceAnalysis analysis,
        int limit)
    {
        var groups = analysis.Exceptions.Take(limit).ToArray();
        return new ProfilingExceptionAnalysisOutput(
            analysis.Exceptions.Sum(static group => group.Count),
            analysis.Exceptions.Count,
            groups);
    }

    private static string RenderOverview(ProfilingOverviewOutput overview)
    {
        return string.Join(
            Environment.NewLine,
            FormattableString.Invariant($"Duration: {overview.DurationMilliseconds:0.###} ms"),
            $"Events: {overview.EventCount}",
            $"CPU samples: {overview.CpuSampleCount}",
            $"Processes: {overview.ProcessCount}",
            $"Threads: {overview.ThreadCount}",
            FormattableString.Invariant(
                $"GC: {overview.GcCount} collections, {overview.TotalGcPauseMilliseconds:0.###} ms total intervals"),
            $"JIT methods: {overview.JittedMethodCount}",
            $"Exceptions: {overview.ExceptionCount}",
            FormattableString.Invariant(
                $"Symbolicated samples: {overview.SymbolicatedSamplePercentage:0.##}%"));
    }

    private static string RenderStartupTimeline(ProfilingStartupTimelineOutput timeline)
        => string.Join(
            Environment.NewLine,
            timeline.Milestones
                .Select(static milestone => FormattableString.Invariant(
                    $"{milestone.TimestampMilliseconds:0.###} ms\t{milestone.Id}\t{milestone.Description}"))
                .Concat(timeline.Limitations.Select(static limitation => $"Limitation: {limitation}")));

    private static string RenderCpu(ProfilingCpuAnalysisOutput cpu)
        => cpu.Hotspots.Count == 0
            ? "No CPU hotspots were captured in the selected range."
            : string.Join(
                Environment.NewLine,
                cpu.Hotspots.Select(static hotspot => FormattableString.Invariant(
                    $"{hotspot.ExclusivePercentage:0.##}% exclusive\t{hotspot.InclusivePercentage:0.##}% inclusive\t{hotspot.Method}\t{hotspot.Module}")));

    private static string RenderCallTree(DotNetCallTreeNode callTree)
    {
        var lines = new List<string>();
        AppendCallTree(lines, callTree, 0);
        return lines.Count == 0 ? "No sampled call tree was captured." : string.Join(Environment.NewLine, lines);
    }

    private static void AppendCallTree(
        ICollection<string> lines,
        DotNetCallTreeNode node,
        int depth)
    {
        var module = string.IsNullOrWhiteSpace(node.Module) ? string.Empty : $"\t{node.Module}";
        lines.Add($"{new string(' ', depth * 2)}{node.SampleCount}\t{node.Name}{module}");
        foreach (var child in node.Children)
        {
            AppendCallTree(lines, child, depth + 1);
        }
    }

    private static string RenderThreads(IReadOnlyList<DotNetThreadActivity> threads)
        => threads.Count == 0
            ? "No sampled thread activity was captured in the selected range."
            : string.Join(
                Environment.NewLine,
                threads.Select(static thread => FormattableString.Invariant(
                    $"{thread.CpuSamplePercentage:0.##}%\tpid={thread.ProcessId}\ttid={thread.ThreadId}\t{thread.ProcessName}\t{thread.ThreadName}")));

    private static string RenderGc(ProfilingGcAnalysisOutput gc)
    {
        var summary = FormattableString.Invariant(
            $"{gc.CollectionCount} collections\t{gc.TotalIntervalMilliseconds:0.###} ms total\t{gc.LongestIntervalMilliseconds:0.###} ms longest");
        return gc.Intervals.Count == 0
            ? summary
            : string.Join(
                Environment.NewLine,
                new[] { summary }.Concat(gc.Intervals.Select(static pause => FormattableString.Invariant(
                    $"{pause.StartMilliseconds:0.###} ms\t{pause.DurationMilliseconds:0.###} ms\tgen={pause.Generation}\t{pause.Reason}\t{pause.Type}"))));
    }

    private static string RenderJit(ProfilingJitAnalysisOutput jit)
    {
        var summary = $"{jit.MethodCount} methods\t{jit.TotalIlBytes} IL bytes";
        return jit.Methods.Count == 0
            ? summary
            : string.Join(
                Environment.NewLine,
                new[] { summary }.Concat(jit.Methods.Select(static method => FormattableString.Invariant(
                    $"{method.TimestampMilliseconds:0.###} ms\t{method.IlSize} IL bytes\t{method.Method}"))));
    }

    private static string RenderExceptions(ProfilingExceptionAnalysisOutput exceptions)
    {
        var summary = $"{exceptions.ThrowCount} throws\t{exceptions.GroupCount} groups";
        return exceptions.Groups.Count == 0
            ? summary
            : string.Join(
                Environment.NewLine,
                new[] { summary }.Concat(exceptions.Groups.Select(static group => FormattableString.Invariant(
                    $"{group.Count}\t{group.FirstTimestampMilliseconds:0.###}-{group.LastTimestampMilliseconds:0.###} ms\t{group.Type}\t{group.Message}"))));
    }
}
