using Ansight.Cli.Commands.Cloud;
using Ansight.Host;
using Ansight.Infrastructure.Preferences;
using System.Globalization;
using System.Text;

namespace Ansight.Cli.Commands.Session;

internal static class SessionCommands
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var action = arguments.RequirePositional(1, "session action").ToLowerInvariant();
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        var runtime = lease.Runtime;
        return action switch
        {
            "list" => await ListAsync(runtime, arguments, output, cancellationToken),
            "show" => await ShowAsync(runtime, arguments, output, cancellationToken),
            "logs" => await LogsAsync(runtime, arguments, output, cancellationToken),
            "network" or "requests" => await NetworkAsync(runtime, arguments, output, cancellationToken),
            "metrics" => await MetricsAsync(runtime, arguments, output, cancellationToken),
            "telemetry" => await TelemetryAsync(runtime, arguments, output, cancellationToken),
            "images" or "screenshots" => await CollectionAsync(
                runtime,
                arguments,
                output,
                "images",
                static snapshot => snapshot.Images,
                cancellationToken),
            "touches" => await CollectionAsync(
                runtime,
                arguments,
                output,
                "touches",
                static snapshot => snapshot.Touches,
                cancellationToken),
            "trees" or "visual-trees" => await CollectionAsync(
                runtime,
                arguments,
                output,
                "visual-trees",
                static snapshot => snapshot.VisualTreeSnapshots,
                cancellationToken),
            "artifacts" => await CollectionAsync(
                runtime,
                arguments,
                output,
                "artifacts",
                static snapshot => snapshot.ArtifactSnapshots,
                cancellationToken),
            "annotations" => await CollectionAsync(
                runtime,
                arguments,
                output,
                "annotations",
                snapshot => snapshot.Annotations
                    .Where(annotation => arguments.GetOption("status") is not { } status
                        || string.Equals(annotation.Status, status, StringComparison.OrdinalIgnoreCase))
                    .Where(annotation => !arguments.HasFlag("without-status") || string.IsNullOrWhiteSpace(annotation.Status))
                    .ToArray(),
                cancellationToken),
            "analyses" => await CollectionAsync(
                runtime,
                arguments,
                output,
                "analyses",
                static snapshot => snapshot.Analyses,
                cancellationToken),
            "extract" => await SessionDirectCommands.ExtractAsync(
                runtime,
                arguments,
                output,
                cancellationToken),
            "trim" => SessionDirectCommands.Trim(runtime, arguments, output),
            "normalize" or "normalise" or "compact" => SessionDirectCommands.Normalize(
                runtime,
                arguments,
                output),
            "annotation" => await SessionDirectCommands.AnnotationAsync(
                runtime,
                arguments,
                output,
                cancellationToken),
            "analysis" => SessionDirectCommands.Analysis(runtime, arguments, output),
            "screenshot" => await SessionDirectCommands.ScreenshotAsync(
                runtime,
                arguments,
                output,
                cancellationToken),
            "artifact" => await SessionDirectCommands.ArtifactAsync(
                runtime,
                arguments,
                output,
                cancellationToken),
            "metadata" => await UpdateMetadataAsync(runtime, arguments, output, cancellationToken),
            "cache" => Cache(runtime, arguments, output),
            "delete" or "remove" => Delete(runtime, arguments, output),
            "disconnect" => Disconnect(runtime, arguments, output),
            "serve" => await ServeAsync(runtime, arguments, output, cancellationToken),
            "share" => await ShareAsync(runtime, arguments, output, cancellationToken),
            "share-batch" => await ShareBatchAsync(runtime, arguments, output, cancellationToken),
            "summary" or "summarize" or "summarise" => arguments.HasFlag("local")
                ? await SessionDirectCommands.SummaryLocalAsync(runtime, arguments, output, cancellationToken)
                : await SummaryAsync(runtime, arguments, output, cancellationToken),
            "summary-local" or "summarize-local" or "summarise-local" => await SessionDirectCommands.SummaryLocalAsync(
                runtime,
                arguments,
                output,
                cancellationToken),
            "url" or "share-url" or "replay-url" => await ShareUrlAsync(
                runtime,
                arguments,
                output,
                cancellationToken),
            "export" => await ExportAsync(runtime, arguments, output, cancellationToken),
            "sanitize" or "sanitise" => await SanitizeAsync(runtime, arguments, output, cancellationToken),
            "import" => await ImportAsync(runtime, arguments, output, cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown session action '{action}'. Expected list, show, logs, network, metrics, telemetry, images, touches, trees, artifacts, annotations, analyses, extract, trim, normalize, annotation, analysis, screenshot, artifact, metadata, cache, delete, disconnect, serve, share, share-batch, summary, summary-local, url, export, sanitize, or import.")
        };
    }

    internal static Task<int> ListAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var page = QuerySessionSummaries(
            runtime.Sessions.GetSummaries(),
            arguments,
            runtime.IsSessionLive,
            cancellationToken);
        var sessions = page.Items.Select(item =>
        {
            var session = item.Session;
            return new SessionSummaryOutput(
                session.SessionId,
                session.AppId,
                session.Name,
                session.ClientName,
                item.IsConnected,
                session.IsHistorical,
                session.CreatedUtc,
                session.LastUpdatedUtc,
                session.IsPinned,
                runtime.SessionCache.TryGetSizeBytes(session.SessionId, out var cacheSizeBytes)
                    ? cacheSizeBytes
                    : session.CacheSizeBytes,
                session.Tags)
            {
                Platform = ResolvePlatform(session),
                DeviceFormFactor = session.DeviceProfile?.Device?.FormFactor,
                OsName = session.DeviceProfile?.Device?.OsName,
                OsVersion = session.DeviceProfile?.Device?.OsVersion,
                IsVirtual = session.DeviceProfile?.Device?.IsVirtual
                            ?? session.DeviceProfile?.Device?.IsEmulator,
                IsEmulator = session.DeviceProfile?.Device?.IsEmulator
            };
        }).ToArray();
        output.Write(
            new SessionListOutput(
                "ansight.sessions/v3",
                page.MatchedCount,
                sessions.Length,
                page.Limit,
                page.IsTruncated,
                page.NextCursor,
                sessions),
            () => RenderSessionList(sessions, page.NextCursor));
        return Task.FromResult(CliExitCodes.Success);
    }

    internal static SessionListPage QuerySessionSummaries(
        IReadOnlyList<AppSessionSnapshot> summaries,
        CliArguments arguments,
        Func<string, bool> isConnected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(isConnected);

        var appId = arguments.GetOption("app-id");
        var connectedOnly = arguments.HasFlag("connected");
        var platformFilters = arguments.GetOptions("platform")
            .Select(NormalizePlatform)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var formFactorFilters = arguments.GetOptions("device-form-factor")
            .Concat(arguments.GetOptions("form-factor"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var osNameFilters = arguments.GetOptions("os-name").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var osVersionFilters = arguments.GetOptions("os-version").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deviceTypeFilters = arguments.GetOptions("device-type").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (arguments.HasFlag("physical"))
        {
            deviceTypeFilters.Add("physical");
        }

        if (arguments.HasFlag("virtual"))
        {
            deviceTypeFilters.Add("virtual");
        }

        if (arguments.HasFlag("emulator") || arguments.HasFlag("simulator"))
        {
            deviceTypeFilters.Add("emulator");
        }

        var search = arguments.GetOption("search") ?? arguments.GetOption("contains");
        var fromUtc = ParseOptionalTimestamp(arguments.GetOption("from"), "from");
        var toUtc = ParseOptionalTimestamp(arguments.GetOption("to"), "to");
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
        {
            throw new CliUsageException("--from must be earlier than or equal to --to.");
        }

        var requiredTags = arguments.GetOptions("tag").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var limit = arguments.GetIntOption("limit", 20, 1, 1_000);
        var cursor = arguments.HasFlag("cursor")
            ? DecodeSessionListCursor(arguments.RequireOption("cursor"))
            : null;
        var connectionStates = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool ResolveConnectionState(string sessionId)
        {
            if (!connectionStates.TryGetValue(sessionId, out var connected))
            {
                connected = isConnected(sessionId);
                connectionStates[sessionId] = connected;
            }

            return connected;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var matched = summaries
            .Where(session => string.IsNullOrWhiteSpace(appId)
                              || string.Equals(session.AppId, appId, StringComparison.OrdinalIgnoreCase))
            .Where(session => !connectedOnly || ResolveConnectionState(session.SessionId))
            .Where(session => !fromUtc.HasValue || session.LastUpdatedUtc >= fromUtc.Value)
            .Where(session => !toUtc.HasValue || session.CreatedUtc <= toUtc.Value)
            .Where(session => MatchesSessionFilters(
                    session,
                    platformFilters,
                    formFactorFilters,
                    osNameFilters,
                    osVersionFilters,
                    deviceTypeFilters,
                    requiredTags,
                    search,
                    arguments.HasFlag("has-logs"),
                    arguments.HasFlag("has-telemetry")))
            .OrderByDescending(static session => session.LastUpdatedUtc)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .ToArray();
        var remaining = cursor is null
            ? matched
            : matched.Where(session => IsAfterSessionListCursor(session, cursor)).ToArray();
        var window = remaining.Take(limit + 1).ToArray();
        var pageSessions = window.Take(limit).ToArray();
        var isTruncated = window.Length > pageSessions.Length;
        var nextCursor = isTruncated && pageSessions.Length > 0
            ? EncodeSessionListCursor(pageSessions[^1])
            : null;
        var items = pageSessions
            .Select(session => new SessionListPageItem(
                session,
                ResolveConnectionState(session.SessionId)))
            .ToArray();
        return new SessionListPage(
            matched.Length,
            limit,
            isTruncated,
            nextCursor,
            items);
    }

    internal static string RenderSessionList(
        IReadOnlyList<SessionSummaryOutput> sessions,
        string? nextCursor)
    {
        if (sessions.Count == 0)
        {
            return "No sessions found.";
        }

        var rows = sessions.Select(session =>
            $"{session.SessionId}\t{session.AppId}\t{session.LastUpdatedUtc:O}\t"
            + $"{(session.IsPinned ? "pinned" : "unpinned")}\t{FormatByteSize(session.CacheSizeBytes)}");
        var rendered = string.Join(Environment.NewLine, rows);
        return nextCursor is null
            ? rendered
            : rendered + Environment.NewLine
              + $"More sessions are available. Continue with --cursor {nextCursor}";
    }

    internal static bool IsAfterSessionListCursor(
        AppSessionSnapshot session,
        SessionListCursor cursor)
    {
        var timestampComparison = session.LastUpdatedUtc.CompareTo(cursor.LastUpdatedUtc);
        return timestampComparison < 0
               || timestampComparison == 0
               && StringComparer.Ordinal.Compare(session.SessionId, cursor.SessionId) > 0;
    }

    internal static string EncodeSessionListCursor(AppSessionSnapshot session)
    {
        var payload = string.Join(
            '\n',
            "v1",
            session.LastUpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            session.SessionId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static SessionListCursor DecodeSessionListCursor(string value)
    {
        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = (normalized.Length % 4) switch
            {
                2 => normalized + "==",
                3 => normalized + "=",
                0 => normalized,
                _ => throw new FormatException("Invalid Base64 URL length.")
            };
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var parts = payload.Split('\n', 3, StringSplitOptions.None);
            if (parts.Length != 3
                || !string.Equals(parts[0], "v1", StringComparison.Ordinal)
                || !DateTimeOffset.TryParse(
                    parts[1],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var lastUpdatedUtc)
                || string.IsNullOrWhiteSpace(parts[2]))
            {
                throw new FormatException("Invalid session-list cursor payload.");
            }

            return new SessionListCursor(lastUpdatedUtc, parts[2]);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new CliUsageException("--cursor is not a valid session-list cursor.");
        }
    }

    internal static bool MatchesSessionFilters(
        AppSessionSnapshot snapshot,
        IReadOnlySet<string> platforms,
        IReadOnlySet<string> formFactors,
        IReadOnlySet<string> osNames,
        IReadOnlySet<string> osVersions,
        IReadOnlySet<string> deviceTypes,
        IReadOnlySet<string> requiredTags,
        string? search,
        bool requireLogs,
        bool requireTelemetry)
    {
        var device = snapshot.DeviceProfile?.Device;
        var isVirtual = device?.IsVirtual ?? device?.IsEmulator;
        if (platforms.Count > 0 && !platforms.Contains(ResolvePlatform(snapshot))
            || formFactors.Count > 0 && !MatchesFilter(formFactors, device?.FormFactor)
            || osNames.Count > 0 && !MatchesFilter(osNames, device?.OsName)
            || osVersions.Count > 0 && !MatchesFilter(osVersions, device?.OsVersion)
            || requiredTags.Count > 0 && !requiredTags.All(tag => snapshot.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            || requireLogs && snapshot.TotalLogCount <= 0
            || requireTelemetry && snapshot.TotalMetricSampleCount <= 0)
        {
            return false;
        }

        if (deviceTypes.Count > 0 && !deviceTypes.Any(type => type.ToLowerInvariant() switch
            {
                "physical" => isVirtual == false,
                "virtual" => isVirtual == true,
                "emulator" or "simulator" => device?.IsEmulator == true,
                _ => false
            }))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(search)
               || new[]
                   {
                       snapshot.SessionId,
                       snapshot.AppId,
                       snapshot.Name,
                       snapshot.ClientName,
                       device?.Model,
                       device?.OsName,
                       device?.OsVersion
                   }
                   .Where(static value => !string.IsNullOrWhiteSpace(value))
                   .Any(value => value!.Contains(search, StringComparison.OrdinalIgnoreCase))
               || snapshot.Tags.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool MatchesFilter(IReadOnlySet<string> filters, string? value)
        => !string.IsNullOrWhiteSpace(value) && filters.Contains(value.Trim());

    internal static string ResolvePlatform(AppSessionSnapshot snapshot)
    {
        var osName = snapshot.DeviceProfile?.Device?.OsName ?? string.Empty;
        if (osName.Contains("android", StringComparison.OrdinalIgnoreCase))
        {
            return "android";
        }

        if (osName.Contains("ios", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("iphone", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("ipad", StringComparison.OrdinalIgnoreCase))
        {
            return "ios";
        }

        if (osName.Contains("windows", StringComparison.OrdinalIgnoreCase))
        {
            return "windows";
        }

        if (osName.Contains("mac", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("catalyst", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("os x", StringComparison.OrdinalIgnoreCase))
        {
            return "macos";
        }

        return "other";
    }

    internal static string NormalizePlatform(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "apple" => "ios",
            var normalized => normalized
        };

    internal static async Task<int> ShowAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session show <session-id>");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var session = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            output.WriteError("session_not_found", $"Session '{sessionId}' was not found.", CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }

        output.Write(
            new SessionDetailOutput(
                "ansight.session/v1",
                session,
                runtime.IsSessionLive(session.SessionId)),
            () => $"Session: {session.SessionId}\n"
                  + $"App: {session.AppId}\n"
                  + $"Client: {session.ClientName}\n"
                  + $"Status: {session.Status}\n"
                  + $"Connected: {runtime.IsSessionLive(session.SessionId)}\n"
                  + $"Pinned: {session.IsPinned}\n"
                  + $"Cache: {FormatByteSize(session.CacheSizeBytes)}\n"
                  + $"Logs: {session.TotalLogCount}\n"
                  + $"Metrics: {session.TotalMetricSampleCount}\n"
                  + $"Images: {session.TotalImageCount}");
        return CliExitCodes.Success;
    }

    internal static int Delete(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var result = runtime.SessionEditing.Delete(sessionId);
        return WriteOperation("delete", sessionId, result, output);
    }

    internal static int Cache(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        var action = arguments.Positionals.Count >= 3
            ? arguments.Positionals[2].ToLowerInvariant()
            : "status";
        if (action is not ("status" or "plan" or "prune" or "compact"))
        {
            throw new CliUsageException(
                $"Unknown session cache action '{action}'. Expected status, plan, prune, or compact.");
        }

        arguments.EnsurePositionalCount(
            action == "status" && arguments.Positionals.Count == 2 ? 2 : 3,
            "ansight session cache status|plan|prune|compact [options]");
        if (action != "prune" && arguments.HasFlag("apply"))
        {
            throw new CliUsageException("--apply is only valid with 'ansight session cache prune'.");
        }

        if (action == "compact")
        {
            return CompactCache(runtime, arguments, output);
        }

        var retentionDays = arguments.GetIntOption(
            "retention-days",
            SessionCleanupPreferenceDefaults.RetentionDays,
            SessionCleanupPreferenceDefaults.MinimumRetentionDays,
            SessionCleanupPreferenceDefaults.MaximumRetentionDays);
        var maximumCacheSizeBytes = ParseMaximumCacheSize(arguments);
        var plan = runtime.SessionCache.CreateSessionCacheCleanupPlan(retentionDays, maximumCacheSizeBytes);
        var shouldApply = action == "prune" && arguments.HasFlag("apply");
        var deletedSessionIds = new List<string>();
        var failures = new List<SessionCacheFailureOutput>();

        if (shouldApply)
        {
            foreach (var item in plan.Items)
            {
                var current = runtime.Sessions.GetSummaries()
                    .FirstOrDefault(session => string.Equals(
                        session.SessionId,
                        item.SessionId,
                        StringComparison.Ordinal));
                if (current is null)
                {
                    failures.Add(new SessionCacheFailureOutput(
                        item.SessionId,
                        "Session no longer exists."));
                    continue;
                }

                if (current.IsPinned || runtime.IsSessionLive(current.SessionId))
                {
                    failures.Add(new SessionCacheFailureOutput(
                        item.SessionId,
                        "Session became pinned or live after the cleanup plan was created."));
                    continue;
                }

                var result = runtime.SessionEditing.Delete(item.SessionId);
                if (result.IsSuccess)
                {
                    deletedSessionIds.Add(item.SessionId);
                }
                else
                {
                    failures.Add(new SessionCacheFailureOutput(item.SessionId, result.Message));
                }
            }
        }

        var resultingCacheSizeBytes = shouldApply
            ? runtime.SessionCache.CreateSessionCacheCleanupPlan(retentionDays, maximumCacheSizeBytes).TotalCacheSizeBytes
            : plan.TotalCacheSizeBytes;
        var resultOutput = new SessionCacheCommandOutput(
            "ansight.session-cache/v1",
            action,
            shouldApply,
            plan,
            resultingCacheSizeBytes,
            deletedSessionIds,
            failures);
        output.Write(resultOutput, () => RenderCacheResult(resultOutput));
        return failures.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static int CompactCache(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        var compactionAgeDays = arguments.GetIntOption(
            "compaction-age-days",
            SessionCleanupPreferenceDefaults.CompactionAgeDays,
            SessionCleanupPreferenceDefaults.MinimumRetentionDays,
            SessionCleanupPreferenceDefaults.MaximumRetentionDays);
        var compactedSessionCount = runtime.SessionCache.CompactSessionCache(compactionAgeDays);
        var resultingCacheSizeBytes = runtime.SessionCache.CreateSessionCacheCleanupPlan(
            SessionCleanupPreferenceDefaults.RetentionDays,
            SessionCleanupPreferenceDefaults.MaximumCacheBytes).TotalCacheSizeBytes;
        var result = new SessionCacheCompactionOutput(
            "ansight.session-cache-compaction/v1",
            "compact",
            compactionAgeDays,
            compactedSessionCount,
            resultingCacheSizeBytes);
        output.Write(
            result,
            () => $"Compacted {compactedSessionCount} "
                  + $"{(compactedSessionCount == 1 ? "session" : "sessions")} "
                  + $"at least {compactionAgeDays} days old.{Environment.NewLine}"
                  + $"Current cache: {FormatByteSize(resultingCacheSizeBytes)}.");
        return CliExitCodes.Success;
    }

    internal static long ParseMaximumCacheSize(CliArguments arguments)
    {
        var source = arguments.GetOption("max-cache")
                     ?? arguments.GetOption("max-cache-size");
        return source is null
            ? SessionCleanupPreferenceDefaults.MaximumCacheBytes
            : ByteSizeParser.Parse(
                source,
                "max-cache",
                SessionCleanupPreferenceDefaults.MinimumMaximumCacheBytes,
                SessionCleanupPreferenceDefaults.MaximumMaximumCacheBytes);
    }

    internal static string RenderCacheResult(SessionCacheCommandOutput result)
    {
        var plan = result.Plan;
        var lines = new List<string>
        {
            $"Sessions: {plan.SessionCount} ({plan.PinnedSessionCount} pinned, {plan.LiveSessionCount} live)",
            $"Cache: {FormatByteSize(plan.TotalCacheSizeBytes)} of {FormatByteSize(plan.MaximumCacheSizeBytes)}",
            $"Cleanup candidates: {plan.DeleteCount}; projected cache: {FormatByteSize(plan.ProjectedCacheSizeBytes)}"
        };

        lines.AddRange(plan.Items.Select(item =>
            $"{item.SessionId}\t{item.Reason}\t{FormatByteSize(item.CacheSizeBytes)}\t{item.CreatedUtc:O}"));
        if (result.Operation == "prune" && !result.Applied)
        {
            lines.Add("Dry run only. Re-run with --apply to delete these unpinned, inactive sessions.");
        }
        else if (result.Applied)
        {
            lines.Add($"Deleted {result.DeletedSessionIds.Count} session(s).");
            lines.Add($"Current cache: {FormatByteSize(result.ResultingCacheSizeBytes)}.");
        }

        lines.AddRange(result.Failures.Select(failure =>
            $"Failed: {failure.SessionId}: {failure.Message}"));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatByteSize(long bytes)
    {
        const double kibibyte = 1024d;
        const double mebibyte = 1024d * kibibyte;
        const double gibibyte = 1024d * mebibyte;
        const double tebibyte = 1024d * gibibyte;

        return bytes switch
        {
            >= (long)tebibyte => $"{bytes / tebibyte:0.##} TiB",
            >= (long)gibibyte => $"{bytes / gibibyte:0.##} GiB",
            >= (long)mebibyte => $"{bytes / mebibyte:0.##} MiB",
            >= (long)kibibyte => $"{bytes / kibibyte:0.##} KiB",
            _ => $"{Math.Max(0, bytes)} B"
        };
    }

    internal static async Task<int> LogsAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session logs <session-id> [--limit <count>] [--contains <text>] [--stream <id>]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return WriteSessionNotFound(sessionId, output);
        }

        var contains = arguments.GetOption("contains") ?? arguments.GetOption("query");
        var streams = arguments.GetOptions("stream").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = arguments.GetOptions("tag").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = arguments.GetOptions("source").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var startUtc = ParseOptionalTimestamp(arguments.GetOption("start"), "start");
        var endUtc = ParseOptionalTimestamp(arguments.GetOption("end"), "end");
        var minimumPriority = ParseOptionalLogPriority(
            arguments.GetOption("minimum-priority")
            ?? arguments.GetOption("minimum-severity")
            ?? arguments.GetOption("severity"));
        var exactPriority = ParseOptionalLogPriority(arguments.GetOption("priority"));
        var limit = arguments.GetIntOption("limit", 200, 1, 10_000);
        var logs = snapshot.Logs
            .Where(log => string.IsNullOrWhiteSpace(contains)
                          || log.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Where(log => streams.Count == 0 || streams.Contains(log.StreamId))
            .Where(log => tags.Count == 0 || tags.Contains(log.Tag))
            .Where(log => sources.Count == 0 || sources.Contains(log.Source))
            .Where(log => !startUtc.HasValue || log.TimestampUtc >= startUtc.Value)
            .Where(log => !endUtc.HasValue || log.TimestampUtc <= endUtc.Value)
            .Where(log => !exactPriority.HasValue || log.Priority == exactPriority.Value)
            .Where(log => !minimumPriority.HasValue || GetLogPriorityRank(log.Priority) >= GetLogPriorityRank(minimumPriority.Value))
            .OrderBy(static log => log.TimestampUtc)
            .TakeLast(limit)
            .ToArray();
        output.Write(
            new SessionLogsOutput(
                "ansight.session-logs/v1",
                sessionId,
                snapshot.TotalLogCount,
                logs),
            () => logs.Length == 0
                ? "No matching logs found."
                : string.Join(
                    Environment.NewLine,
                    logs.Select(log =>
                        $"{log.TimestampUtc:O}\t{log.Priority}\t{log.StreamId}\t{log.Message}")));
        return CliExitCodes.Success;
    }

    internal static DateTimeOffset? ParseOptionalTimestamp(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        throw new CliUsageException($"--{optionName} must be an ISO-8601 timestamp.");
    }

    internal static async Task<int> NetworkAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session network <session-id> [--method <method>] [--status <code|class>] [--host <host>] [--contains <text>] [--failed] [--bodies] [--limit <count>]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return WriteSessionNotFound(sessionId, output);
        }

        var requestId = arguments.GetOption("id");
        var methods = arguments.GetOptions("method").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statuses = arguments.GetOptions("status").ToArray();
        var host = arguments.GetOption("host");
        var contains = arguments.GetOption("contains") ?? arguments.GetOption("query");
        var startUtc = ParseOptionalTimestamp(arguments.GetOption("start"), "start");
        var endUtc = ParseOptionalTimestamp(arguments.GetOption("end"), "end");
        var failedOnly = arguments.HasFlag("failed");
        var includeBodies = arguments.HasFlag("bodies");
        var limit = arguments.GetIntOption("limit", 200, 1, 10_000);
        var matched = snapshot.NetworkRequests
            .Where(request => string.IsNullOrWhiteSpace(requestId)
                              || string.Equals(request.Id, requestId, StringComparison.OrdinalIgnoreCase))
            .Where(request => methods.Count == 0 || methods.Contains(request.Method))
            .Where(request => statuses.Length == 0 || statuses.Any(status => SessionNetworkRequestFilter.MatchesStatus(request, status)))
            .Where(request => string.IsNullOrWhiteSpace(host) || SessionNetworkRequestFilter.MatchesHost(request.Url, host))
            .Where(request => string.IsNullOrWhiteSpace(contains)
                              || SessionNetworkRequestFilter.MatchesQuery(request, contains))
            .Where(request => !startUtc.HasValue || request.StartedAtUtc >= startUtc.Value)
            .Where(request => !endUtc.HasValue || request.StartedAtUtc <= endUtc.Value)
            .Where(request => !failedOnly || SessionNetworkRequestFilter.IsFailed(request))
            .OrderBy(static request => request.StartedAtUtc)
            .ThenBy(static request => request.Id, StringComparer.Ordinal)
            .ToArray();
        var requests = matched.TakeLast(limit).ToArray();
        output.Write(
            new SessionNetworkOutput(
                "ansight.session-network/v1",
                sessionId,
                Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count),
                matched.Length,
                requests),
            () => requests.Length == 0
                ? "No matching network requests found."
                : string.Join(
                    Environment.NewLine,
                    requests.Select(request => FormatNetworkRequest(request, includeBodies))));
        return CliExitCodes.Success;
    }

    internal static string FormatNetworkStatus(SessionNetworkRequest request)
        => request.StatusCode?.ToString(CultureInfo.InvariantCulture)
           ?? (!string.IsNullOrWhiteSpace(request.ErrorType) ? "ERROR" : "NO_RESPONSE");

    internal static string FormatNetworkRequest(SessionNetworkRequest request, bool includeBodies)
    {
        var summary = FormattableString.Invariant(
            $"{request.StartedAtUtc:O}\t{request.Method}\t{FormatNetworkStatus(request)}\t{request.DurationMilliseconds:0.0}ms\t{request.Url}");
        if (!includeBodies)
        {
            return summary;
        }

        return string.Join(
            Environment.NewLine,
            new[]
            {
                summary,
                FormatNetworkBody("request", request.RequestBody, request.RequestBodySizeBytes),
                FormatNetworkBody("response", request.ResponseBody, request.ResponseBodySizeBytes)
            });
    }

    internal static string FormatNetworkBody(string label, SessionNetworkBody? body, long? reportedBytes)
    {
        if (body is null)
        {
            return reportedBytes is null
                ? $"  {label} body: not captured"
                : $"  {label} body: not captured ({reportedBytes.Value.ToString(CultureInfo.InvariantCulture)} bytes reported)";
        }

        var total = body.TotalBytes ?? reportedBytes;
        var size = total is null
            ? body.CapturedBytes.ToString(CultureInfo.InvariantCulture)
            : $"{body.CapturedBytes.ToString(CultureInfo.InvariantCulture)}/{total.Value.ToString(CultureInfo.InvariantCulture)}";
        return $"  {label} body ({body.Encoding}, {size} bytes{(body.Truncated ? ", truncated" : string.Empty)}):{Environment.NewLine}{body.Data}";
    }

    internal static LogPriority? ParseOptionalLogPriority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<LogPriority>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new CliUsageException(
                "Log priority must be verbose, debug, information, warning, error, fatal, or unknown.");
    }

    internal static int GetLogPriorityRank(LogPriority priority)
        => priority switch
        {
            LogPriority.Verbose => 0,
            LogPriority.Debug => 1,
            LogPriority.Information => 2,
            LogPriority.Warning => 3,
            LogPriority.Error => 4,
            LogPriority.Fatal => 5,
            _ => -1
        };

    internal static async Task<int> MetricsAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session metrics <session-id> [--limit <count>] [--channel <id>]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return WriteSessionNotFound(sessionId, output);
        }

        byte? channelId = null;
        if (arguments.GetOption("channel") is { } channelValue)
        {
            if (!byte.TryParse(channelValue, out var parsedChannel))
            {
                throw new CliUsageException("--channel must be a number between 0 and 255.");
            }

            channelId = parsedChannel;
        }

        var limit = arguments.GetIntOption("limit", 500, 1, 50_000);
        var samples = snapshot.Metrics
            .Where(sample => channelId is null || sample.ChannelId == channelId.Value)
            .OrderBy(static sample => sample.CapturedAtUtc)
            .TakeLast(limit)
            .ToArray();
        var channels = snapshot.MetricChannels
            .Where(channel => channelId is null || channel.ChannelId == channelId.Value)
            .ToArray();
        output.Write(
            new SessionMetricsOutput(
                "ansight.session-metrics/v1",
                sessionId,
                snapshot.TotalMetricSampleCount,
                channels,
                samples),
            () => samples.Length == 0
                ? "No matching metric samples found."
                : string.Join(
                    Environment.NewLine,
                    samples.Select(sample =>
                        $"{sample.CapturedAtUtc:O}\t{sample.ChannelId}\t{sample.Value}\tsegment={sample.SegmentId}")));
        return CliExitCodes.Success;
    }

    internal static Task<int> TelemetryAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        return arguments.Positionals.Count >= 3
               && string.Equals(arguments.Positionals[2], "analyze", StringComparison.OrdinalIgnoreCase)
            ? AnalyzeTelemetryAsync(runtime, arguments, output, cancellationToken)
            : MetricsAsync(runtime, arguments, output, cancellationToken);
    }

    internal static async Task<int> AnalyzeTelemetryAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight session telemetry analyze <session-id> [--kind <all|fps-drop|memory-spike>] [--memory-min-percent <percent>] [--memory-min-mb <megabytes>] [--max-events <count>] [--fail-on-detection]");
        var sessionId = arguments.RequirePositional(3, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return WriteSessionNotFound(sessionId, output);
        }

        var selection = ParseTelemetryAnalysisSelection(arguments.GetOption("kind"));
        var minimumIncreasePercent = arguments.GetIntOption(
            "memory-min-percent",
            MemorySpikeAnalysisOptions.DefaultMinimumIncreasePercent,
            MemorySpikeAnalysisOptions.MinimumAllowedIncreasePercent,
            MemorySpikeAnalysisOptions.MaximumAllowedIncreasePercent);
        var minimumIncreaseMegabytes = arguments.GetIntOption(
            "memory-min-mb",
            MemorySpikeAnalysisOptions.DefaultMinimumIncreaseMegabytes,
            MemorySpikeAnalysisOptions.MinimumAllowedIncreaseMegabytes,
            MemorySpikeAnalysisOptions.MaximumAllowedIncreaseMegabytes);
        var maximumEventsPerKind = arguments.GetIntOption("max-events", 12, 1, 10_000);
        var analysisService = new TelemetryAnalysisService();

        var fpsDrops = selection is TelemetryAnalysisSelection.All or TelemetryAnalysisSelection.FpsDrop
            ? analysisService.AnalyzeFpsDrops(snapshot.Metrics, snapshot.MetricChannels, cancellationToken)
                .OrderByDescending(static detection => detection.SeverityScore)
                .ThenBy(static detection => detection.StartUtc)
                .Take(maximumEventsPerKind)
                .OrderBy(static detection => detection.StartUtc)
                .ThenBy(static detection => detection.ChannelId)
                .Select(static detection => new SessionFpsDropDetectionOutput(
                    detection.ChannelId,
                    detection.ChannelName,
                    detection.StartUtc,
                    detection.EndUtc,
                    detection.Minimum.CapturedAtUtc.ToUniversalTime(),
                    detection.Baseline.CapturedAtUtc.ToUniversalTime(),
                    detection.Baseline.Value,
                    detection.Minimum.Value,
                    detection.DropFps,
                    detection.Severity.ToString(),
                    detection.SeverityScore))
                .ToArray()
            : [];

        var memoryOptions = new MemorySpikeAnalysisOptions(
            minimumIncreasePercent,
            minimumIncreaseMegabytes);
        var memorySpikes = selection is TelemetryAnalysisSelection.All or TelemetryAnalysisSelection.MemorySpike
            ? analysisService.AnalyzeMemorySpikes(
                    snapshot.Metrics,
                    snapshot.MetricChannels,
                    memoryOptions,
                    cancellationToken)
                .OrderByDescending(static detection => detection.SeverityScore)
                .ThenBy(static detection => detection.StartUtc)
                .Take(maximumEventsPerKind)
                .OrderBy(static detection => detection.StartUtc)
                .ThenBy(static detection => detection.ChannelId)
                .Select(static detection => new SessionMemorySpikeDetectionOutput(
                    detection.ChannelId,
                    detection.ChannelName,
                    detection.StartUtc,
                    detection.EndUtc,
                    detection.Peak.CapturedAtUtc.ToUniversalTime(),
                    detection.Baseline.CapturedAtUtc.ToUniversalTime(),
                    detection.Baseline.Value,
                    detection.Peak.Value,
                    detection.DeltaBytes,
                    detection.DeltaPercent,
                    detection.RetainedUntilUtc,
                    detection.Severity.ToString(),
                    detection.SeverityScore))
                .ToArray()
            : [];

        var detectionCount = fpsDrops.Length + memorySpikes.Length;
        var result = new SessionTelemetryAnalysisOutput(
            "ansight.session-telemetry-analysis/v1",
            sessionId,
            FormatTelemetryAnalysisSelection(selection),
            snapshot.Metrics.Count,
            maximumEventsPerKind,
            new SessionMemorySpikeThresholdsOutput(
                memoryOptions.MinimumIncreasePercent,
                memoryOptions.MinimumIncreaseMegabytes),
            detectionCount,
            fpsDrops,
            memorySpikes);
        output.Write(result, () => RenderTelemetryAnalysis(result));

        return arguments.HasFlag("fail-on-detection") && detectionCount > 0
            ? CliExitCodes.TelemetryAnalysisDetected
            : CliExitCodes.Success;
    }

    internal static TelemetryAnalysisSelection ParseTelemetryAnalysisSelection(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => TelemetryAnalysisSelection.All,
            "fps" or "fps-drop" => TelemetryAnalysisSelection.FpsDrop,
            "memory" or "memory-spike" => TelemetryAnalysisSelection.MemorySpike,
            _ => throw new CliUsageException("--kind must be all, fps-drop, or memory-spike.")
        };
    }

    internal static string FormatTelemetryAnalysisSelection(TelemetryAnalysisSelection selection)
    {
        return selection switch
        {
            TelemetryAnalysisSelection.FpsDrop => "fps-drop",
            TelemetryAnalysisSelection.MemorySpike => "memory-spike",
            _ => "all"
        };
    }

    internal static string RenderTelemetryAnalysis(SessionTelemetryAnalysisOutput result)
    {
        var lines = new List<string>();
        lines.AddRange(result.FpsDrops.Select(static detection => FormattableString.Invariant(
            $"{detection.FocusUtc:O}\tfps-drop\t{detection.ChannelName}\t{detection.BaselineFps} -> {detection.MinimumFps} FPS\tdrop={detection.DropFps}\tseverity={detection.Severity}")));
        lines.AddRange(result.MemorySpikes.Select(static detection => FormattableString.Invariant(
            $"{detection.FocusUtc:O}\tmemory-spike\t{detection.ChannelName}\t+{detection.DeltaBytes / (1024d * 1024d):0.#} MB\tdelta={detection.DeltaPercent:0.#}%\tseverity={detection.Severity}")));
        return lines.Count == 0
            ? "No FPS drops or memory spikes detected."
            : string.Join(Environment.NewLine, lines.Order(StringComparer.Ordinal));
    }

    internal static async Task<int> CollectionAsync<T>(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        string collectionName,
        Func<AppSessionSnapshot, IReadOnlyList<T>> selector,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            $"ansight session {collectionName} <session-id> [--limit <count>]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return WriteSessionNotFound(sessionId, output);
        }

        var limit = arguments.GetIntOption("limit", 200, 1, 10_000);
        var allItems = selector(snapshot);
        var items = allItems.TakeLast(limit).ToArray();
        output.Write(
            new SessionCollectionOutput<T>(
                "ansight.session-collection/v1",
                sessionId,
                collectionName,
                allItems.Count,
                items),
            () => collectionName == "artifacts"
                ? Commands.ArtifactComparison.ArtifactCommands.FormatList(new ArtifactListResult(
                    "ansight.artifact-list/v1", items.Cast<SessionArtifactSnapshot>().Select(item => ArtifactComparisonService.Describe(snapshot, item)).ToArray(), allItems.Count, null))
                : items.Length == 0
                ? $"No {collectionName} found."
                : $"{items.Length} of {allItems.Count} {collectionName}. Use --json for structured details.");
        return CliExitCodes.Success;
    }

    internal static async Task<int> UpdateMetadataAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session metadata <session-id> [--name <name>] [--tag <tag>] [--pin|--unpin]");
        if (arguments.HasFlag("pin") && arguments.HasFlag("unpin"))
        {
            throw new CliUsageException("Use only one of --pin or --unpin.");
        }

        var sessionId = arguments.RequirePositional(2, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return WriteSessionNotFound(sessionId, output);
        }

        var isPinned = arguments.HasFlag("pin")
            ? true
            : arguments.HasFlag("unpin")
                ? false
                : snapshot.IsPinned;
        var tags = arguments.HasFlag("clear-tags")
            ? Array.Empty<string>()
            : arguments.HasFlag("tag")
                ? arguments.GetOptions("tag")
                : snapshot.Tags;
        var notes = arguments.HasFlag("clear-notes")
            ? null
            : arguments.GetOption("notes") ?? snapshot.Notes;
        var result = runtime.SessionEditing.UpdateMetadata(
            sessionId,
            isPinned,
            tags,
            notes,
            arguments.GetOption("name") ?? snapshot.Name);
        return WriteOperation("metadata", sessionId, result, output);
    }

    internal static int WriteSessionNotFound(string sessionId, CliOutput output)
    {
        output.WriteError(
            "session_not_found",
            $"Session '{sessionId}' was not found.",
            CliExitCodes.Failure);
        return CliExitCodes.Failure;
    }

    internal static int Disconnect(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var result = runtime.AppTools.ForceDisconnect(sessionId);
        return WriteOperation("disconnect", sessionId, result, output);
    }

    internal static async Task<int> ExportAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight session export <session-id> <output.zip> [--exclude-native-logs] [--sanitize] [--sanitizer <module.ts>]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var outputPath = Path.GetFullPath(arguments.RequirePositional(3, "archive output path"));
        var sanitizerModulePath = ResolveSanitizerModulePath(arguments);
        var result = await runtime.SessionArchives.ExportSessionArchiveAsync(
            sessionId,
            outputPath,
            new SessionArchiveExportOptions
            {
                IncludeNativeDeviceLogs = !arguments.HasFlag("exclude-native-logs"),
                SanitizerModulePath = sanitizerModulePath,
                Sanitize = sanitizerModulePath is null && arguments.HasFlag("sanitize")
            },
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new SessionArchiveOutput(
                "ansight.session-archive/v1",
                "export",
                result.IsSuccess,
                result.Message,
                sessionId,
                outputPath),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static async Task<int> SanitizeAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight session sanitize <session-id> <output.zip> [--sanitizer <module.ts>] [--exclude-native-logs]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var outputPath = Path.GetFullPath(arguments.RequirePositional(3, "archive output path"));
        var sanitizerModulePath = ResolveSanitizerModulePath(arguments);
        var result = await runtime.SessionArchives.ExportSessionArchiveAsync(
            sessionId,
            outputPath,
            new SessionArchiveExportOptions
            {
                IncludeNativeDeviceLogs = !arguments.HasFlag("exclude-native-logs"),
                SanitizerModulePath = sanitizerModulePath,
                Sanitize = sanitizerModulePath is null
            },
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new SessionArchiveOutput(
                "ansight.session-archive/v1",
                "sanitize",
                result.IsSuccess,
                result.Message,
                sessionId,
                outputPath),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static async Task<int> ServeAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (arguments.Positionals.Count > 2
            && string.Equals(arguments.Positionals[2], "stop", StringComparison.OrdinalIgnoreCase))
        {
            arguments.EnsurePositionalCount(4, "ansight session serve stop <session-id>");
            var stoppedSessionId = arguments.RequirePositional(3, "session identifier");
            var stopResult = await runtime.SessionReplays.StopAsync(stoppedSessionId, cancellationToken)
                .ConfigureAwait(false);
            return WriteOperation("serve-stop", stoppedSessionId, stopResult, output);
        }

        arguments.EnsurePositionalCount(
            3,
            "ansight session serve <session-id> [--port <port>] [--open]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var result = await runtime.SessionReplays.StartAsync(
            new SessionReplayStartRequest(
                sessionId,
                arguments.GetIntOption("port", 0, 0, 65_535)),
            cancellationToken).ConfigureAwait(false);
        var isResidentHost = CliCommandContext.Current is not null;
        string? openWarning = null;
        if (result.IsSuccess && result.ReplayUrl is not null && arguments.HasFlag("open"))
        {
            openWarning = CliBrowserLauncher.TryOpen(result.ReplayUrl);
        }

        output.Write(
            new SessionLocalReplayOutput(
                "ansight.session-local-replay/v1",
                result.IsSuccess,
                result.Message,
                result.SessionId,
                result.ReplayUrl?.ToString(),
                result.Port,
                result.WasAlreadyRunning,
                isResidentHost),
            () => BuildServeMessage(result, isResidentHost, openWarning));
        if (!result.IsSuccess)
        {
            return CliExitCodes.Failure;
        }

        if (isResidentHost)
        {
            return CliExitCodes.Success;
        }

        try
        {
            await runtime.SessionReplays.WaitForStopAsync(result.SessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await runtime.SessionReplays.StopAsync(result.SessionId, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return CliExitCodes.Success;
    }

    internal static string BuildServeMessage(
        SessionReplayStartResult result,
        bool isResidentHost,
        string? openWarning)
    {
        if (!result.IsSuccess)
        {
            return result.Message;
        }

        var lifetime = isResidentHost
            ? $"The resident host will keep this replay active. Stop it with: ansight session serve stop {result.SessionId}"
            : "Press Ctrl+C to stop the local replay server.";
        var warning = string.IsNullOrWhiteSpace(openWarning)
            ? string.Empty
            : $"\nBrowser warning: {openWarning}";
        return $"{result.Message}\nReplay: {result.ReplayUrl}\n{lifetime}{warning}";
    }
internal static Task<int> ShareAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("CloudSessionCommands.ShareAsync", [runtime, arguments, output, cancellationToken]);
internal static Task<int> ShareBatchAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("CloudSessionCommands.ShareBatchAsync", [runtime, arguments, output, cancellationToken]);

internal static Task<int> ShareUrlAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("CloudSessionCommands.ShareUrlAsync", [runtime, arguments, output, cancellationToken]);
internal static Task<int> SummaryAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("CloudSessionCommands.SummaryAsync", [runtime, arguments, output, cancellationToken]);

    internal static async Task<int> ImportAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session import <archive.zip> [--password-env <NAME>]");
        var archivePath = Path.GetFullPath(arguments.RequirePositional(2, "archive path"));
        var password = arguments.GetOption("password-env") is { Length: > 0 } passwordVariable
            ? Environment.GetEnvironmentVariable(passwordVariable)
            : null;
        var result = await runtime.SessionArchives.ImportSessionArchiveAsync(
            archivePath,
            cancellationToken,
            password: password).ConfigureAwait(false);
        output.Write(
            new SessionArchiveOutput(
                "ansight.session-archive/v1",
                "import",
                result.IsSuccess,
                result.Message,
                result.ImportedSession?.SessionId,
                archivePath),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static int WriteOperation(
        string operation,
        string sessionId,
        OperationResult result,
        CliOutput output)
    {
        output.Write(
            new SessionOperationOutput(
                "ansight.session-operation/v1",
                operation,
                sessionId,
                result.IsSuccess,
                result.Message),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static string? ResolveSanitizerModulePath(CliArguments arguments)
    {
        var modulePath = arguments.GetOption("sanitizer") ?? arguments.GetOption("policy");
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return null;
        }

        var absolutePath = Path.GetFullPath(modulePath);
        if (!File.Exists(absolutePath))
        {
            throw new CliUsageException($"Sanitizer module '{absolutePath}' was not found.");
        }

        var extension = Path.GetExtension(absolutePath);
        if (!extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("Sanitizer modules must use .ts.");
        }

        return absolutePath;
    }

    internal static string BuildHelp()
        => """
           Inspect, manage, replay, transfer, and share app sessions

           Usage:
             ansight session list [filters] [--limit <count>] [--cursor <cursor>]
             ansight session show <session-id>
             ansight session logs <session-id> [filters]
             ansight session network <session-id> [filters]
             ansight session metrics <session-id> [--limit <count>] [--channel <id>]
             ansight session telemetry analyze <session-id> [options]
             ansight session images|touches|trees|artifacts <session-id> [--limit <count>]
             ansight session annotations|analyses <session-id> [--limit <count>]
             ansight session extract <session-id> (--annotation <id> | --start <utc> --end <utc>) [--name <name>]
             ansight session trim <session-id> --start <utc> --end <utc> --mode <cut|keep>
             ansight session normalize <session-id>
             ansight session annotation upsert <session-id> [options]
             ansight session annotation status <session-id> <annotation-id> (--status <text> | --clear-status)
             ansight session annotation delete <session-id> <annotation-id>
             ansight session analysis delete <session-id> <analysis-id>
             ansight session screenshot export <session-id> --output <path> [options]
             ansight session artifact export <session-id> --path <artifact-path> --output <path>
             ansight session metadata <session-id> [options]
             ansight session cache [status|plan|prune|compact] [options]
             ansight session disconnect <session-id>
             ansight session delete <session-id>
             ansight session serve <session-id> [--port <port>] [--open]
             ansight session serve stop <session-id>
             ansight session share <session-id> [options]
             ansight session share-batch <session-id> <session-id>... [--team-id <uuid>] [--sanitize]
             ansight session summary <session-id> [analysis options]
             ansight session summary <session-id> --local [--team-id <uuid>] [--reasoning <mode>]
             ansight session summary-local <session-id> [--team-id <uuid>] [--reasoning <mode>]
             ansight session url <session-id> [options]
             ansight session export <session-id> <output.zip> [--exclude-native-logs] [--sanitize]
             ansight session sanitize <session-id> <output.zip> [--sanitizer <module.ts>]
             ansight session import <archive.zip> [--password-env <NAME>]

           Inspection commands:
             list          List recorded and live sessions; --connected filters to live sessions
             show          Show session identity, status, connection state, and evidence totals
             logs          Return recent logs with optional text and stream filters
             network       Query captured HTTP requests by method, status, host, time, or text
             metrics       Return telemetry channels and samples
             telemetry     Analyze replay telemetry for FPS drops and memory spikes
             images        Return screenshot frames; alias: screenshots
             touches       Return recorded touch events
             trees         Return persisted visual-tree snapshots; alias: visual-trees
             artifacts     Return app-requested artifact snapshots
             annotations   Return user and agent annotations
             analyses      Return persisted session analyses

           Summary commands:
             summary       Start Cloud AI extraction for an existing team share
             summary --local | summary-local
                           Summarize local capture evidence with the brokered agent model;
                           save the result locally without sharing the capture

           Session list filters:
             --app-id <id>                  Exact application ID
             --connected                    Live sessions only
             --platform <key>               ios, android, macos, windows, or other; repeatable
             --device-form-factor <value>   Exact device form factor; repeatable
             --os-name <value>              Exact OS name; repeatable
             --os-version <value>           Exact OS version; repeatable
             --device-type <value>          physical, virtual, emulator, or simulator; repeatable
             --physical | --virtual | --emulator | --simulator
             --tag <tag>                    Require a session tag; repeatable
             --from <utc> | --to <utc>      Keep sessions whose capture interval overlaps the range
             --has-logs | --has-telemetry   Require retained evidence of that kind
             --search <text>                Search identity, app, device, OS, and tags
             --limit <count>                Page size from 1 to 1000; default: 20
             --cursor <cursor>              Continue from nextCursor in a previous JSON result

           Log filters:
             --start <utc> | --end <utc>    Inclusive timestamp range
             --priority <level>             Exact log priority
             --minimum-priority <level>     Minimum verbose/debug/information/warning/error/fatal priority
             --tag <tag>                    Exact tag; repeatable
             --source <source>              Exact source; repeatable
             --stream <id>                  Exact stream; repeatable
             --contains <text>              Message substring

           Network filters:
             --id <request-id>              Exact request identifier
             --method <method>              Exact HTTP method; repeatable
             --status <code|class>          Status code, 2xx/3xx/4xx/5xx, or failed; repeatable
             --host <host>                  Hostname substring
             --start <utc> | --end <utc>    Inclusive request start range
             --contains <text>              URL, method, or error substring
             --failed                       Requests with 4xx/5xx responses or transport errors
             --bodies                       Include captured request and response bodies in text output

           Direct mutation and evidence options:
             annotation upsert accepts --file <annotation.json>, or --label/--start with
             optional --annotation-id, --end, --notes, --status, and --source.
             annotations accepts --status <text> or --without-status to filter results.
             screenshot export selects --frame-id, --timestamp, or the latest frame.
             artifact export accepts optional --snapshot-id. Pass --force to replace output files.

           Telemetry analysis options:
             --kind <all|fps-drop|memory-spike>  Select detectors; default: all
             --memory-min-percent <percent>      Minimum relative memory increase; default: 15
             --memory-min-mb <megabytes>         Minimum absolute memory increase; default: 128
             --max-events <count>                Maximum findings returned per detector; default: 12
             --fail-on-detection                 Return exit code 11 when findings are present

           Management commands:
             normalize     Remove duplicate screenshot and per-type visual-tree sequences;
                           aliases: normalise, compact
             metadata      Update display name, notes, tags, or pinned state
             cache         Inspect, compact, or prune the recorded-session cache
             disconnect    Force a connected SDK session to disconnect
             delete        Permanently remove local session data; alias: remove

           Metadata options:
             --name <name>       Set the display name
             --notes <text>      Set notes; --clear-notes removes them
             --tag <tag>         Replace tags; may be repeated
             --clear-tags        Remove every tag
             --pin | --unpin     Change pinned state

           Cache management:
             status                Show cache usage, pinned/live counts, and cleanup candidates
             plan                  Preview the sessions selected by retention and cache limits
             prune                 Preview cleanup; add --apply to perform deletions
             compact               ZIP inactive sessions older than the compaction threshold
             --compaction-age-days <n>
                                   Compact inactive sessions this old; default: 30
             --retention-days <n>  Remove eligible sessions older than this; default: 90
             --max-cache <size>    Target cache limit such as 5GB or 5GiB; default: 5GiB
             --apply               Delete the planned sessions; pinned and live sessions stay protected

           Compacted sessions expand automatically when opened. Expansion resets their compaction
           age, so the default policy waits another 30 days before compacting them again.

           Replay and transfer:
             serve         Play one session in a capability-protected loopback browser
             share         Export and upload a team, authenticated, or public cloud replay
             share-batch   Upload 2-32 team sessions and send one email listing successful uploads
             summary       Start a portal-compatible AI summary for an existing cloud share
             summary-local Summarize local session evidence and save the result locally
             url           Resolve the newest existing cloud replay URL
             export        Write a portable ZIP archive
             sanitize      Write a PII-sanitized portable ZIP without modifying the local capture
             import        Import a portable ZIP archive into local session storage

           Share options:
             --team-id <uuid>                    Select an organisation by ID
             --team-name <name>                  Select an organisation by name
             --public                            Public replay shorthand
             --access <team|authenticated|public>
             --include-native-logs               Include native device logs in the upload
             --video                              Experimentally replace screenshots with an H.264 MP4
                                                  using the CLI's native platform encoder (macOS or Windows)
             --sanitize                           Apply the built-in PII policy before sharing
             --no-sanitize                        Explicitly retain the raw-capture default
             --sanitizer <module.ts>              Apply named sanitize functions from a local module
             --include-archived                  Allow archived shares when resolving a URL

           AI summary options:
             --local                             Analyze local evidence and save the summary locally
             --team-id <uuid>                    Select an organisation for the summary run
             --reasoning <fast|balanced|deep>     Select local summary reasoning; default: fast
             --mode <fast|thorough>               Cloud AI extraction depth
             --provider <openai|anthropic|gemini> Cloud AI provider
             --model <id>                        Cloud provider override or diagnostic local model override;
                                                 local override cannot be combined with --reasoning
             --source-part <part>                Repeat to override metadata, logs, and screenshot sources
             --slice-start-ms <value>             Inclusive extraction start offset
             --slice-end-ms <value>               Inclusive extraction end offset
             --instructions <text>                Override the portal-compatible reproduction prompt

           Sanitization:
             The built-in PII policy redacts logs, metadata, annotations, and visual trees; masks
             detected sensitive screenshot regions using OCR and visual trees, falling back to a
             full mask when neither can inspect a frame; sanitizes text artifacts and excludes
             binary artifacts; and writes sanitization/report.json into the archive. Share and
             export use it only with --sanitize. A custom --sanitizer replaces the built-in policy.
             The dedicated sanitize command uses the built-in policy when no module is supplied.

           Use --json for structured evidence. Live SDK tools that are not dedicated session verbs
           are available through `ansight app tools`, `ansight app call`, and `ansight ui`.
           Find session IDs with `ansight session list`; add `--connected` for live sessions only.
           """;

}

internal enum TelemetryAnalysisSelection
{
    All,
    FpsDrop,
    MemorySpike
}
