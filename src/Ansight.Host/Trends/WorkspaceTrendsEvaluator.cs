using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ansight.Host.Trends;

public sealed class WorkspaceTrendsEvaluator
{
    private const double BytesPerMebibyte = 1024d * 1024d;

    public WorkspaceTrendsCheckResult Evaluate(
        AppSessionSnapshot snapshot,
        WorkspaceTrendsDefinition trends)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(trends);
        if (!string.Equals(trends.AppId, snapshot.AppId, StringComparison.Ordinal))
        {
            return new WorkspaceTrendsCheckResult(
                trends.TrendsId,
                trends.Required,
                WorkspaceTrendsStatus.Error,
                $"The trends definition must target captured app '{snapshot.AppId}'.",
                [],
                [],
                trends.DefinitionHash);
        }
        var span = trends.Span;
        var resolution = WorkspaceTrendsSpanResolver.Resolve(snapshot, span);
        if (resolution.Status != WorkspaceTrendsStatus.Passed)
        {
            var missingStatus = ResolveMissingDataStatus(trends);
            return new WorkspaceTrendsCheckResult(
                trends.TrendsId,
                trends.Required,
                missingStatus,
                resolution.Message,
                [],
                [],
                trends.DefinitionHash);
        }

        var platform = ResolvePlatform(snapshot);
        var applicableDefinitions = trends.Metrics
            .Where(definition => string.IsNullOrWhiteSpace(definition.Platform)
                                 || string.Equals(definition.Platform, platform, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var metrics = new List<WorkspaceTrendsMetricResult>();
        foreach (var instance in resolution.Instances)
        {
            foreach (var definition in applicableDefinitions)
            {
                metrics.Add(EvaluateMetric(snapshot, trends, instance, definition));
            }
        }

        var status = metrics.Any(static result => result.Status == WorkspaceTrendsStatus.Error)
            ? WorkspaceTrendsStatus.Error
            : metrics.Any(static result => result.Status == WorkspaceTrendsStatus.Failed)
                ? WorkspaceTrendsStatus.Failed
                : metrics.Any(static result => result.Status == WorkspaceTrendsStatus.Inconclusive)
                    ? ResolveMissingDataStatus(trends)
                    : metrics.Any(static result => result.Status == WorkspaceTrendsStatus.Warning)
                        ? WorkspaceTrendsStatus.Warning
                        : WorkspaceTrendsStatus.Passed;
        var passedCount = metrics.Count(static result => result.Status == WorkspaceTrendsStatus.Passed);
        var message = status == WorkspaceTrendsStatus.Passed
            ? $"All {metrics.Count:N0} trends metric(s) passed."
            : $"{passedCount:N0} of {metrics.Count:N0} trends metric(s) passed.";
        return new WorkspaceTrendsCheckResult(
            trends.TrendsId,
            trends.Required,
            status,
            message,
            resolution.Instances,
            metrics,
            trends.DefinitionHash);
    }

    internal static string MeasurementIdentity(AppSessionSnapshot snapshot, SessionMetricChannel channel, string definitionHash)
    {
        if (snapshot.CaptureSource != WorkspaceExecutionModes.Device) return definitionHash;
        var execution = snapshot.CustomProperties?["deviceExecution"];
        var identity = string.Join("\u001f", definitionHash, "device", channel.Source, channel.Kind, channel.Unit,
            execution?["collectorVersion"], execution?["sampleIntervalMs"], execution?["processScope"],
            execution?["hostArchitecture"], execution?["hostProcessors"], execution?["hostOs"],
            execution?["deviceMetadata"]?["hostModel"], execution?["deviceMetadata"]?["deviceConfiguration"],
            execution?["deviceMetadata"]?["operatingSystemVersion"]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static WorkspaceTrendsMetricResult EvaluateMetric(
        AppSessionSnapshot snapshot,
        WorkspaceTrendsDefinition trends,
        WorkspaceTrendsSpanInstance instance,
        WorkspaceTrendsMetricDefinition definition)
    {
        var channels = MatchChannels(snapshot.MetricChannels, definition.Channel);
        var metricKey = $"{trends.TrendsId}.{definition.MetricId}";
        var metricHash = ComputeMetricHash(trends.Span, definition);
        if (channels.Count == 0)
        {
            return Missing(
                trends,
                definition,
                instance,
                metricKey,
                metricHash,
                "No telemetry channel matched the metric selector.");
        }
        if (definition.Channel.RequireExactlyOne && channels.Count != 1)
        {
            return Missing(
                trends,
                definition,
                instance,
                metricKey,
                metricHash,
                $"The telemetry selector matched {channels.Count:N0} channels; exactly one was required.");
        }

        var channel = channels[0];
        metricHash = MeasurementIdentity(snapshot, channel, metricHash);
        var spanSamples = SelectSamples(
            snapshot,
            channel.ChannelId,
            instance.StartUtc,
            instance.EndUtc);
        var tailEndUtc = instance.EndUtc + definition.TailAfterEnd;
        var tailSamples = SelectSamples(
            snapshot,
            channel.ChannelId,
            instance.EndUtc,
            tailEndUtc);
        var baselineStartUtc = instance.StartUtc - definition.BaselineBeforeStart;
        var baselineSamples = SelectSamples(
            snapshot,
            channel.ChannelId,
            baselineStartUtc,
            instance.StartUtc);
        var statistic = definition.Statistic.Trim();
        var primarySamples = RequiresTail(statistic) ? tailSamples : spanSamples;
        var primaryStartUtc = RequiresTail(statistic) ? instance.EndUtc : instance.StartUtc;
        var primaryEndUtc = RequiresTail(statistic) ? tailEndUtc : instance.EndUtc;
        var sampleValidation = ValidateSamples(primarySamples, definition, primaryStartUtc, primaryEndUtc);
        if (sampleValidation is not null)
        {
            return Missing(
                trends,
                definition,
                instance,
                metricKey,
                metricHash,
                sampleValidation,
                primarySamples.Count,
                RequiresTail(statistic) ? instance.EndUtc : instance.StartUtc,
                RequiresTail(statistic) ? tailEndUtc : instance.EndUtc);
        }

        double? baselineValue = null;
        ComputedMetric computed;
        if (IsMemoryDeltaStatistic(statistic))
        {
            var baselineValidation = ValidateSamples(
                baselineSamples,
                definition with { MinimumSamples = Math.Min(2, definition.MinimumSamples) },
                baselineStartUtc,
                instance.StartUtc);
            if (baselineValidation is not null)
            {
                return Missing(
                    trends,
                    definition,
                    instance,
                    metricKey,
                    metricHash,
                    $"Memory baseline is invalid: {baselineValidation}",
                    baselineSamples.Count,
                    baselineStartUtc,
                    instance.StartUtc);
            }
            baselineValue = Percentile(baselineSamples.Select(static sample => (double)sample.Value).ToArray(), 0.5d);
        }

        try
        {
            computed = statistic.ToLowerInvariant() switch
            {
                "min" => new(spanSamples.Min(static sample => (double)sample.Value), ResolveUnit(channel)),
                "max" => new(spanSamples.Max(static sample => (double)sample.Value), ResolveUnit(channel)),
                "average" => new(spanSamples.Average(static sample => (double)sample.Value), ResolveUnit(channel)),
                "p05" => new(Percentile(spanSamples.Select(static sample => (double)sample.Value).ToArray(), 0.05d), ResolveUnit(channel)),
                "p10" => new(Percentile(spanSamples.Select(static sample => (double)sample.Value).ToArray(), 0.10d), ResolveUnit(channel)),
                "p50" => new(Percentile(spanSamples.Select(static sample => (double)sample.Value).ToArray(), 0.50d), ResolveUnit(channel)),
                "p95" => new(Percentile(spanSamples.Select(static sample => (double)sample.Value).ToArray(), 0.95d), ResolveUnit(channel)),
                "timebelowratio" => new(TimeBelowRatio(spanSamples, definition.StatisticThreshold!.Value, instance.StartUtc, instance.EndUtc), "ratio"),
                "memorypeakincreasemib" => new((spanSamples.Max(static sample => sample.Value) - baselineValue!.Value) / BytesPerMebibyte, "MiB"),
                "memoryretainedincreasemib" => new((Percentile(tailSamples.Select(static sample => (double)sample.Value).ToArray(), 0.5d) - baselineValue!.Value) / BytesPerMebibyte, "MiB"),
                "tailslopemibpersecond" => new(LinearSlope(tailSamples) / BytesPerMebibyte, "MiB/s"),
                "tailspreadmib" => new((Percentile(tailSamples.Select(static sample => (double)sample.Value).ToArray(), 0.95d)
                                         - Percentile(tailSamples.Select(static sample => (double)sample.Value).ToArray(), 0.05d)) / BytesPerMebibyte, "MiB"),
                _ => throw new InvalidDataException($"Unsupported statistic '{statistic}'.")
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            return Error(
                trends,
                definition,
                instance,
                metricKey,
                metricHash,
                exception.Message,
                primarySamples.Count);
        }

        var passed = MeetsBudget(computed.Value, definition.Budget);
        return new WorkspaceTrendsMetricResult(
            trends.TrendsId,
            definition.MetricId,
            metricKey,
            instance.InstanceIndex,
            passed ? WorkspaceTrendsStatus.Passed : WorkspaceTrendsStatus.Failed,
            BuildBudgetMessage(computed.Value, computed.Unit, definition.Budget, passed),
            computed.Value,
            computed.Unit,
            baselineValue,
            primarySamples.Count,
            RequiresTail(statistic) ? instance.EndUtc : instance.StartUtc,
            RequiresTail(statistic) ? tailEndUtc : instance.EndUtc,
            metricHash)
        {
            SpanGroup = instance.Group
        };
    }

    private static IReadOnlyList<SessionMetricChannel> MatchChannels(
        IReadOnlyList<SessionMetricChannel> channels,
        WorkspaceTrendsMetricSelector selector)
        => channels.Where(channel =>
                (string.IsNullOrWhiteSpace(selector.Type)
                 || string.Equals(
                     MetricChannelClassification.ResolveTelemetryType(channel.ChannelId, channel),
                     selector.Type,
                     StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(selector.Name)
                    || string.Equals(channel.Name, selector.Name, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(selector.Source)
                    || string.Equals(channel.Source, selector.Source, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(selector.Kind)
                    || string.Equals(channel.Kind, selector.Kind, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(static channel => channel.ChannelId)
            .ToArray();

    private static IReadOnlyList<SessionMetricSample> SelectSamples(
        AppSessionSnapshot snapshot,
        byte channelId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
        => snapshot.Metrics
            .Where(sample => sample.ChannelId == channelId
                             && sample.CapturedAtUtc >= startUtc
                             && sample.CapturedAtUtc <= endUtc)
            .OrderBy(static sample => sample.CapturedAtUtc)
            .ToArray();

    private static string? ValidateSamples(
        IReadOnlyList<SessionMetricSample> samples,
        WorkspaceTrendsMetricDefinition definition,
        DateTimeOffset spanStartUtc,
        DateTimeOffset spanEndUtc)
    {
        if (samples.Count < definition.MinimumSamples)
        {
            return $"Captured {samples.Count:N0} sample(s); at least {definition.MinimumSamples:N0} are required.";
        }
        var leadingGap = samples[0].CapturedAtUtc - spanStartUtc;
        if (leadingGap > definition.MaximumSampleGap)
        {
            return $"The first telemetry sample arrived {leadingGap.TotalMilliseconds:N0} ms after the span began, exceeding the {definition.MaximumSampleGap.TotalMilliseconds:N0} ms limit.";
        }
        for (var index = 1; index < samples.Count; index++)
        {
            var gap = samples[index].CapturedAtUtc - samples[index - 1].CapturedAtUtc;
            if (gap > definition.MaximumSampleGap)
            {
                return $"A telemetry gap of {gap.TotalMilliseconds:N0} ms exceeded the {definition.MaximumSampleGap.TotalMilliseconds:N0} ms limit.";
            }
        }
        var trailingGap = spanEndUtc - samples[^1].CapturedAtUtc;
        if (trailingGap > definition.MaximumSampleGap)
        {
            return $"The final telemetry sample was {trailingGap.TotalMilliseconds:N0} ms before the span ended, exceeding the {definition.MaximumSampleGap.TotalMilliseconds:N0} ms limit.";
        }
        return null;
    }

    private static double Percentile(IReadOnlyList<double> source, double percentile)
    {
        if (source.Count == 0)
        {
            throw new InvalidOperationException("A percentile requires at least one sample.");
        }
        var values = source.OrderBy(static value => value).ToArray();
        if (values.Length == 1)
        {
            return values[0];
        }
        var position = Math.Clamp(percentile, 0d, 1d) * (values.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return values[lower] + ((values[upper] - values[lower]) * fraction);
    }

    private static double TimeBelowRatio(
        IReadOnlyList<SessionMetricSample> samples,
        double threshold,
        DateTimeOffset spanStartUtc,
        DateTimeOffset spanEndUtc)
    {
        var duration = (spanEndUtc - spanStartUtc).TotalMilliseconds;
        if (duration <= 0)
        {
            throw new InvalidOperationException("The trends span has no duration.");
        }
        double belowMilliseconds = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            var sampleStart = samples[index].CapturedAtUtc < spanStartUtc
                ? spanStartUtc
                : samples[index].CapturedAtUtc;
            var sampleEnd = index + 1 < samples.Count
                ? samples[index + 1].CapturedAtUtc
                : spanEndUtc;
            if (samples[index].Value < threshold && sampleEnd > sampleStart)
            {
                belowMilliseconds += (sampleEnd - sampleStart).TotalMilliseconds;
            }
        }
        return Math.Clamp(belowMilliseconds / duration, 0d, 1d);
    }

    private static double LinearSlope(IReadOnlyList<SessionMetricSample> samples)
    {
        if (samples.Count < 2)
        {
            throw new InvalidOperationException("A slope requires at least two samples.");
        }
        var origin = samples[0].CapturedAtUtc;
        var xAverage = samples.Average(sample => (sample.CapturedAtUtc - origin).TotalSeconds);
        var yAverage = samples.Average(static sample => (double)sample.Value);
        double numerator = 0;
        double denominator = 0;
        foreach (var sample in samples)
        {
            var x = (sample.CapturedAtUtc - origin).TotalSeconds - xAverage;
            numerator += x * (sample.Value - yAverage);
            denominator += x * x;
        }
        return denominator <= double.Epsilon ? 0d : numerator / denominator;
    }

    private static bool MeetsBudget(double value, WorkspaceTrendsBudget budget)
        => (!budget.GreaterThanOrEqual.HasValue || value >= budget.GreaterThanOrEqual.Value)
           && (!budget.LessThanOrEqual.HasValue || value <= budget.LessThanOrEqual.Value)
           && (!budget.AbsoluteLessThanOrEqual.HasValue || Math.Abs(value) <= budget.AbsoluteLessThanOrEqual.Value);

    private static string BuildBudgetMessage(
        double value,
        string unit,
        WorkspaceTrendsBudget budget,
        bool passed)
    {
        var clauses = new List<string>();
        if (budget.GreaterThanOrEqual.HasValue)
        {
            clauses.Add($">= {budget.GreaterThanOrEqual.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        if (budget.LessThanOrEqual.HasValue)
        {
            clauses.Add($"<= {budget.LessThanOrEqual.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        if (budget.AbsoluteLessThanOrEqual.HasValue)
        {
            clauses.Add($"absolute <= {budget.AbsoluteLessThanOrEqual.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        return $"{value.ToString("0.###", CultureInfo.InvariantCulture)} {unit} {(passed ? "met" : "did not meet")} budget {string.Join(" and ", clauses)}.";
    }

    private static bool RequiresTail(string statistic)
        => statistic.Equals("memoryRetainedIncreaseMiB", StringComparison.OrdinalIgnoreCase)
           || statistic.Equals("tailSlopeMiBPerSecond", StringComparison.OrdinalIgnoreCase)
           || statistic.Equals("tailSpreadMiB", StringComparison.OrdinalIgnoreCase);

    private static bool IsMemoryDeltaStatistic(string statistic)
        => statistic.Equals("memoryPeakIncreaseMiB", StringComparison.OrdinalIgnoreCase)
           || statistic.Equals("memoryRetainedIncreaseMiB", StringComparison.OrdinalIgnoreCase);

    private static string ResolveUnit(SessionMetricChannel channel)
        => string.IsNullOrWhiteSpace(channel.Unit) ? "value" : channel.Unit.Trim();

    private static string ResolvePlatform(AppSessionSnapshot snapshot)
    {
        var osName = snapshot.DeviceProfile?.Device?.OsName;
        if (string.IsNullOrWhiteSpace(osName))
        {
            return "other";
        }

        var normalized = osName.Trim();
        if (normalized.Contains("android", StringComparison.OrdinalIgnoreCase))
        {
            return "android";
        }
        if (normalized.Contains("ios", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("iphone", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ipad", StringComparison.OrdinalIgnoreCase))
        {
            return "ios";
        }
        if (normalized.Contains("windows", StringComparison.OrdinalIgnoreCase))
        {
            return "windows";
        }
        if (normalized.Contains("mac", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("catalyst", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("os x", StringComparison.OrdinalIgnoreCase))
        {
            return "macos";
        }
        return "other";
    }

    private static WorkspaceTrendsStatus ResolveMissingDataStatus(WorkspaceTrendsDefinition trends)
        => string.Equals(trends.MissingDataOutcome, "fail", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceTrendsStatus.Failed
            : string.Equals(trends.MissingDataOutcome, "warn", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceTrendsStatus.Warning
                : WorkspaceTrendsStatus.Inconclusive;

    private static WorkspaceTrendsMetricResult Missing(
        WorkspaceTrendsDefinition trends,
        WorkspaceTrendsMetricDefinition definition,
        WorkspaceTrendsSpanInstance instance,
        string metricKey,
        string metricHash,
        string message,
        int sampleCount = 0,
        DateTimeOffset? spanStartUtc = null,
        DateTimeOffset? spanEndUtc = null)
        => new WorkspaceTrendsMetricResult(
            trends.TrendsId,
            definition.MetricId,
            metricKey,
            instance.InstanceIndex,
            ResolveMissingDataStatus(trends),
            message,
            null,
            string.Empty,
            null,
            sampleCount,
            spanStartUtc ?? instance.StartUtc,
            spanEndUtc ?? instance.EndUtc,
            metricHash)
        {
            SpanGroup = instance.Group
        };

    private static WorkspaceTrendsMetricResult Error(
        WorkspaceTrendsDefinition trends,
        WorkspaceTrendsMetricDefinition definition,
        WorkspaceTrendsSpanInstance instance,
        string metricKey,
        string metricHash,
        string message,
        int sampleCount)
        => new WorkspaceTrendsMetricResult(
            trends.TrendsId,
            definition.MetricId,
            metricKey,
            instance.InstanceIndex,
            WorkspaceTrendsStatus.Error,
            message,
            null,
            string.Empty,
            null,
            sampleCount,
            instance.StartUtc,
            instance.EndUtc,
            metricHash)
        {
            SpanGroup = instance.Group
        };

    private static string ComputeMetricHash(
        WorkspaceTrendsSpanDefinition span,
        WorkspaceTrendsMetricDefinition definition)
    {
        var value = string.Join(
            "|",
            span.Start.Label,
            span.Start.EventType,
            span.Start.ChannelId,
            span.End.Label,
            span.End.EventType,
            span.End.ChannelId,
            span.Selection,
            span.MaximumDuration.TotalMilliseconds,
            definition.MetricId,
            definition.Platform,
            definition.Channel.Type,
            definition.Channel.Name,
            definition.Channel.Source,
            definition.Channel.Kind,
            definition.Channel.RequireExactlyOne,
            definition.Statistic,
            definition.StatisticThreshold,
            definition.MinimumSamples,
            definition.MaximumSampleGap.TotalMilliseconds,
            definition.BaselineBeforeStart.TotalMilliseconds,
            definition.TailAfterEnd.TotalMilliseconds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record ComputedMetric(double Value, string Unit);
}
