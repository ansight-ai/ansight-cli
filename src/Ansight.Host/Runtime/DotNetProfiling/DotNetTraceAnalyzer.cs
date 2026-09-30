using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed class DotNetTraceAnalyzer
{
    private const string SampleProfilerProviderName = "Microsoft-DotNETCore-SampleProfiler";
    private readonly Lock conversionGate = new();

    public ParsedTraceAnalysis Analyze(
        string tracePath,
        TraceSelectionWindow? selection = null,
        int hotspotLimit = 100,
        int callTreeDepth = 64,
        int callTreeChildLimit = 100)
    {
        if (string.IsNullOrWhiteSpace(tracePath) || !File.Exists(tracePath))
        {
            throw new FileNotFoundException("The .nettrace artifact could not be found.", tracePath);
        }

        selection ??= TraceSelectionWindow.EntireTrace;
        var traceLogPath = GetOrCreateTraceLog(tracePath);
        var builder = new AnalysisBuilder(selection, hotspotLimit, callTreeDepth, callTreeChildLimit);
        using var traceLog = new TraceLog(traceLogPath);
        using var source = traceLog.Events.GetSource();
        var sampleProfiler = new SampleProfilerTraceEventParser(source);
        sampleProfiler.ThreadSample += builder.ObserveSampleEvent;
        source.Dynamic.All += builder.ObserveEvent;
        source.Clr.GCStart += builder.ObserveGcStart;
        source.Clr.GCStop += builder.ObserveGcStop;
        source.Clr.MethodJittingStarted += builder.ObserveJit;
        source.Clr.ExceptionStart += builder.ObserveException;
        source.Process();
        return builder.Build();
    }

    private string GetOrCreateTraceLog(string tracePath)
    {
        var capturePath = Directory.GetParent(Path.GetDirectoryName(tracePath) ?? string.Empty)?.FullName;
        var derivedPath = capturePath is null
            ? Path.GetDirectoryName(tracePath) ?? Environment.CurrentDirectory
            : Path.Combine(capturePath, "derived");
        Directory.CreateDirectory(derivedPath);
        var traceLogPath = Path.Combine(derivedPath, "runtime.etlx");
        lock (conversionGate)
        {
            if (!File.Exists(traceLogPath)
                || File.GetLastWriteTimeUtc(traceLogPath) < File.GetLastWriteTimeUtc(tracePath))
            {
                TraceLog.CreateFromEventPipeDataFile(tracePath, traceLogPath);
            }
        }

        return traceLogPath;
    }

    private sealed class AnalysisBuilder
    {
        private readonly TraceSelectionWindow selection;
        private readonly int hotspotLimit;
        private readonly int callTreeDepth;
        private readonly int callTreeChildLimit;
        private readonly Dictionary<string, int> providerEventCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> eventNameCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MethodSamples> methodSamples = new(StringComparer.Ordinal);
        private readonly Dictionary<ProcessThreadKey, ThreadSamples> threadSamples = [];
        private readonly Dictionary<GcKey, GcStartObservation> gcStarts = [];
        private readonly List<ParsedGcPause> gcPauses = [];
        private readonly List<ParsedJitMethod> jittedMethods = [];
        private readonly Dictionary<ExceptionKey, ExceptionObservation> exceptions = [];
        private readonly List<ParsedStartupMilestone> startupMilestones = [];
        private readonly CallTreeBuilder callTree = new("root", module: null);
        private readonly HashSet<int> processIds = [];
        private readonly HashSet<ProcessThreadKey> threadIds = [];
        private readonly List<string> warnings = [];
        private double minimumTimestamp = double.MaxValue;
        private double maximumTimestamp = double.MinValue;
        private int eventCount;
        private int sampleCount;
        private int symbolicatedSampleCount;

        public AnalysisBuilder(
            TraceSelectionWindow selection,
            int hotspotLimit,
            int callTreeDepth,
            int callTreeChildLimit)
        {
            this.selection = selection;
            this.hotspotLimit = Math.Clamp(hotspotLimit, 1, 1000);
            this.callTreeDepth = Math.Clamp(callTreeDepth, 1, 256);
            this.callTreeChildLimit = Math.Clamp(callTreeChildLimit, 1, 1000);
        }

        public void ObserveEvent(TraceEvent traceEvent)
        {
            if (!selection.Includes(
                    traceEvent.TimeStampRelativeMSec,
                    traceEvent.ProcessID,
                    traceEvent.ThreadID))
            {
                return;
            }

            if (string.Equals(traceEvent.ProviderName, SampleProfilerProviderName, StringComparison.Ordinal))
            {
                return;
            }

            eventCount++;
            minimumTimestamp = Math.Min(minimumTimestamp, traceEvent.TimeStampRelativeMSec);
            maximumTimestamp = Math.Max(maximumTimestamp, traceEvent.TimeStampRelativeMSec);
            processIds.Add(traceEvent.ProcessID);
            var processThread = new ProcessThreadKey(traceEvent.ProcessID, traceEvent.ThreadID);
            threadIds.Add(processThread);
            Increment(providerEventCounts, traceEvent.ProviderName ?? "unknown");
            Increment(eventNameCounts, $"{traceEvent.ProviderName ?? "unknown"}/{traceEvent.EventName}");
            var eventName = traceEvent.EventName;
            if (string.Equals(
                    traceEvent.ProviderName,
                    DotNetStartupMarker.ProviderName,
                    StringComparison.Ordinal)
                && (string.Equals(
                        eventName,
                        DotNetStartupMarker.EventName,
                        StringComparison.Ordinal)
                    || string.Equals(
                        eventName,
                        DotNetStartupMarker.ManagedModuleInitializedEventName,
                        StringComparison.Ordinal)))
            {
                startupMilestones.Add(new ParsedStartupMilestone(
                    DotNetStartupMarker.ProviderName,
                    eventName,
                    traceEvent.TimeStampRelativeMSec,
                    traceEvent.ProcessID,
                    traceEvent.ThreadID));
            }

            if (string.Equals(
                    traceEvent.ProviderName,
                    "Microsoft-Windows-DotNETRuntime",
                    StringComparison.Ordinal)
                && traceEvent.EventName.Contains("JittingStarted", StringComparison.OrdinalIgnoreCase))
            {
                ObserveDynamicJit(traceEvent);
            }
        }

        public void ObserveSampleEvent(TraceEvent traceEvent)
        {
            if (!selection.Includes(
                    traceEvent.TimeStampRelativeMSec,
                    traceEvent.ProcessID,
                    traceEvent.ThreadID))
            {
                return;
            }

            eventCount++;
            minimumTimestamp = Math.Min(minimumTimestamp, traceEvent.TimeStampRelativeMSec);
            maximumTimestamp = Math.Max(maximumTimestamp, traceEvent.TimeStampRelativeMSec);
            processIds.Add(traceEvent.ProcessID);
            var processThread = new ProcessThreadKey(traceEvent.ProcessID, traceEvent.ThreadID);
            threadIds.Add(processThread);
            Increment(providerEventCounts, SampleProfilerProviderName);
            Increment(eventNameCounts, $"{SampleProfilerProviderName}/{traceEvent.EventName}");
            ObserveSample(traceEvent, processThread);
        }

        public void ObserveGcStart(Microsoft.Diagnostics.Tracing.Parsers.Clr.GCStartTraceData traceData)
        {
            if (!selection.Includes(traceData.TimeStampRelativeMSec, traceData.ProcessID, traceData.ThreadID))
            {
                return;
            }

            gcStarts[new GcKey(traceData.ProcessID, traceData.Count)] = new GcStartObservation(
                traceData.TimeStampRelativeMSec,
                traceData.Depth,
                traceData.Reason.ToString(),
                traceData.Type.ToString(),
                traceData.ThreadID);
        }

        public void ObserveGcStop(Microsoft.Diagnostics.Tracing.Parsers.Clr.GCEndTraceData traceData)
        {
            if (!selection.Includes(traceData.TimeStampRelativeMSec, traceData.ProcessID, traceData.ThreadID))
            {
                return;
            }

            var key = new GcKey(traceData.ProcessID, traceData.Count);
            if (!gcStarts.Remove(key, out var start))
            {
                return;
            }

            gcPauses.Add(new ParsedGcPause(
                traceData.Count,
                start.Generation,
                start.Reason,
                start.Type,
                start.TimestampMilliseconds,
                traceData.TimeStampRelativeMSec,
                Math.Max(0, traceData.TimeStampRelativeMSec - start.TimestampMilliseconds),
                traceData.ProcessID,
                start.ThreadId));
        }

        public void ObserveJit(Microsoft.Diagnostics.Tracing.Parsers.Clr.MethodJittingStartedTraceData traceData)
        {
            if (!selection.Includes(traceData.TimeStampRelativeMSec, traceData.ProcessID, traceData.ThreadID))
            {
                return;
            }

            var methodName = string.IsNullOrWhiteSpace(traceData.MethodNamespace)
                ? traceData.MethodName
                : $"{traceData.MethodNamespace}.{traceData.MethodName}";
            jittedMethods.Add(new ParsedJitMethod(
                methodName,
                traceData.MethodILSize,
                traceData.TimeStampRelativeMSec,
                traceData.ProcessID,
                traceData.ThreadID));
        }

        public void ObserveException(Microsoft.Diagnostics.Tracing.Parsers.Clr.ExceptionTraceData traceData)
        {
            if (!selection.Includes(traceData.TimeStampRelativeMSec, traceData.ProcessID, traceData.ThreadID))
            {
                return;
            }

            var key = new ExceptionKey(
                NormalizeName(traceData.ExceptionType, "unknown exception"),
                string.IsNullOrWhiteSpace(traceData.ExceptionMessage) ? null : traceData.ExceptionMessage);
            if (!exceptions.TryGetValue(key, out var observation))
            {
                observation = new ExceptionObservation(traceData.TimeStampRelativeMSec);
                exceptions.Add(key, observation);
            }

            observation.Observe(traceData.TimeStampRelativeMSec);
        }

        public ParsedTraceAnalysis Build()
        {
            if (minimumTimestamp == double.MaxValue)
            {
                minimumTimestamp = selection.StartMilliseconds ?? 0;
                maximumTimestamp = selection.EndMilliseconds ?? minimumTimestamp;
                warnings.Add("No trace events matched the requested selection.");
            }

            if (sampleCount == 0)
            {
                warnings.Add("No sampled thread stacks matched the requested selection.");
            }
            else if (symbolicatedSampleCount < sampleCount)
            {
                warnings.Add("Some CPU samples could not be resolved to managed method names.");
            }

            var hotspots = methodSamples
                .Select(pair => new ParsedCpuHotspot(
                    pair.Key,
                    pair.Value.Module,
                    pair.Value.Exclusive,
                    pair.Value.Inclusive,
                    Percentage(pair.Value.Exclusive, sampleCount),
                    Percentage(pair.Value.Inclusive, sampleCount)))
                .OrderByDescending(method => method.ExclusiveSamples)
                .ThenByDescending(method => method.InclusiveSamples)
                .ThenBy(method => method.Method, StringComparer.Ordinal)
                .Take(hotspotLimit)
                .ToArray();
            var threads = threadSamples
                .Select(pair => new ParsedThreadActivity(
                    pair.Key.ProcessId,
                    pair.Key.ThreadId,
                    pair.Value.ProcessName,
                    pair.Value.ThreadName,
                    pair.Value.SampleCount,
                    Percentage(pair.Value.SampleCount, sampleCount)))
                .OrderByDescending(thread => thread.CpuSampleCount)
                .ThenBy(thread => thread.ProcessId)
                .ThenBy(thread => thread.ThreadId)
                .ToArray();
            var exceptionGroups = exceptions
                .Select(pair => new ParsedExceptionGroup(
                    pair.Key.Type,
                    pair.Key.Message,
                    pair.Value.Count,
                    pair.Value.FirstTimestampMilliseconds,
                    pair.Value.LastTimestampMilliseconds))
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Type, StringComparer.Ordinal)
                .ToArray();
            var orderedGcPauses = gcPauses
                .OrderBy(pause => pause.StartMilliseconds)
                .ToArray();
            var orderedJittedMethods = jittedMethods
                .OrderBy(method => method.TimestampMilliseconds)
                .ToArray();
            var overview = new ParsedTraceOverview(
                minimumTimestamp,
                maximumTimestamp,
                Math.Max(0, maximumTimestamp - minimumTimestamp),
                eventCount,
                sampleCount,
                processIds.Count,
                threadIds.Count,
                orderedGcPauses.Length,
                orderedGcPauses.Sum(pause => pause.DurationMilliseconds),
                orderedJittedMethods.Length,
                exceptionGroups.Sum(group => group.Count),
                Percentage(symbolicatedSampleCount, sampleCount));
            return new ParsedTraceAnalysis(
                overview,
                hotspots,
                callTree.Build(callTreeDepth, callTreeChildLimit),
                orderedGcPauses,
                orderedJittedMethods,
                exceptionGroups,
                threads,
                startupMilestones
                    .OrderBy(milestone => milestone.TimestampMilliseconds)
                    .ToArray(),
                providerEventCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                eventNameCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                warnings);
        }

        private void ObserveDynamicJit(TraceEvent traceEvent)
        {
            var methodNamespace = traceEvent.PayloadByName("MethodNamespace")?.ToString();
            var methodName = traceEvent.PayloadByName("MethodName")?.ToString();
            if (string.IsNullOrWhiteSpace(methodName))
            {
                return;
            }

            var fullMethodName = string.IsNullOrWhiteSpace(methodNamespace)
                ? methodName
                : $"{methodNamespace}.{methodName}";
            var ilSizeValue = traceEvent.PayloadByName("MethodILSize");
            var ilSize = ilSizeValue is null
                ? 0
                : Convert.ToInt32(ilSizeValue, System.Globalization.CultureInfo.InvariantCulture);
            jittedMethods.Add(new ParsedJitMethod(
                fullMethodName,
                ilSize,
                traceEvent.TimeStampRelativeMSec,
                traceEvent.ProcessID,
                traceEvent.ThreadID));
        }

        private void ObserveSample(TraceEvent traceEvent, ProcessThreadKey processThread)
        {
            sampleCount++;
            if (!threadSamples.TryGetValue(processThread, out var thread))
            {
                thread = new ThreadSamples(traceEvent.ProcessName, threadName: null);
                threadSamples.Add(processThread, thread);
            }

            thread.SampleCount++;
            var frames = ReadFrames(traceEvent.CallStack());
            if (frames.Count == 0)
            {
                IncrementMethod("[unsymbolicated]", module: null, exclusive: true);
                callTree.Observe([new StackFrame("[unsymbolicated]", null)]);
                return;
            }

            symbolicatedSampleCount++;
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                IncrementMethod(frame.Method, frame.Module, exclusive: index == frames.Count - 1);
            }

            callTree.Observe(frames);
        }

        private void IncrementMethod(string method, string? module, bool exclusive)
        {
            if (!methodSamples.TryGetValue(method, out var samples))
            {
                samples = new MethodSamples(module);
                methodSamples.Add(method, samples);
            }

            samples.Inclusive++;
            if (exclusive)
            {
                samples.Exclusive++;
            }
        }

        private static IReadOnlyList<StackFrame> ReadFrames(TraceCallStack? callStack)
        {
            var leafToRoot = new List<StackFrame>();
            for (var current = callStack; current is not null; current = current.Caller)
            {
                var methodName = NormalizeName(current.CodeAddress.FullMethodName, current.CodeAddress.ToString());
                leafToRoot.Add(new StackFrame(
                    methodName,
                    string.IsNullOrWhiteSpace(current.CodeAddress.ModuleName)
                        ? null
                        : current.CodeAddress.ModuleName));
            }

            leafToRoot.Reverse();
            return leafToRoot;
        }

        private static void Increment(IDictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }

        private static double Percentage(int value, int total)
            => total == 0 ? 0 : Math.Round(value * 100d / total, 3);

        private static string NormalizeName(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private sealed class CallTreeBuilder
    {
        private readonly Dictionary<FrameKey, CallTreeBuilder> children = [];

        public CallTreeBuilder(string name, string? module)
        {
            Name = name;
            Module = module;
        }

        public string Name { get; }

        public string? Module { get; }

        public int SampleCount { get; private set; }

        public void Observe(IReadOnlyList<StackFrame> frames)
        {
            SampleCount++;
            var current = this;
            foreach (var frame in frames)
            {
                var key = new FrameKey(frame.Method, frame.Module);
                if (!current.children.TryGetValue(key, out var child))
                {
                    child = new CallTreeBuilder(frame.Method, frame.Module);
                    current.children.Add(key, child);
                }

                child.SampleCount++;
                current = child;
            }
        }

        public ParsedCallTreeNode Build(int remainingDepth, int childLimit)
        {
            var builtChildren = remainingDepth <= 0
                ? []
                : children.Values
                    .OrderByDescending(child => child.SampleCount)
                    .ThenBy(child => child.Name, StringComparer.Ordinal)
                    .Take(childLimit)
                    .Select(child => child.Build(remainingDepth - 1, childLimit))
                    .ToArray();
            return new ParsedCallTreeNode(Name, Module, SampleCount, builtChildren);
        }
    }

    private sealed class MethodSamples
    {
        public MethodSamples(string? module)
        {
            Module = module;
        }

        public string? Module { get; }

        public int Exclusive { get; set; }

        public int Inclusive { get; set; }
    }

    private sealed class ThreadSamples
    {
        public ThreadSamples(string? processName, string? threadName)
        {
            ProcessName = processName;
            ThreadName = threadName;
        }

        public string? ProcessName { get; }

        public string? ThreadName { get; }

        public int SampleCount { get; set; }
    }

    private sealed class ExceptionObservation
    {
        public ExceptionObservation(double timestampMilliseconds)
        {
            FirstTimestampMilliseconds = timestampMilliseconds;
            LastTimestampMilliseconds = timestampMilliseconds;
        }

        public int Count { get; private set; }

        public double FirstTimestampMilliseconds { get; }

        public double LastTimestampMilliseconds { get; private set; }

        public void Observe(double timestampMilliseconds)
        {
            Count++;
            LastTimestampMilliseconds = timestampMilliseconds;
        }
    }

    private sealed record ProcessThreadKey(int ProcessId, int ThreadId);

    private sealed record GcKey(int ProcessId, int Number);

    private sealed record GcStartObservation(
        double TimestampMilliseconds,
        int Generation,
        string Reason,
        string Type,
        int ThreadId);

    private sealed record ExceptionKey(string Type, string? Message);

    private sealed record StackFrame(string Method, string? Module);

    private sealed record FrameKey(string Method, string? Module);
}
