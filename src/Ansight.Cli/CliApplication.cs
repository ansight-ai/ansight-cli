using System.Diagnostics;
using System.Runtime.InteropServices;
using Ansight.Cli.Commands.Audio;

namespace Ansight.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        var arguments = CliArguments.Parse(args);
        CliAnalytics? analytics = null;
        var analyticsStopwatch = Stopwatch.StartNew();
        var analyticsExitCode = CliExitCodes.Failure;
        IDisposable? logging = null;
        var originalStandardOutput = Console.Out;
        var originalStandardError = Console.Error;
        if (arguments.IsSilent)
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }

        try
        {
            logging = CliLogging.Configure(arguments, arguments.IsVerbose, arguments.IsSilent);
            var output = new CliOutput(arguments.IsJson, silent: arguments.IsSilent);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            using var terminateRegistration = RegisterTerminationSignal(cancellation);

            try
            {
                if (ShouldInitializeAnalytics(arguments)
)
                {
                    analytics = CliAnalytics.TryCreate(arguments);
                }

                var exitCode = await RunParsedAsync(
                    arguments,
                    output,
                    cancellation.Token,
                    allowResidentHostForwarding: true,
                    standardInput: Console.In,
                    standardInputIsInteractive: !Console.IsInputRedirected).ConfigureAwait(false);

                if (exitCode == CliExitCodes.Success && ShouldSuggestUpdate(arguments))
                {
                    await CliUpdateSuggestion.TryWriteAsync(output, cancellation.Token)
                        .ConfigureAwait(false);
                }

                analyticsExitCode = exitCode;
                return exitCode;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        finally
        {
            try
            {
                if (analytics is not null)
                {
                    await analytics.TrackCompletionAsync(
                        arguments,
                        analyticsExitCode,
                        analyticsStopwatch.Elapsed).ConfigureAwait(false);

                }
            }
            catch
            {
                // Analytics must never affect CLI behavior or output.
            }
            finally
            {
                logging?.Dispose();
            }

            if (arguments.IsSilent)
            {
                Console.SetOut(originalStandardOutput);
                Console.SetError(originalStandardError);
            }
        }
    }

    internal static async Task<int> RunParsedAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken,
        bool allowResidentHostForwarding,
        TextReader? standardInput = null,
        bool standardInputIsInteractive = false,
        ICliAccessAuthorizer? accessAuthorizer = null,
        Func<CliArguments, CliOutput, CancellationToken, Task<int?>>? residentHostForwarder = null,
        TimeProvider? timeProvider = null,
        CliAccessLease? existingLease = null)
    {
        try
        {
            var accessClass = CliCommandAccessPolicy.Classify(arguments, CliCommandAccessPolicy.HasRunnerCredentials);
            if (accessClass == CliAccessClass.ResidentHost)
            {
                return await HostCommands.RunRecoverableHostAsync(arguments, output, cancellationToken,
                    CliLocalAccessAuthorizer.Instance,
                    timeProvider ?? TimeProvider.System).ConfigureAwait(false);
            }
            if (accessClass == CliAccessClass.Bootstrap)
            {
                await using var localLease = CliAccessLease.CreateLocal(cancellationToken, timeProvider);
                using var localContext = CliAccessContext.Push(CliLocalAccessAuthorizer.Instance, localLease);
                return await RunAuthorizedAsync(arguments, output, localLease.Token,
                    allowResidentHostForwarding, standardInput, standardInputIsInteractive,
                    residentHostForwarder).ConfigureAwait(false);
            }

            // Cloud identity belongs to the calling process. Never reuse the local host's
            // account-free lifetime or forward the caller's cloud credentials to it.
            allowResidentHostForwarding = false;
            accessAuthorizer = accessClass == CliAccessClass.Runner
                ? Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<ICliAccessAuthorizer>("Access.CreateRunnerAuthorizer", [arguments])
                : accessAuthorizer is CliLocalAccessAuthorizer || accessAuthorizer is null
                    ? Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<ICliAccessAuthorizer>("Access.CreateAccountAuthorizer", [CliRuntime.ResolveOptions(arguments)])
                    : accessAuthorizer;
            await using var lease = await CliAccessLease.CreateAsync(accessAuthorizer, cancellationToken, timeProvider)
                .ConfigureAwait(false);
            using var accessContext = CliAccessContext.Push(accessAuthorizer, lease);
            try
            {
                var result = await RunAuthorizedAsync(arguments, output, lease.Token, allowResidentHostForwarding,
                    standardInput, standardInputIsInteractive, residentHostForwarder).ConfigureAwait(false);
                return lease.IsDenied ? lease.Failure.WriteFailure(output) : result;
            }
            catch (OperationCanceledException) when (lease.IsDenied)
            {
                return lease.Failure.WriteFailure(output);
            }
        }
        catch (CliAccessDeniedException exception)
        {
            return exception.Decision.WriteFailure(output);
        }
        catch (CliUsageException exception)
        {
            output.WriteError("usage", exception.Message, CliExitCodes.Usage);
            return CliExitCodes.Usage;
        }
        catch (CliHostUnavailableException exception)
        {
            output.WriteError("host_unavailable", exception.Message, CliExitCodes.HostUnavailable);
            return CliExitCodes.HostUnavailable;
        }
        catch (Ansight.Infrastructure.Extensions.OptionalExtensionUnavailableException exception)
        {
            output.WriteError("capability_unavailable", exception.Message, CliExitCodes.CapabilityUnavailable);
            return CliExitCodes.CapabilityUnavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.WriteError("cancelled", "Operation cancelled.", CliExitCodes.Cancelled);
            return CliExitCodes.Cancelled;
        }
        catch (Exception)
        {
            // The command executor handles feature errors. Fail closed if constructing or
            // verifying its access lifetime fails, without exposing credential-provider errors.
            return AccessDecision.Unavailable.WriteFailure(output);
        }
    }

    private static bool MayPromptBeforeForwarding(CliArguments arguments)
        => arguments.Positionals.Count > 1
           && arguments.Positionals[0].ToLowerInvariant() is "workspace" or "workspaces"
           && arguments.Positionals[1].ToLowerInvariant() is "init" or "initialize";

    private static async Task<int> RunAuthorizedAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken,
        bool allowResidentHostForwarding,
        TextReader? standardInput,
        bool standardInputIsInteractive,
        Func<CliArguments, CliOutput, CancellationToken, Task<int?>>? residentHostForwarder)
    {
        try
        {
            ModelTransportOptions.Validate(arguments);
            ReasoningOptions.ValidateForCommand(arguments);
            if (arguments.Positionals.Count == 0)
            {
                if (arguments.HasFlag("version"))
                {
                    return await CliVersionCommand.RunAsync(arguments, output, cancellationToken)
                        .ConfigureAwait(false);
                }

                output.WriteText(BuildHelp(arguments.HasFlag("beta")));
                return CliExitCodes.Success;
            }

            var isHelpRequest = CliCommandHelp.IsRequested(arguments)
                                || string.Equals(
                                    arguments.Positionals[0],
                                    "help",
                                    StringComparison.OrdinalIgnoreCase)
                                || arguments.Positionals.Count == 1
                                && arguments.Positionals[0].ToLowerInvariant()
                                    is "auth" or "companion" or "remote" or "remote-simulator";
            if (arguments.Positionals[0].Equals("replay", StringComparison.OrdinalIgnoreCase))
            {
                ReplayCommands.ValidateOptions(arguments);
            }

            arguments = WorkspaceRegistrationPrompt.Prepare(
                arguments,
                output,
                standardInput,
                standardInputIsInteractive);
            arguments = await AppGraphTargetPrompt.PrepareAsync(
                arguments,
                output,
                standardInput,
                standardInputIsInteractive,
                cancellationToken).ConfigureAwait(false);

            var commandName = arguments.Positionals[0].ToLowerInvariant();
            if (AppInteractionCommand.IsMatch(arguments))
            {
                return await AppInteractionCommand.RunAsync(arguments, output,
                    standardInput ?? TextReader.Null, cancellationToken).ConfigureAwait(false);
            }
            var isLocalAccountCommand = commandName == "account"
                                        && (arguments.Positionals.Count < 2
                                            || arguments.Positionals[1].ToLowerInvariant() is not ("grant" or "grants"));
            var isLocalHostInspectionCommand = commandName == "host"
                                               && arguments.Positionals.Count >= 2
                                               && arguments.Positionals[1].ToLowerInvariant()
                                                   is "status" or "log" or "logs" or "desktop-restart";
            var isLocalCloudArtifactCommand = commandName == "cloud"
                                              && arguments.Positionals.Count >= 2
                                              && arguments.Positionals[1].ToLowerInvariant()
                                                  is "build" or "workspace";
            var isLocalCommand = commandName
                                     is "version" or "info" or "update" or "upgrade"
                                     or "doctor" or "capabilities"
                                     or "runner" or "runners"
                                     or "analytics"
                                     or "config" or "settings" or "setup"
                                     or "license" or "licenses" or "licence" or "licences" or "notice" or "notices" or "attribution" or "attributions"
                                 || isLocalAccountCommand
                                 || isLocalCloudArtifactCommand;
            isLocalCommand |= isLocalHostInspectionCommand;
            if (allowResidentHostForwarding && !isHelpRequest && !isLocalCommand)
            {
                residentHostForwarder ??= ControlClient.TryRunAsync;
                var forwardedExitCode = await residentHostForwarder(
                    arguments,
                    output,
                    cancellationToken).ConfigureAwait(false);
                if (forwardedExitCode is not null)
                {
                    return forwardedExitCode.Value;
                }
            }

            using var deviceLaunchContext = DeviceLaunchContext.Push(arguments.HasFlag("headless"));
            var command = arguments.Positionals[0].ToLowerInvariant();
            return command switch
            {
                "host" => await HostCommands.RunAsync(arguments, output, cancellationToken),
                "serve" => await ServeCommand.RunAsync(arguments, output, cancellationToken),
                "doctor" or "capabilities" => await DoctorCommand.RunAsync(arguments, output, cancellationToken),
                "version" or "info" => await CliVersionCommand.RunAsync(arguments, output, cancellationToken),
                "update" or "upgrade" => await UpdateCommands.RunAsync(arguments, output, cancellationToken),
                "analytics" => AnalyticsCommands.Run(arguments, output),
                "setup" => await Commands.Setup.SetupCommands.RunAsync(arguments, output, standardInput, standardInputIsInteractive, cancellationToken),
                "config" or "settings" => ConfigCommands.Run(arguments, output, standardInput, standardInputIsInteractive),
                "device" or "devices" => await DeviceCommands.RunAsync(arguments, output, cancellationToken),
                "input" => await DeviceInputCommands.RunAsync(arguments, output, cancellationToken),
                "ui" => await UiCommands.RunAsync(arguments, output, cancellationToken),
                "keyboard" => await KeyboardCommands.RunAsync(arguments, output, cancellationToken),
                "audio" => await AudioCommands.RunAsync(arguments, output, cancellationToken),
                "app" => await AppCommands.RunAsync(arguments, output, cancellationToken),
                "app-graph" or "app-graphs" => await AppGraphCommands.RunAsync(arguments, output, cancellationToken),
                "pairing" or "enrollment" => await PairingCommands.RunAsync(arguments, output, cancellationToken),
                "companion" or "remote" or "remote-simulator" => await CompanionCommands.RunAsync(arguments, output, cancellationToken),
                "profile" or "profiling" => await ProfilingCommands.RunAsync(arguments, output, cancellationToken),
                "repo" or "repository" => await RepositoryCommands.RunAsync(arguments, output, cancellationToken),
                "runner" or "runners" => await RunnerCommands.RunAsync(
                    arguments,
                    output,
                    cancellationToken,
                    standardInput,
                    standardInputIsInteractive),
                "task" or "tasks" => await TaskCommands.RunAsync(arguments, output, cancellationToken),
                "replay" => await ReplayCommands.RunAsync(arguments, output, cancellationToken),
                "workspace" or "workspaces" => await WorkspaceCommands.RunAsync(arguments, output, cancellationToken),
                "auth" => await AuthCommands.RunAsync(arguments, output, cancellationToken),
                "account" => await AccountCommands.RunAsync(arguments, output, cancellationToken),
                "secret" or "secrets" => await SecretCommands.RunAsync(arguments, output, cancellationToken),
                "test" or "tests" => await TestCommands.RunAsync(arguments, output, cancellationToken),
                "trends" => await TrendsCommands.RunAsync(arguments, output, cancellationToken),
                "session" or "sessions" => await SessionCommands.RunAsync(arguments, output, cancellationToken),
                "artifact" or "artifacts" => await Commands.ArtifactComparison.ArtifactCommands.RunAsync(arguments, output, cancellationToken),
                "cloud" => await CloudCommands.RunAsync(arguments, output, cancellationToken),
                "license" or "licenses" or "licence" or "licences" or "notice" or "notices" or "attribution" or "attributions"
                    => await LicensesCommands.RunAsync(arguments, output, cancellationToken),
                "help" => WriteHelp(arguments, output),
                _ => throw new CliUsageException(
                    $"Unknown command '{arguments.Positionals[0]}'. Run 'ansight help' for usage.")
            };
        }
        catch (CliUsageException exception)
        {
            output.WriteError("usage", exception.Message, CliExitCodes.Usage);
            return CliExitCodes.Usage;
        }
        catch (CliHostUnavailableException exception)
        {
            output.WriteError("host_unavailable", exception.Message, CliExitCodes.HostUnavailable);
            return CliExitCodes.HostUnavailable;
        }
        catch (Ansight.Infrastructure.Extensions.OptionalExtensionUnavailableException exception)
        {
            output.WriteError("capability_unavailable", exception.Message, CliExitCodes.CapabilityUnavailable);
            return CliExitCodes.CapabilityUnavailable;
        }
        catch (OperationCanceledException) when (CliAccessContext.Current?.Lease.IsDenied == true)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.WriteError("cancelled", "Operation cancelled.", CliExitCodes.Cancelled);
            return CliExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            output.WriteError("failure", exception.GetBaseException().Message, CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }
    }

    private static int WriteHelp(CliArguments arguments, CliOutput output)
    {
        output.WriteText(BuildHelp(arguments.HasFlag("beta")));
        return CliExitCodes.Success;
    }

    internal static bool ShouldSuggestUpdate(CliArguments arguments)
    {
        if (AppInteractionCommand.IsMatch(arguments)
            || IsFullSystemReport(arguments)
            || arguments.IsJson
            || arguments.IsSilent
            || Console.IsOutputRedirected
            || Console.IsErrorRedirected
            || string.Equals(
                Environment.GetEnvironmentVariable("ANSIGHT_NO_UPDATE_CHECK"),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (arguments.Positionals.Count == 0)
        {
            return false;
        }

        return arguments.Positionals[0].ToLowerInvariant()
            is not ("help" or "version" or "update" or "upgrade");
    }



    internal static bool ShouldInitializeAnalytics(CliArguments arguments)
    {
        // Full diagnostics must not open/migrate credential stores for analytics.
        if (IsFullSystemReport(arguments)) return false;
        if (arguments.Positionals.Count == 0)
        {
            return arguments.HasFlag("version");
        }

        return !CliCommandHelp.IsRequested(arguments)
               && !string.Equals(
                   arguments.Positionals[0],
                   "help",
                   StringComparison.OrdinalIgnoreCase)
               && !(arguments.Positionals.Count == 1
                    && arguments.Positionals[0].ToLowerInvariant()
                        is "companion" or "remote" or "remote-simulator")
               && !(arguments.Positionals[0].Equals("account", StringComparison.OrdinalIgnoreCase)
                    && (arguments.Positionals.Count == 1
                        || arguments.Positionals[1].ToLowerInvariant() is "open" or "portal"));
    }

    private static bool IsFullSystemReport(CliArguments arguments)
        => arguments.HasFlag("full") && arguments.Positionals.Count > 0
            && arguments.Positionals[0].ToLowerInvariant() is "doctor" or "capabilities";

    private static IDisposable? RegisterTerminationSignal(CancellationTokenSource cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
    }

    private static string BuildHelp(bool includeBetaFeatures)
    {
        var help = """
           Ansight CLI — local app inspection, automation, and cloud client

           Usage:
             ansight <command> [subcommand] [arguments] [options]

           Getting help:
             ansight <command> help            Detailed commands, options, requirements, and examples
             ansight help --beta               Include experimental feature commands in this index

           Local tools and optional cloud account management:
             ansight help                         Show this command index
             ansight <command> help|--help        Show detailed help for any command family
             ansight version|doctor [--json]      Inspect the local CLI and its capabilities
             ansight host status|stop [options]   Inspect host state or stop the local host
             ansight setup android|credentials    Optional Android tooling or credential setup
             ansight config credentials          Configure persistent credential storage
             ansight config list|get|set|unset    Inspect or change local CLI defaults
             ansight account [open]               Open the Ansight account portal
             ansight account access [--json]      Verify cloud sign-in and token expiry
             ansight account login [options]      Sign in; alias for `ansight auth login`
             ansight account refresh|status       Refresh or inspect the saved account session
             ansight account logout [--local]     Sign out or clear only local credentials
             ansight account machines list        List registered machines for an organisation
             ansight auth login [options]         Sign in using browser, email, token, or device flow
             ansight auth refresh|status          Refresh or inspect the saved account session
             ansight auth logout [--local]        Sign out or clear only local credentials

             All local developer features are free and require no account.
             Sign in for cloud uploads, session sharing and remote job delegation.

           Setup and account:
             version                          Show CLI build, runtime, and executable location; alias: info
             update check|apply               Check for or install CLI updates
             setup android|credentials       Configure missing machine prerequisites
             config list|get|set|unset        Inspect or change local CLI defaults; alias: settings
             account open|access|grants|usage|machines
                                              Open the portal or manage account access and machines
             auth login|signin|refresh|status|logout|signout
                                              Manage the headless account session
             analytics status|detailed        Inspect or change detailed usage tracking
             doctor                           Inspect platform and optional tool capabilities
             licenses list|export             Show or export software attributions and licenses

           Local host and targets:
             host run|status|logs|stop        Run and inspect the reusable Ansight host and find its logs
             host run --pair <app-id>         Start the host and show a phone pairing QR
             serve                            Open the local session explorer and live inspector
             device list|start|shutdown       Discover and control devices; listings are live-only by default
             device apps|install|launch|terminate|location|screenshot
             input tap|swipe|pinch|text|button
                                              Send real simulator or device input
             ui snapshot|find|tap|type|swipe|pinch|back|wait|assert
                                              Inspect and drive live UI through semantic selectors
             keyboard open|is-open|dismiss    Inspect and control the system software keyboard
             audio capabilities|inject       Inject a WAV into a connected virtual device microphone
             pairing issue|list|get|revoke    Manage SDK enrollment invites, codes, and QR capture
             companion access|machines|connections
                                              Manage the remote-control companion app

           Apps, sessions, and evidence:
             artifact list|diff              Find captured artifacts and compare qualified references
             app list|get|register|remove     Manage linked application metadata
             app tools|call|batch|execute     Inspect, invoke, or agentically operate a connected app
             app push-file                    Transfer files through a connected app
             session list|show|logs|network|metrics
             session telemetry analyze|images|touches|trees|artifacts
             session annotations|analyses|extract|trim|normalize|metadata|delete|disconnect
             session annotation|analysis|screenshot|artifact
             session cache status|plan|prune|compact
                                              Manage retention and recorded-session cache size
             session serve|share|summary|url  Play, share, or summarize replay evidence
             session import|export|sanitize   Move portable or sanitized session ZIP files

           Workspace:
             workspace list                   Show app registrations linked to trusted codebases
             workspace init|add               Scaffold tests, trends, sanitizers, tasks, and triggers

           Automation and testing:
             test list|validate|run|run-inline|run-all
             test history|inspect|export
                                              Run workspace tests and inspect or export their results
             trends history|rebuild      Inspect metrics or rebuild historical comparisons
             task list|run|extract            Discover, run, or extract repeatable tasks; alias: tasks
             repo tasks|automation            Manage repository task sources and triggers
             runner setup|start|submit        Set up a machine and execute durable typed jobs
             secret list|set|remove           Manage test secrets without exposing values
           Cloud:
             cloud team|app|session           Inspect organisations, registered apps, and shared captures
             cloud trends|test|key       Register CI keys and upload cloud data
             cloud attachment|analysis       Administer shared captures and hosted computation

           Output and behavior options:
             --version                        Show CLI version, daily build number, and channel
             --beta                           Include beta feature commands in help
             --json                           Emit versioned machine-readable JSON
             --silent                         Suppress all stdout and stderr output
             --verbose                        Include debug, telemetry, and app-event logs
             --headless                       Do not open simulator/emulator windows during this command

           Host configuration options:
             --data-dir <path>                Override the shared Ansight state directory
             --adb-path <path>                Explicit ADB executable or SDK directory
             --xcode-path <path>              Explicit Xcode app or Developer directory
             --secret-store-file <path>       Use an encrypted file instead of the OS vault
             --secret-key-file <path>         Read its AES key from a protected owner-only file
             --discovery-port <port>          UDP discovery port
             --websocket-port <port>          SDK WebSocket port
             --companion-access [session|always]
                                              Publish simulators to the companion app (macOS host run)
             --enable-repository-automations  Enable trusted repository triggers (default)
             --disable-repository-automations Disable repository trigger execution for this host
             --automation-repository <path>   Repository to connect when the host starts (repeatable)
             --node-path <path>               Node.js runtime for repository modules
           Discovery shortcuts:
             ansight app list                 App bundle/package IDs known to Ansight
             ansight device list              Live device IDs and states; pass --all to include offline targets
             ansight session list --connected Live session IDs and their app IDs
             ansight app tools <session-id>   Tool IDs exposed by a connected app
             ansight test list <workspace>    Test IDs declared in an Ansight workspace
           """;

        if (!includeBetaFeatures)
        {
            return help;
        }

        const string outputOptionsHeading = "\nOutput and behavior options:";
        const string betaFeatureHelp = """

Beta feature commands:
  App Graphs:
    app-graph list|show|plan        Inspect and compile organisation navigation graphs
    app-graph validate|run|explore  Validate, execute, or grow a graph against a live app
    cloud app-graph                 Inspect organisation navigation graphs in the cloud

  Agentic replay:
    replay ansight|sentry|posthog   Reproduce recorded flows through the bounded UI agent

  .NET profiling:
    profile dotnet tools|start|status|cancel|list|manifest|import|speedscope
                                    Capture and manage .NET EventPipe traces
    profile dotnet overview|startup|cpu|call-tree|threads|gc|jit|exceptions
                                    Analyze captured .NET traces

  Native mobile profiling:
    profile ios tools|start|status|cancel|list|manifest
                                    Capture native Instruments profiles
    profile android tools|start|status|cancel|list|manifest
                                    Capture native Perfetto traces

  Local process sampling:
    profile sample tools|start|status|cancel|list|manifest|artifact
                                    Capture macOS sampled thread stacks and call graphs
""";
        return help.Replace(
            outputOptionsHeading,
            betaFeatureHelp + Environment.NewLine + outputOptionsHeading,
            StringComparison.Ordinal);
    }
}
