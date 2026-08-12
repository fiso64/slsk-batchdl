using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;
using Sockseek.Core.Snapshots;

namespace Sockseek.Cli;

internal static partial class Program
{
    internal enum CliExitCode
    {
        Success = 0,
        WorkFailed = 1,
        UsageError = 2,
        Cancelled = 130,
    }

    public static async Task<int> Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        SockseekLog.SetupExceptionHandling();
        using var output = CliOutputController.Install(args);

        try
        {
            CliExitCode? configuredCommand = await ConfiguredCommandDispatcher.TryRunAsync(args)
                .ConfigureAwait(false);
            if (configuredCommand is not null)
                return (int)configuredCommand.Value;
            if (Help.PrintAndExitIfNeeded(args))
                return (int)CliExitCode.Success;
            return (int)await MainCore(args, output);
        }
        catch (Exception ex)
        {
            SockseekLog.Fatal($"Unhandled CLI startup error: {SockseekLog.ExceptionSummary(ex)}");
            return (int)CliExitCode.WorkFailed;
        }
    }

    internal static async Task<CliExitCode> MainCore(string[] args)
    {
        using var output = CliOutputController.CreateDetached();
        return await MainCore(args, output, CancellationToken.None);
    }

    internal static async Task<CliExitCode> MainCore(
        string[] args,
        CancellationToken cancellationToken)
    {
        using var output = CliOutputController.CreateDetached();
        return await MainCore(args, output, cancellationToken);
    }

    internal static Task<CliExitCode> MainCore(string[] args, CliOutputController output)
        => MainCore(args, output, CancellationToken.None);

    internal static async Task<CliExitCode> MainCore(
        string[] args,
        CliOutputController output,
        CancellationToken cancellationToken)
    {
        bool daemonMode = args.Length > 0 && string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase);
        var bindArgs = daemonMode ? args.Skip(1).ToArray() : args;

        ConfigFile configFile;
        EngineSettings engineSettings;
        DownloadSettings rootSettings;
        CliSettings cliSettings;
        DaemonSettings daemonSettings;
        RemoteSettings remoteSettings;

        // TODO [ARCHITECTURE]: Replace scattered CLI/server validation exception handling
        // with typed diagnostics carrying severity, exit-code class, output stream, and
        // optional debug detail. Parser, daemon startup, remote startup, and extractor
        // validation currently encode that policy in several catch/log branches.
        try
        {
            (configFile, engineSettings, rootSettings, cliSettings, daemonSettings, remoteSettings) =
                ConfigManager.LoadAndBindAll(bindArgs);
            ConfigManager.ApplyAutoProfileCliSettings(configFile, rootSettings, cliSettings);
            ApplyMockFilesDefaults(engineSettings, rootSettings);
        }
        catch (Exception ex) when (ex is ArgumentException || ex.Message.StartsWith("Input error:"))
        {
            SockseekLog.Error(ex.Message);
            return CliExitCode.UsageError;
        }

        string? profileArg = ConfigManager.ExtractProfileName(bindArgs);
        if (profileArg != null)
        {
            var requestedProfiles = profileArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (requestedProfiles.Contains("help", StringComparer.OrdinalIgnoreCase))
            {
                if (remoteSettings.IsEnabled)
                {
                    try
                    {
                        using var http = SockseekApiClient.CreateHttpClient(remoteSettings.ServerUrl!);
                        var api = new SockseekApiClient(http, RemoteCliBackend.CreateJsonOptions());
                        var profiles = await api.GetProfilesAsync();

                        if (profiles.Count == 0)
                            Console.WriteLine("No profiles found on remote daemon.");
                        else
                        {
                            Console.WriteLine($"Available profiles on remote daemon ({remoteSettings.ServerUrl}):");
                            foreach (var p in profiles)
                                Console.WriteLine($"  {p.Name}{(p.IsAutoProfile ? " (auto)" : "")}");
                        }
            }
            catch (Exception ex)
            {
                SockseekLog.Error($"Failed to retrieve profiles from remote daemon: {SockseekLog.ExceptionSummary(ex)}");
                return CliExitCode.WorkFailed;
            }
                }
                else
                {
                    var profiles = ConfigManager.GetProfileNames(configFile);
                    if (profiles.Count == 0)
                        Console.WriteLine("No profiles found in local config.");
                    else
                    {
                        Console.WriteLine("Available profiles:");
                        foreach (var p in profiles)
                            Console.WriteLine($"  {p}");
                    }
                }
                return CliExitCode.Success;
            }
        }

        if (!string.IsNullOrWhiteSpace(engineSettings.LogFilePath))
            SockseekLog.AddOrReplaceFile(engineSettings.LogFilePath, engineSettings.LogLevel < LogLevel.Debug ? engineSettings.LogLevel : LogLevel.Debug);

        SockseekLog.SetConsoleLogLevel(rootSettings.NonVerbosePrint ? LogLevel.Error : engineSettings.LogLevel);

        if (daemonMode)
        {
            try
            {
                await RunDaemonAsync(bindArgs, configFile, engineSettings, rootSettings, daemonSettings);
            }
            catch (ArgumentException ex)
            {
                SockseekLog.Error(ex.Message);
                return CliExitCode.UsageError;
            }
            catch (DaemonEndpointUnavailableException ex)
            {
                SockseekLog.Error(ex.Message);
                return CliExitCode.WorkFailed;
            }
            catch (Exception ex)
            {
                SockseekLog.Fatal($"Unhandled daemon error: {SockseekLog.ExceptionSummary(ex)}");
                return CliExitCode.WorkFailed;
            }
            return CliExitCode.Success;
        }

        LogCliSessionStart(remoteSettings);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (cliSettings.Monitor)
        {
            if (!remoteSettings.IsEnabled)
            {
                SockseekLog.Error(
                    "Monitor mode requires a configured remote URL "
                    + "(remote = <url> or --remote <url>).");
                return CliExitCode.UsageError;
            }

            try
            {
                return await RunRemoteMonitorAsync(
                    bindArgs,
                    engineSettings,
                    rootSettings,
                    cliSettings,
                    remoteSettings,
                    output,
                    cts);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return CliExitCode.Cancelled;
            }
            catch (Exception ex)
            {
                SockseekLog.Error($"Remote monitor failed: {SockseekLog.ExceptionSummary(ex)}");
                return CliExitCode.WorkFailed;
            }
        }

        if (remoteSettings.IsEnabled)
        {
            try
            {
                return await RunRemoteAsync(bindArgs, engineSettings, rootSettings, cliSettings, remoteSettings, output, cts);
            }
            catch (SockseekApiRequestException ex)
            {
                SockseekLog.Error(ex.Message);
                return CliExitCode.WorkFailed;
            }
            catch (Exception ex)
            {
                SockseekLog.Fatal($"Unhandled remote CLI error: {SockseekLog.ExceptionSummary(ex)}");
                return CliExitCode.WorkFailed;
            }
        }

        var clientManager = new SoulseekClientManager(engineSettings);

        if (string.IsNullOrEmpty(rootSettings.Extraction.Input))
        {
            var diagnostic = new DiagnosticService(clientManager);
            try
            {
                await diagnostic.PerformNoInputActions(rootSettings.PrintOption, rootSettings.Output.IndexFilePath, cts.Token);
            }
            catch (Exception ex)
            {
                SockseekLog.Error($"Diagnostic action failed: {SockseekLog.ExceptionSummary(ex)}");
            }

            if (!rootSettings.PrintOption.HasFlag(PrintOption.Index))
            {
                SockseekLog.Error("Input error: No input provided.");
                Help.PrintAndExitIfNeeded([]);
                return CliExitCode.UsageError;
            }
            return CliExitCode.Success;
        }

        IJobSettingsResolver jobSettingsResolver;
        try
        {
            jobSettingsResolver = ConfigManager.CreateJobSettingsResolver(configFile, bindArgs, cliSettings);
            if (!string.IsNullOrEmpty(engineSettings.MockFilesDir))
                jobSettingsResolver = new MockFilesJobSettingsResolver(jobSettingsResolver);
        }
        catch (Exception ex) when (ex is ArgumentException || ex.Message.StartsWith("Input error:"))
        {
            SockseekLog.Error(ex.Message);
            return CliExitCode.UsageError;
        }

        var localSubmissionOptionsResolver = new SubmissionOptionsJobSettingsResolver(
            jobSettingsResolver,
            normalize: settings => SettingsNormalizer.NormalizeDownloadPaths(settings, settings.RuntimePathContext));

        bool attachHumanProgressReporter = ShouldAttachHumanProgressReporter(rootSettings.PrintOption);
        if (attachHumanProgressReporter)
        {
            output.ConfigureLiveRendering(cliSettings, engineSettings.LogLevel);
            // Only the foreground local renderer replaces the engine's plain interval
            // progress logs. Daemon and remote execution must retain their own policy.
            if (output.WillUseLiveRendering)
                engineSettings.ReportIntervalProgress = false;
        }

        var engine = new DownloadEngine(engineSettings, clientManager, localSubmissionOptionsResolver);
        var backend = new LocalCliBackend(
            engine,
            rootSettings,
            localSubmissionOptionsResolver,
            ConfigManager.CreateCliDownloadSettingsPatch(bindArgs));

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else if (attachHumanProgressReporter)
        {
            cliReporter = new CliProgressReporter(cliSettings, output);
            cliReporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, includeDiagnosticDetails: engineSettings.LogLevel <= LogLevel.Debug);
        eventLogger.Attach();

        backend.ActivityReceived += activity =>
        {
            if (activity.Payload is TrackBatchResolvedActivityDto batch
                && batch.PrintOption == PrintOption.None
                && ShouldPrintHumanBatchPreview(batch.PrintOption)
                && cliReporter?.UsesLiveRendering != true)
            {
                PrintCompactTrackBatchResolved(activity, batch);
            }
        };

        Task? interactiveCoordinatorTask = null;
        if (cliSettings.InteractiveMode)
        {
            var workflowId = Guid.NewGuid();
            var coordinator = new InteractiveCliCoordinator(backend, cliSettings, cts.Token);
            var submission = await coordinator.StartAsync(
                new SubmitExtractJobRequestDto(
                    rootSettings.Extraction.Input,
                    rootSettings.Extraction.InputType.ToString(),
                    Options: new SubmissionOptionsDto(workflowId)),
                cts.Token);
            interactiveCoordinatorTask = coordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token);
            _ = interactiveCoordinatorTask
                .ContinueWith(_ => engine.CompleteEnqueue(), TaskScheduler.Default);
        }
        else
        {
            await backend.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    rootSettings.Extraction.Input,
                    rootSettings.Extraction.InputType.ToString()),
                cts.Token);
            engine.CompleteEnqueue();
        }

        using var consoleControls = StartConsoleControls(
            backend,
            cliReporter,
            workflowId: null,
            cancelPrompt: "Cancel job ID or all jobs? id/[A]ll/n: ",
            cancelAllMessage: "Cancelling all jobs...",
            cancelAll: _ =>
            {
                engine.Cancel();
                return Task.CompletedTask;
            },
            cts);

        try
        {
            await engine.RunAsync(cts.Token);
            if (interactiveCoordinatorTask != null)
                await interactiveCoordinatorTask;

            SockseekLog.Trace("Main: RunAsync returned.");
            bool hasDownloadableJobs = PrintOutputRenderer.HasDownloadableJobs(engine.Queue);
            bool hasRequestedOutput = PrintOutputRenderer.HasRequestedOutput(engine.Queue);

            if (hasDownloadableJobs)
                Printing.PrintComplete(engine.Queue);

            if (hasRequestedOutput)
            {
                cliReporter?.Stop(printSummary: hasDownloadableJobs);
                cliReporter = null;
                PrintOutputRenderer.PrintRequestedOutput(engine.Queue);
            }

            var exitCode = LogCliSessionExit(DetermineLocalExitCode(engine.Queue));
            cliReporter?.Stop();
            cliReporter = null;

            return exitCode;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return LogCliSessionExit(CliExitCode.Cancelled);
        }
        catch (SoulseekConnectionUnavailableException ex)
        {
            SockseekLog.Error(ex.Message);
            return LogCliSessionExit(CliExitCode.WorkFailed);
        }
        catch (Exception ex)
        {
            SockseekLog.Fatal($"Unhandled CLI error: {SockseekLog.ExceptionSummary(ex)}");
            return LogCliSessionExit(CliExitCode.WorkFailed);
        }
        finally
        {
            SockseekLog.Trace("Main: Entered finally block. Disposing clientManager...");
            engine.Cancel();
            await engine.DisposeAsync();
            await cts.CancelAsync();
            cliReporter?.Stop();
            clientManager.Dispose();
            Printing.SetBuffering(false);
            SockseekLog.Trace("Main: ClientManager disposed.");
            SockseekLog.Trace("Main: Exiting.");
        }
    }

    internal static bool ArgsRequestProgressJson(IReadOnlyList<string> args)
        => CliOutputController.ArgsRequestProgressJson(args);

    private static void ApplyMockFilesDefaults(EngineSettings engineSettings, DownloadSettings downloadSettings)
    {
        if (!string.IsNullOrEmpty(engineSettings.MockFilesDir))
            downloadSettings.Search.MinSharesAggregate = 1;
    }

    private sealed class MockFilesJobSettingsResolver(IJobSettingsResolver inner) : IJobSettingsResolver
    {
        public DownloadSettings Resolve(DownloadSettings inherited, Job job)
        {
            var settings = inner.Resolve(inherited, job);
            settings.Search.MinSharesAggregate = 1;
            return settings;
        }
    }

    private static async Task<CliExitCode> RunRemoteAsync(
        string[] args,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        CliSettings cliSettings,
        RemoteSettings remoteSettings,
        CliOutputController output,
        CancellationTokenSource cts)
    {
        if (string.IsNullOrWhiteSpace(rootSettings.Extraction.Input))
        {
            SockseekLog.Error("Remote mode requires an input.");
            return CliExitCode.UsageError;
        }

        await using var backend = new RemoteCliBackend(remoteSettings.ServerUrl!);
        await backend.StartAsync(cts.Token);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else if (ShouldAttachHumanProgressReporter(rootSettings.PrintOption))
        {
            output.ConfigureLiveRendering(cliSettings, engineSettings.LogLevel);
            cliReporter = new CliProgressReporter(cliSettings, output);
            cliReporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, includeDiagnosticDetails: false);
        eventLogger.Attach();

        backend.ActivityReceived += activity =>
        {
            if (activity.Payload is TrackBatchResolvedActivityDto batch
                && batch.PrintOption == PrintOption.None
                && ShouldPrintHumanBatchPreview(batch.PrintOption)
                && cliReporter?.UsesLiveRendering != true)
            {
                PrintCompactTrackBatchResolved(activity, batch);
            }
        };

        try
        {
            Guid workflowId = Guid.NewGuid();
            await backend.SubscribeWorkflowAsync(workflowId, cts.Token);
            using var terminalUpdateObserver = new WorkflowTerminalUpdateObserver(backend, workflowId);

            var options = BuildRemoteSubmissionOptions(args, cliSettings) with { WorkflowId = workflowId };
            var request = new SubmitExtractJobRequestDto(
                rootSettings.Extraction.Input,
                rootSettings.Extraction.InputType.ToString(),
                Options: options);

            InteractiveCliCoordinator? interactiveCoordinator = null;
            JobSummaryDto submission;
            if (cliSettings.InteractiveMode)
            {
                interactiveCoordinator = new InteractiveCliCoordinator(backend, cliSettings, cts.Token);
                submission = await interactiveCoordinator.StartAsync(request, cts.Token);
            }
            else
            {
                submission = await backend.SubmitExtractJobAsync(request, cts.Token);
            }

            using var consoleControls = StartConsoleControls(
                backend,
                cliReporter,
                submission.WorkflowId,
                cancelPrompt: "Cancel job ID or current workflow? id/[A]ll/n: ",
                cancelAllMessage: "Cancelling workflow...",
                cancelAll: async ct =>
                {
                    await backend.CancelWorkflowAsync(submission.WorkflowId, ct);
                },
                cts);

            if (interactiveCoordinator != null)
                await interactiveCoordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token);
            else
                await WaitForRemoteWorkflowAsync(backend, submission.WorkflowId, cts.Token);

            await terminalUpdateObserver.WaitForTerminalUpdateAsync(cts.Token);

            if (!rootSettings.DoNotDownload)
                await PrintRemoteCompleteAsync(backend, submission.WorkflowId, cts.Token);

            var exitCode = LogCliSessionExit(await DetermineRemoteExitCodeAsync(backend, submission.WorkflowId, cts.Token), remoteSettings);
            cliReporter?.Stop();
            cliReporter = null;

            if (rootSettings.PrintResults || rootSettings.PrintJobs)
                await PrintRemoteRequestedOutputAsync(backend, submission.WorkflowId, rootSettings, cts.Token);

            return exitCode;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return LogCliSessionExit(CliExitCode.Cancelled, remoteSettings);
        }
        catch (SockseekApiRequestException ex)
        {
            if (cliReporter != null)
                cliReporter.ReportClientError(ex.Message);
            else
                SockseekLog.Error(ex.Message);

            return LogCliSessionExit(CliExitCode.WorkFailed, remoteSettings);
        }
        catch (Exception ex)
        {
            if (cliReporter != null)
                cliReporter.ReportClientError($"Unhandled remote CLI error: {SockseekLog.ExceptionSummary(ex)}");
            else
                SockseekLog.Fatal($"Unhandled remote CLI error: {SockseekLog.ExceptionSummary(ex)}");

            return LogCliSessionExit(CliExitCode.WorkFailed, remoteSettings);
        }
        finally
        {
            await cts.CancelAsync();
            cliReporter?.Stop();
        }
    }

    private static async Task<CliExitCode> RunRemoteMonitorAsync(
        string[] args,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        CliSettings cliSettings,
        RemoteSettings remoteSettings,
        CliOutputController output,
        CancellationTokenSource cts)
    {
        await using var backend = new RemoteCliBackend(remoteSettings.ServerUrl!);
        CliProgressReporter? reporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else
        {
            output.ConfigureLiveRendering(cliSettings, engineSettings.LogLevel);
            reporter = new CliProgressReporter(cliSettings, output);
            reporter.Attach(backend);
        }

        var eventLogger = new EventLogger(backend, includeDiagnosticDetails: false);
        eventLogger.Attach();

        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            await backend.SubscribeAllAsync(cts.Token);
            using var consoleControls = StartConsoleControls(
                backend,
                reporter,
                workflowId: null,
                cancelPrompt: "Cancel job ID or all daemon jobs? id/[A]ll/n: ",
                cancelAllMessage: "Cancelling all daemon jobs...",
                cancelAll: async ct =>
                {
                    await backend.CancelAllJobsAsync(ct);
                },
                cts);

            if (!string.IsNullOrWhiteSpace(rootSettings.Extraction.Input))
            {
                var options = BuildRemoteSubmissionOptions(args, cliSettings) with
                {
                    WorkflowId = Guid.NewGuid(),
                };
                await backend.SubmitExtractJobAsync(
                    new SubmitExtractJobRequestDto(
                        rootSettings.Extraction.Input,
                        rootSettings.Extraction.InputType.ToString(),
                        Options: options),
                    cts.Token);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            return CliExitCode.Success;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            reporter?.Stop();
        }
    }

    private static IDisposable StartConsoleControls(
        ICliBackend backend,
        CliProgressReporter? reporter,
        Guid? workflowId,
        string cancelPrompt,
        string cancelAllMessage,
        Func<CancellationToken, Task> cancelAll,
        CancellationTokenSource cts)
    {
        ConsoleInputManager.Reporter = reporter;

        Func<Task> cancelHandler = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write(cancelPrompt, ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();
            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
            {
                SockseekLog.Info(cancelAllMessage);
                Printing.WriteLine(cancelAllMessage, ConsoleColor.Gray, force: true);
                await cancelAll(cts.Token);
                return;
            }

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob
                && result.JobId is int id)
            {
                if (await backend.CancelJobByDisplayIdAsync(id, workflowId, cts.Token))
                    SockseekLog.Info($"Cancelling job [{id}]...");
                else
                    SockseekLog.Error($"Job ID [{id}] not found.");
                return;
            }

            SockseekLog.Error($"Invalid input '{result.Input}'.");
        };

        Func<Task> nextCandidateHandler = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write(
                    "Try next candidate for job ID or n: ",
                    ConsoleColor.Yellow,
                    force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();
            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob
                && result.JobId is int id)
            {
                if (await backend.TryNextCandidateByDisplayIdAsync(
                    id,
                    workflowId,
                    cts.Token))
                {
                    SockseekLog.Info($"Trying next candidate for job [{id}]...");
                }
                else
                {
                    SockseekLog.Error(
                        $"Job ID [{id}] not found or has no active download.");
                }
                return;
            }

            SockseekLog.Error($"Invalid input '{result.Input}'.");
        };

        Func<Task> infoHandler = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine(force: true);
                Printing.Write(
                    "Info for job ID (blank to cancel): ",
                    ConsoleColor.Yellow,
                    force: true);
            }

            var id = ConsoleInputManager.ReadJobIdInput();
            if (id == null)
                return;

            while (true)
            {
                int printStart = Console.IsOutputRedirected ? -1 : Console.CursorTop;
                var detail = await backend.GetJobDetailByDisplayIdAsync(
                    id.Value,
                    workflowId,
                    cts.Token);
                if (detail == null)
                    SockseekLog.Error($"Job ID [{id}] not found.");
                else
                    JobInfoPrinter.Print(detail);

                lock (Printing.ConsoleLock)
                {
                    Printing.Write(
                        "Info for job ID (r to refresh, blank to exit): ",
                        ConsoleColor.Yellow,
                        force: true);
                }

                var result = ConsoleInputManager.ReadJobIdOrRefreshResult();
                if (result.Action == ConsoleInputManager.CancelPromptAction.Refresh)
                {
                    ClearPrintedJobInfo(printStart);
                }
                else if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob
                         && result.JobId.HasValue)
                {
                    id = result.JobId.Value;
                }
                else
                {
                    return;
                }
            }
        };

        ConsoleInputManager.OnCancelRequested = cancelHandler;
        ConsoleInputManager.OnNextCandidateRequested = nextCandidateHandler;
        ConsoleInputManager.OnInfoRequested = infoHandler;
        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        return new ConsoleControlRegistration(
            reporter,
            cancelHandler,
            nextCandidateHandler,
            infoHandler);
    }

    private static void ClearPrintedJobInfo(int printStart)
    {
        if (printStart < 0)
            return;

        int pos = Console.CursorTop;
        while (pos > printStart && pos > 0)
        {
            Console.SetCursorPosition(0, pos - 1);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, pos - 1);
            pos--;
        }
        Console.SetCursorPosition(0, printStart);
    }

    private sealed class ConsoleControlRegistration(
        CliProgressReporter? reporter,
        Func<Task> cancelHandler,
        Func<Task> nextCandidateHandler,
        Func<Task> infoHandler) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            if (ReferenceEquals(ConsoleInputManager.OnCancelRequested, cancelHandler))
                ConsoleInputManager.OnCancelRequested = null;
            if (ReferenceEquals(ConsoleInputManager.OnNextCandidateRequested, nextCandidateHandler))
                ConsoleInputManager.OnNextCandidateRequested = null;
            if (ReferenceEquals(ConsoleInputManager.OnInfoRequested, infoHandler))
                ConsoleInputManager.OnInfoRequested = null;
            if (ReferenceEquals(ConsoleInputManager.Reporter, reporter))
                ConsoleInputManager.Reporter = null;
        }
    }

    private static async Task WaitForRemoteWorkflowAsync(ICliBackend backend, Guid workflowId, CancellationToken ct)
    {
        if (backend.ClientStore.GetWorkflow(workflowId)?.State
            is ServerWorkflowState.Completed or ServerWorkflowState.Failed)
        {
            return;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateUpdated(DaemonClientUpdate _)
        {
            if (backend.ClientStore.GetWorkflow(workflowId)?.State
                is ServerWorkflowState.Completed or ServerWorkflowState.Failed)
            {
                completed.TrySetResult();
            }
        }

        backend.StateUpdated += OnStateUpdated;
        try
        {
            OnStateUpdated(default!);
            await completed.Task.WaitAsync(ct);
        }
        finally
        {
            backend.StateUpdated -= OnStateUpdated;
        }
    }

    private sealed class WorkflowTerminalUpdateObserver : IDisposable
    {
        private static readonly TimeSpan TerminalUpdateDrainTimeout = TimeSpan.FromSeconds(2);

        private readonly ICliBackend backend;
        private readonly Guid workflowId;
        private readonly TaskCompletionSource terminalUpdateSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkflowTerminalUpdateObserver(ICliBackend backend, Guid workflowId)
        {
            this.backend = backend;
            this.workflowId = workflowId;
            backend.StateUpdated += OnStateUpdated;
        }

        public async Task WaitForTerminalUpdateAsync(CancellationToken ct)
        {
            if (terminalUpdateSeen.Task.IsCompleted)
                return;

            try
            {
                await terminalUpdateSeen.Task.WaitAsync(TerminalUpdateDrainTimeout, ct);
            }
            catch (TimeoutException)
            {
                // The HTTP snapshot is authoritative for completion. This wait is only to give
                // the remote event stream a chance to deliver terminal activity before the CLI exits.
            }
        }

        public void Dispose()
            => backend.StateUpdated -= OnStateUpdated;

        private void OnStateUpdated(DaemonClientUpdate update)
        {
            if (update.Status != DaemonClientApplyStatus.Applied)
                return;

            if (update.ChangedWorkflows.Any(workflow =>
                    workflow.WorkflowId == workflowId
                    && workflow.State is ServerWorkflowState.Completed or ServerWorkflowState.Failed))
                terminalUpdateSeen.TrySetResult();
        }
    }

    internal static CliExitCode DetermineLocalExitCode(JobList queue)
    {
        var allJobs = queue.AllJobs().ToArray();
        if (allJobs.Any(job => job.TerminalOutcome == JobTerminalOutcome.Cancelled))
            return CliExitCode.Cancelled;

        var (_, fails, _) = Printing.CountUserFacingCompletionsDetailed(queue);
        if (fails > 0)
            return CliExitCode.WorkFailed;

        if (allJobs.Any(IsFailedPrintResultsJob))
            return CliExitCode.WorkFailed;

        return allJobs.Any(IsInfrastructureFailure)
            ? CliExitCode.WorkFailed
            : CliExitCode.Success;
    }

    private static bool IsFailedPrintResultsJob(Job job)
        => job.Config?.PrintResults == true && job.IsUnsuccessfulTerminal;

    private static bool IsInfrastructureFailure(Job job)
        => job.IsUnsuccessfulTerminal
            && job is ExtractJob or JobList;

    private static async Task<CliExitCode> DetermineRemoteExitCodeAsync(
        ICliBackend backend,
        Guid workflowId,
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return CliExitCode.WorkFailed;

        var summaries = (await backend.GetJobsAsync(
                new JobQuery(null, null, null, workflowId, IncludeAll: true),
                ct))
            .OrderBy(job => job.DisplayId)
            .ToArray();

        if (summaries.Any(job => job.TerminalOutcome == ServerJobTerminalOutcome.Cancelled))
            return CliExitCode.Cancelled;
        var jobsById = summaries.ToDictionary(job => job.JobId);
        var supersededSourceJobIds = summaries
            .Select(job => job.SourceJobId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        int successes = 0;
        int fails = 0;
        int skipped = 0;
        foreach (var summary in summaries)
            CountRemoteUserFacingCompletion(summary, jobsById, supersededSourceJobIds, ref successes, ref fails, ref skipped);

        if (fails > 0 || workflow.Summary.State == ServerWorkflowState.Failed)
            return CliExitCode.WorkFailed;

        return CliExitCode.Success;
    }

    private static void PrintCompactTrackBatchResolved(
        ActivityEventDto activity,
        TrackBatchResolvedActivityDto batch)
    {
        if (batch.IsNormal && batch.PendingCount == 1 && batch.ExistingCount + batch.NotFoundCount == 0)
            return;

        string skipped = string.Join(
            ", ",
            new[]
            {
                batch.ExistingCount > 0 ? $"{batch.ExistingCount} already exist" : null,
                batch.NotFoundCount > 0 ? $"{batch.NotFoundCount} not found" : null,
            }.Where(value => value != null));
        string message = batch.PendingCount > 0
            ? $"Downloading {batch.PendingCount} tracks{(skipped.Length > 0 ? $" ({skipped})" : "")}."
            : $"No tracks pending{(skipped.Length > 0 ? $" ({skipped})" : "")}.";

        if (batch.IsNormal || activity.JobId == null)
        {
            SockseekLog.Info(message);
            return;
        }

        var line = new TerminalLogLine(
            TerminalLogKind.Status,
            activity.JobId.Value.ToString(),
            batch.DisplayId,
            "Job List",
            message);
        SockseekLog.Write(new SockseekLog.StructuredLogEntry(
            LogLevel.Information,
            SockseekLog.Categories.Jobs,
            CliLogStyle.FormatTerminalLogText(line),
            Context: new CliOutputEvent.JobLog(line)));
    }

    private static bool ShouldAttachHumanProgressReporter(PrintOption printOption)
        => !IsMachineReadablePrint(printOption);

    private static bool ShouldPrintHumanBatchPreview(PrintOption printOption)
        => !IsMachineReadablePrint(printOption);

    private static bool IsMachineReadablePrint(PrintOption printOption)
        => (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;

    internal static async Task PrintRemoteCompleteAsync(
        ICliBackend backend,
        Guid workflowId,
        CancellationToken ct,
        TextWriter? output = null)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return;

        var summaries = workflow.Jobs
            .OrderBy(job => job.DisplayId)
            .ToArray();
        var jobsById = summaries.ToDictionary(job => job.JobId);
        var supersededSourceJobIds = summaries
            .Select(job => job.SourceJobId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        int successes = 0;
        int fails = 0;
        int skipped = 0;
        foreach (var summary in summaries)
            CountRemoteUserFacingCompletion(summary, jobsById, supersededSourceJobIds, ref successes, ref fails, ref skipped);

        if (output is null)
        {
            Printing.PrintComplete(successes, fails, skipped);
            return;
        }

        string? message = Printing.FormatComplete(successes, fails, skipped);
        if (message is not null)
        {
            output.WriteLine();
            output.WriteLine(message);
        }
    }

    private static void CountRemoteUserFacingCompletion(
        JobSummaryDto summary,
        IReadOnlyDictionary<Guid, JobSummaryDto> jobsById,
        IReadOnlySet<Guid> supersededSourceJobIds,
        ref int successes,
        ref int fails,
        ref int skipped)
    {
        if (supersededSourceJobIds.Contains(summary.JobId))
            return;

        if (IsRemoteInfrastructureJobKind(summary.Kind))
            return;

        if (summary.Kind == ServerJobKind.Song
            && summary.ParentJobId is Guid parentId
            && jobsById.TryGetValue(parentId, out var parent)
            && parent.Kind == ServerJobKind.Album)
        {
            return;
        }

        CountSummary(summary, ref successes, ref fails, ref skipped);
    }

    // TODO [ARCHITECTURE]: Move local and remote completion accounting onto one
    // shared domain-level summary model. Manual skips, already-exists skips,
    // partial success, cancellation, and infrastructure jobs should not be
    // recounted independently by each CLI/API consumer.
    private static bool IsRemoteInfrastructureJobKind(ServerJobKind kind)
        => kind is ServerJobKind.Extract or ServerJobKind.JobList or ServerJobKind.RetrieveFolder
            or ServerJobKind.Aggregate or ServerJobKind.AlbumAggregate;

    private static void CountSummary(JobSummaryDto summary, ref int successes, ref int fails, ref int skipped)
    {
        if (IsSuccessfulRemoteOutcome(summary.TerminalOutcome, summary.SkipReason))
            successes++;
        else if (IsManualSkipRemoteOutcome(summary.TerminalOutcome, summary.SkipReason))
            skipped++;
        else if (IsFailedRemoteOutcome(summary.TerminalOutcome, summary.SkipReason))
            fails++;
    }

    private static bool IsSuccessfulRemoteOutcome(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason)
        => outcome == ServerJobTerminalOutcome.Succeeded
            || (outcome == ServerJobTerminalOutcome.Skipped && skipReason == ServerJobSkipReason.AlreadyExists);

    private static bool IsManualSkipRemoteOutcome(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason)
        => outcome == ServerJobTerminalOutcome.Skipped && skipReason == ServerJobSkipReason.Manual;

    private static bool IsFailedRemoteOutcome(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason)
        => (outcome is ServerJobTerminalOutcome.Failed
                or ServerJobTerminalOutcome.Cancelled
                or ServerJobTerminalOutcome.PartialSuccess)
            || (outcome == ServerJobTerminalOutcome.Skipped
                && skipReason is not ServerJobSkipReason.AlreadyExists and not ServerJobSkipReason.Manual);

    private static IEnumerable<SongJobPayloadDto> ResolvedAlbumSongs(AlbumJobPayloadDto album)
        => album.Tracks?.Where(song => Utils.IsMusicFile(song.ResolvedFilename ?? "")) ?? [];

    internal static async Task PrintRemoteRequestedOutputAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct,
        TextWriter? output = null)
    {
        var queue = await BuildRemotePrintQueueAsync(backend, workflowId, settings, ct);
        using var outputScope = output is null ? null : Printing.RedirectOutput(output);
        PrintOutputRenderer.PrintRequestedOutput(queue);
    }

    internal static async Task PrintRemoteResultsAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
        => await PrintRemoteRequestedOutputAsync(backend, workflowId, settings, ct);

    internal static async Task PrintRemoteJobOutputAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
        => await PrintRemoteRequestedOutputAsync(backend, workflowId, settings, ct);

    private static async Task<JobList> BuildRemotePrintQueueAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
    {
        var queue = new JobList("remote workflow")
        {
            Config = SettingsCloner.Clone(settings),
        };

        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return queue;

        var details = new Dictionary<Guid, JobDetailDto>();
        foreach (var summary in workflow.Jobs)
            await LoadRemoteJobTreeAsync(backend, summary.JobId, details, ct);

        var roots = details.Values
            .Where(detail => workflow.Jobs.Any(root => root.JobId == detail.Summary.JobId))
            .OrderBy(detail => detail.Summary.DisplayId)
            .ToList();

        var visited = new HashSet<Guid>();
        foreach (var root in roots)
        {
            var job = await ToRemotePrintJobAsync(backend, root, details, settings, visited, ct);
            if (job != null)
                queue.Add(job);
        }

        return queue;
    }

    private static async Task<Job?> ToRemotePrintJobAsync(
        ICliBackend backend,
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        DownloadSettings settings,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        if (!visited.Add(detail.Summary.JobId) || detail.Payload == null)
            return null;

        var effectiveSettings = RemotePrintSettings(settings, detail.Summary);

        Job? job = detail.Payload switch
        {
            ExtractJobPayloadDto extract
                => await ToRemoteExtractPrintJobAsync(backend, detail, extract, details, effectiveSettings, visited, ct),

            JobListPayloadDto
                => await ToRemoteJobListPrintJobAsync(backend, detail, details, effectiveSettings, visited, ct),

            SearchJobPayloadDto search
                => effectiveSettings.PrintResults
                    ? await ToSearchResultsJobAsync(backend, detail.Summary.JobId, search, ct)
                    : ToSearchJob(search, detail.Summary),

            SongJobPayloadDto song
                => effectiveSettings.PrintResults
                    ? await ToSongResultsJobAsync(backend, detail.Summary.JobId, song, ct)
                    : ToSongJob(song, detail.Summary),

            AlbumJobPayloadDto album
                => effectiveSettings.PrintResults
                    ? await ToAlbumResultsJobAsync(backend, detail.Summary.JobId, album, detail.Summary, ct)
                    : ToAlbumJob(album, detail.Summary),

            AggregateJobPayloadDto aggregate
                => effectiveSettings.PrintResults
                    ? await ToAggregateResultsJobAsync(backend, aggregate, detail.Children, details, ct)
                    : ToAggregateJob(aggregate),

            AlbumAggregateJobPayloadDto albumAggregate
                => effectiveSettings.PrintResults
                    ? await ToAlbumAggregateResultsJobAsync(backend, albumAggregate, detail.Children, details, ct)
                    : ToAlbumAggregateJob(albumAggregate, detail.Summary),

            RetrieveFolderJobPayloadDto folder
                => ToRetrieveFolderJob(folder, detail.Summary),

            _ => null,
        };

        if (job != null)
            ApplyRemotePrintConfig(job, effectiveSettings);

        return job;
    }

    private static async Task<ExtractJob> ToRemoteExtractPrintJobAsync(
        ICliBackend backend,
        JobDetailDto detail,
        ExtractJobPayloadDto extract,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        DownloadSettings settings,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        var job = new ExtractJob(extract.Input, ParseRemoteInputType(extract.InputType))
        {
            AutoProcessResult = extract.AutoProcessResult,
        };
        ApplyJobOutcome(job, detail.Summary.LifecycleState, detail.Summary.ActivityPhase, detail.Summary.TerminalOutcome, detail.Summary.SkipReason, detail.Summary.FailureReason, detail.Summary.FailureMessage, detail.Summary.CancellationSource);

        if (extract.ResultJobId is Guid resultJobId
            && details.TryGetValue(resultJobId, out var resultDetail))
        {
            job.Result = await ToRemotePrintJobAsync(backend, resultDetail, details, settings, visited, ct);
        }

        return job;
    }

    private static async Task<JobList> ToRemoteJobListPrintJobAsync(
        ICliBackend backend,
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        DownloadSettings settings,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        var jobList = new JobList(detail.Summary.ItemName ?? detail.Summary.QueryText);
        ApplyJobOutcome(jobList, detail.Summary.LifecycleState, detail.Summary.ActivityPhase, detail.Summary.TerminalOutcome, detail.Summary.SkipReason, detail.Summary.FailureReason, detail.Summary.FailureMessage, detail.Summary.CancellationSource);

        foreach (var child in ChildrenOf(detail, details))
        {
            var childJob = await ToRemotePrintJobAsync(backend, child, details, settings, visited, ct);
            if (childJob != null)
                jobList.Add(childJob);
        }

        return jobList;
    }

    private static async Task<Job?> ToSearchResultsJobAsync(
        ICliBackend backend,
        Guid searchJobId,
        SearchJobPayloadDto search,
        CancellationToken ct)
    {
        if (search.DefaultFolderProjection != null)
        {
            var folders = await backend.GetFolderResultsAsync(
                searchJobId,
                search.DefaultFolderProjection with { IncludeFiles = true },
                ct);
            return folders == null
                ? null
                : new AlbumJob(ToAlbumQuery(search.DefaultFolderProjection.AlbumQuery))
                {
                    Results = folders.Items.Select(ToAlbumFolder).ToList(),
                };
        }

        var fileProjection = search.DefaultFileProjection
            ?? new FileSearchProjectionRequestDto(new SongQueryDto(null, search.QueryText, null, null, null, false));
        var files = await backend.GetFileResultsAsync(searchJobId, fileProjection, ct);
        return files == null
            ? null
            : new SongJob(ToSongQuery(fileProjection.SongQuery ?? new SongQueryDto(null, search.QueryText, null, null, null, false)))
            {
                Candidates = files.Items.Select(ToFileCandidate).ToList(),
            };
    }

    private static async Task<Job?> ToSongResultsJobAsync(
        ICliBackend backend,
        Guid songJobId,
        SongJobPayloadDto song,
        CancellationToken ct)
    {
        var files = await backend.GetFileResultsAsync(songJobId, ct);
        var job = ToSongJob(song);
        job.Candidates = files?.Items.Select(ToFileCandidate).ToList();
        return job;
    }

    private static async Task<Job?> ToAlbumResultsJobAsync(
        ICliBackend backend,
        Guid albumJobId,
        AlbumJobPayloadDto album,
        JobSummaryDto? summary,
        CancellationToken ct)
    {
        var folders = await backend.GetFolderResultsAsync(albumJobId, includeFiles: true, ct);
        var job = ToAlbumJob(album, summary);
        job.Results = folders?.Items.Select(ToAlbumFolder).ToList() ?? [];
        return job;
    }

    private static async Task<Job?> ToAggregateResultsJobAsync(
        ICliBackend backend,
        AggregateJobPayloadDto aggregate,
        IReadOnlyList<JobSummaryDto> children,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        CancellationToken ct)
    {
        var job = new AggregateJob(ToSongQuery(aggregate.Query));
        foreach (var summary in children.Where(child => child.Kind == ServerJobKind.Song).OrderBy(child => child.DisplayId))
        {
            if (!details.TryGetValue(summary.JobId, out var detail))
                detail = await backend.GetJobDetailAsync(summary.JobId, ct);

            if (detail?.Payload is not SongJobPayloadDto payload)
                continue;

            var song = ToSongJob(payload);
            if (payload.CandidateCount.GetValueOrDefault() > 0)
            {
                var files = await backend.GetFileResultsAsync(summary.JobId, ct);
                song.Candidates = files?.Items.Select(ToFileCandidate).ToList();
            }

            job.Songs.Add(song);
        }

        return job;
    }

    private static async Task<Job?> ToAlbumAggregateResultsJobAsync(
        ICliBackend backend,
        AlbumAggregateJobPayloadDto aggregate,
        IReadOnlyList<JobSummaryDto> children,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        CancellationToken ct)
    {
        var job = new AlbumAggregateJob(ToAlbumQuery(aggregate.Query));
        foreach (var summary in children.Where(child => child.Kind == ServerJobKind.Album).OrderBy(child => child.DisplayId))
        {
            if (!details.TryGetValue(summary.JobId, out var detail))
                detail = await backend.GetJobDetailAsync(summary.JobId, ct);

            if (detail?.Payload is not AlbumJobPayloadDto payload)
                continue;

            if (await ToAlbumResultsJobAsync(backend, summary.JobId, payload, summary, ct) is AlbumJob album)
                job.Albums.Add(album);
        }

        return job;
    }

    private static SearchJob ToSearchJob(SearchJobPayloadDto search, JobSummaryDto summary)
    {
        SearchJob job;
        if (search.DefaultFolderProjection != null)
        {
            job = new SearchJob(ToAlbumQuery(search.DefaultFolderProjection.AlbumQuery));
        }
        else if (search.DefaultFileProjection?.SongQuery != null)
        {
            job = new SearchJob(
                ToSongQuery(search.DefaultFileProjection.SongQuery),
                search.DefaultFileProjection.IncludeFullResults);
        }
        else
        {
            job = new SearchJob(search.QueryText);
        }

        ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        return job;
    }

    private static RetrieveFolderJob ToRetrieveFolderJob(RetrieveFolderJobPayloadDto folder, JobSummaryDto summary)
    {
        var directory = new PeerDirectoryIdentity(folder.Username, folder.FolderPath);
        var job = new RetrieveFolderJob(directory)
        {
            NewFilesFoundCount = folder.NewFilesFoundCount,
            RetrievalOutcome = ToCoreFolderRetrievalOutcome(folder.RetrievalOutcome),
            Result = folder.Folder == null ? null : ToPeerDirectorySnapshot(folder.Folder),
        };

        ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        return job;
    }

    private static PeerDirectorySnapshot ToPeerDirectorySnapshot(AlbumFolderDto folder)
        => new(
            new PeerDirectoryIdentity(folder.Username, folder.FolderPath),
            folder.Files?.Select(file => new PeerFileTarget(
                new PeerFileIdentity(file.Username, file.Filename),
                file.File.Size < 0 ? null : file.File.Size,
                file.File.Extension,
                file.File.BitRate,
                file.File.BitDepth,
                file.File.SampleRate,
                file.File.Length,
                file.File.Attributes?.Select(attribute => new Sockseek.Core.Snapshots.FileAttributeSnapshot(
                    attribute.Type,
                    attribute.Value)).ToArray())).ToArray() ?? [],
            folder.IsFullyRetrieved);

    private static void ApplyRemotePrintConfig(Job job, DownloadSettings settings)
        => job.Config = SettingsCloner.Clone(settings);

    private static DownloadSettings RemotePrintSettings(DownloadSettings inherited, JobSummaryDto summary)
    {
        var settings = SettingsCloner.Clone(inherited);
        if (summary.PrintOption != PrintOption.None)
            settings.PrintOption = summary.PrintOption;
        return settings;
    }

    private static InputType? ParseRemoteInputType(string? inputType)
        => Enum.TryParse<InputType>(inputType, ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private static FolderRetrievalOutcome ToCoreFolderRetrievalOutcome(ServerFolderRetrievalOutcome outcome)
        => Enum.TryParse<FolderRetrievalOutcome>(outcome.ToString(), out var parsed)
            ? parsed
            : FolderRetrievalOutcome.None;

    private static async Task LoadRemoteJobTreeAsync(
        ICliBackend backend,
        Guid jobId,
        Dictionary<Guid, JobDetailDto> details,
        CancellationToken ct)
    {
        if (details.ContainsKey(jobId))
            return;

        var detail = await backend.GetJobDetailAsync(jobId, ct);
        if (detail == null)
            return;

        details[jobId] = detail;

        if (detail.Payload is ExtractJobPayloadDto { ResultJobId: Guid resultJobId })
            await LoadRemoteJobTreeAsync(backend, resultJobId, details, ct);

        foreach (var child in detail.Children)
        {
            if (detail.Summary.Kind == ServerJobKind.Album
                && child.Kind == ServerJobKind.Song)
                continue;

            await LoadRemoteJobTreeAsync(backend, child.JobId, details, ct);
        }
    }

    private static List<JobDetailDto> ChildrenOf(
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details)
        => details.Values
            .Where(candidate => candidate.Summary.ParentJobId == detail.Summary.JobId)
            .OrderBy(candidate => candidate.Summary.DisplayId)
            .ToList();

    internal static SubmissionOptionsDto BuildRemoteSubmissionOptions(
        string[] args,
        CliSettings cliSettings)
        => new(
            ProfileNames: SplitProfileNames(ConfigManager.ExtractProfileName(args)),
            ProfileContext: new Dictionary<string, bool>
            {
                ["interactive"] = cliSettings.InteractiveMode,
                ["progress-json"] = cliSettings.ProgressJson,
                ["no-progress"] = cliSettings.NoProgress,
            },
            DownloadSettings: ConfigManager.CreateCliDownloadSettingsPatch(args));

    private static string[]? SplitProfileNames(string? names)
        => string.IsNullOrWhiteSpace(names)
            ? null
            : names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void LogCliSessionStart(RemoteSettings remoteSettings)
    {
        if (remoteSettings.IsEnabled)
        {
            SockseekLog.Cli.Info($"Starting CLI session in remote mode: {remoteSettings.ServerUrl}");
            return;
        }

        SockseekLog.Cli.Info("Starting CLI session in local mode");
    }

    private static CliExitCode LogCliSessionExit(CliExitCode exitCode, RemoteSettings? remoteSettings = null)
    {
        if (remoteSettings?.IsEnabled == true)
        {
            SockseekLog.Cli.Debug($"Exiting CLI session in remote mode with code {(int)exitCode} ({exitCode})");
            return exitCode;
        }

        SockseekLog.Cli.Debug($"Exiting CLI session in local mode with code {(int)exitCode} ({exitCode})");
        return exitCode;
    }

    private static async Task RunDaemonAsync(
        string[] args,
        ConfigFile configFile,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        DaemonSettings daemonSettings)
    {
        var url = BuildDaemonListenUrl(daemonSettings);
        EnsureDaemonEndpointAvailable(daemonSettings);
        var options = new ServerOptions
        {
            Engine = SettingsCloner.Clone(engineSettings),
            DefaultDownload = SettingsCloner.Clone(rootSettings),
            LaunchDownloadSettings = ConfigManager.CreateCliDownloadSettingsPatch(args),
            Profiles = ConfigManager.CreateProfileCatalog(configFile),
            ConfigDir = configFile.ConfigDir,
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = daemonSettings.DataDirectory,
                RetentionEnabled = daemonSettings.RetentionEnabled,
                CompletedJobHistoryAge = daemonSettings.CompletedJobRetention,
                UnsuccessfulJobHistoryAge = daemonSettings.UnsuccessfulJobRetention,
                SearchResultAge = daemonSettings.SearchResultRetention,
                TransferHistoryAge = daemonSettings.TransferRetention,
                PrivateMessageHistoryAge = daemonSettings.PrivateMessageRetention,
                RoomMessageHistoryAge = daemonSettings.RoomMessageRetention,
                MaximumRetainedJobs = daemonSettings.MaximumRetainedJobs,
            },
        };

        var app = ServerHost.Build(args, options, url);
        CoreLoggerBridge.Configure(engineSettings.LogLevel);
        SockseekLog.Info($"Starting Sockseek daemon on {url}", categoryName: SockseekLog.Categories.Daemon);
        if (IsDaemonListenAddressNetworkExposed(daemonSettings))
        {
            SockseekLog.Warn(
                "Sockseek daemon is listening on all network interfaces. The API is unauthenticated; expose it only on trusted networks or behind your own access control.",
                categoryName: SockseekLog.Categories.Daemon);
        }
        SockseekLog.Info("Press Ctrl+C to stop.", categoryName: SockseekLog.Categories.Daemon);
        try
        {
            await app.RunAsync();
        }
        finally
        {
            SockseekLog.Info($"Exiting Sockseek daemon on {url}", categoryName: SockseekLog.Categories.Daemon);
        }
    }

    internal static void EnsureDaemonEndpointAvailable(DaemonSettings daemonSettings)
    {
        if (!System.Net.IPAddress.TryParse(daemonSettings.ListenIp, out var ipAddress))
            throw new ArgumentException($"Invalid daemon listen IP '{daemonSettings.ListenIp}'. Use a valid IP address such as 127.0.0.1, 0.0.0.0, ::1, or ::.");

        try
        {
            using var listener = new System.Net.Sockets.TcpListener(ipAddress, daemonSettings.ListenPort);
            listener.Start();
            listener.Stop();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            throw new DaemonEndpointUnavailableException(
                $"Cannot start Sockseek daemon on {BuildDaemonListenUrl(daemonSettings)}: {SockseekLog.ExceptionSummary(ex)}",
                ex);
        }
    }

    internal static string BuildDaemonListenUrl(DaemonSettings daemonSettings)
    {
        if (!System.Net.IPAddress.TryParse(daemonSettings.ListenIp, out var ipAddress))
            throw new ArgumentException($"Invalid daemon listen IP '{daemonSettings.ListenIp}'. Use a valid IP address such as 127.0.0.1, 0.0.0.0, ::1, or ::.");

        var host = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ipAddress}]"
            : ipAddress.ToString();

        return $"http://{host}:{daemonSettings.ListenPort}";
    }

    internal static bool IsDaemonListenAddressNetworkExposed(DaemonSettings daemonSettings)
    {
        if (!System.Net.IPAddress.TryParse(daemonSettings.ListenIp, out var ipAddress))
            throw new ArgumentException($"Invalid daemon listen IP '{daemonSettings.ListenIp}'. Use a valid IP address such as 127.0.0.1, 0.0.0.0, ::1, or ::.");

        return ipAddress.Equals(System.Net.IPAddress.Any)
            || ipAddress.Equals(System.Net.IPAddress.IPv6Any);
    }

    internal sealed class DaemonEndpointUnavailableException : Exception
    {
        public DaemonEndpointUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private static SongJob ToSongJob(SongJobPayloadDto song)
        => ToSongJob(song, null);

    private static SongJob ToSongJob(SongJobPayloadDto song, JobSummaryDto? summary)
    {
        var job = new SongJob(new SongQuery
        {
            Artist = song.Query.Artist ?? "",
            Title = song.Query.Title ?? "",
            Album = song.Query.Album ?? "",
            URI = song.Query.Uri ?? "",
            Length = song.Query.Length ?? -1,
            ArtistMaybeWrong = song.Query.ArtistMaybeWrong,
        })
        {
            DownloadPath = song.File.DownloadPath,
            BytesTransferred = song.File.BytesTransferred,
            FileSize = song.File.FileSize,
            Candidates = song.Candidates?.Select(ToFileCandidate).ToList(),
            DownloadSource = ToSongDownloadSource(song.DownloadSource),
        };

        ApplyJobOutcome(job, song.LifecycleState, song.ActivityPhase, song.TerminalOutcome, song.SkipReason, song.FailureReason, song.FailureMessage, song.CancellationSource);

        if (summary != null)
        {
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        }

        if (song.ExactTarget != null)
        {
            job.ExactTarget = ToPeerFileTarget(song.ExactTarget);
        }
        else if (!string.IsNullOrWhiteSpace(song.ResolvedUsername)
            && !string.IsNullOrWhiteSpace(song.ResolvedFilename))
        {
            job.ResolvedTarget = ToFileCandidate(new FileCandidateDto(
                new FileCandidateRefDto(song.ResolvedUsername, song.ResolvedFilename),
                song.ResolvedUsername,
                song.ResolvedFilename,
                new PeerInfoDto(song.ResolvedUsername, song.ResolvedHasFreeUploadSlot, song.ResolvedUploadSpeed),
                new FileMetadataDto(
                    Utils.GetFileNameSlsk(song.ResolvedFilename),
                    song.ResolvedSize ?? 0,
                    song.ResolvedExtension,
                    null,
                    null,
                    null,
                    null,
                    song.ResolvedAttributes)));
        }

        return job;
    }

    private static PeerFileTarget ToPeerFileTarget(PeerFileTargetDto target)
        => new(
            new PeerFileIdentity(target.Username, target.Filename),
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.Select(attribute =>
                new FileAttributeSnapshot(attribute.Type, attribute.Value, 0)).ToList());

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album)
        => ToAlbumJob(album, null);

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album, JobSummaryDto? summary)
    {
        var job = new AlbumJob(ToAlbumQuery(album.Query))
        {
            Results = album.Results?.Select(ToAlbumFolder).ToList() ?? [],
            DownloadPath = album.Directory.DownloadPath,
        };

        if (summary != null)
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);

        return job;
    }

    private static AggregateJob ToAggregateJob(AggregateJobPayloadDto aggregate)
        => new(ToSongQuery(aggregate.Query))
        {
            Songs = aggregate.Songs?.Select(ToSongJob).ToList() ?? [],
        };

    private static AlbumAggregateJob ToAlbumAggregateJob(AlbumAggregateJobPayloadDto albumAggregate, JobSummaryDto? summary = null)
    {
        var job = new AlbumAggregateJob(ToAlbumQuery(albumAggregate.Query));
        if (summary != null)
            ApplyJobOutcome(job, summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason, summary.FailureReason, summary.FailureMessage, summary.CancellationSource);
        return job;
    }

    private static void ApplyJobOutcome(
        Job job,
        ServerJobLifecycleState? lifecycleState,
        ServerJobActivityPhase? activityPhase,
        ServerJobTerminalOutcome? terminalOutcome,
        ServerJobSkipReason? skipReason,
        ServerJobFailureReason? failureReason,
        string? failureMessage,
        ServerJobCancellationSource cancellationSource)
    {
        if (lifecycleState == ServerJobLifecycleState.AwaitingSelection)
        {
            job.SetAwaitingSelection();
            return;
        }

        if (lifecycleState is ServerJobLifecycleState.Running or ServerJobLifecycleState.Pending)
        {
            ApplyJobActivity(job, lifecycleState.Value, activityPhase ?? ServerJobActivityPhase.None);
            return;
        }

        if (lifecycleState != ServerJobLifecycleState.Terminal || terminalOutcome is null or ServerJobTerminalOutcome.None)
            return;

        TryToCoreFailureReason(failureReason, out var parsedFailureReason);

        switch (terminalOutcome.Value)
        {
            case ServerJobTerminalOutcome.Succeeded:
                job.SetDone();
                break;
            case ServerJobTerminalOutcome.Skipped:
                job.SetSkipped(ToCoreSkipReason(skipReason), parsedFailureReason);
                break;
            case ServerJobTerminalOutcome.PartialSuccess:
                job.SetPartialSuccess(failureMessage);
                break;
            case ServerJobTerminalOutcome.Cancelled:
                job.SetCancelled(ToCoreCancellationSource(cancellationSource), failureMessage);
                break;
            case ServerJobTerminalOutcome.Failed:
                job.Fail(parsedFailureReason, failureMessage);
                break;
        }
    }

    private static void ApplyJobActivity(Job job, ServerJobLifecycleState lifecycleState, ServerJobActivityPhase activityPhase)
    {
        if (lifecycleState == ServerJobLifecycleState.Pending)
        {
            job.ResetToPending();
            return;
        }

        var corePhase = activityPhase switch
        {
            ServerJobActivityPhase.Extracting => JobActivityPhase.Extracting,
            ServerJobActivityPhase.Downloading => JobActivityPhase.Downloading,
            ServerJobActivityPhase.RetrievingFolder => JobActivityPhase.RetrievingFolder,
            ServerJobActivityPhase.RunningChildren => JobActivityPhase.RunningChildren,
            ServerJobActivityPhase.Searching => JobActivityPhase.Searching,
            ServerJobActivityPhase.WaitingForSearchConcurrency => JobActivityPhase.WaitingForSearchConcurrency,
            ServerJobActivityPhase.SearchRateLimited => JobActivityPhase.SearchRateLimited,
            ServerJobActivityPhase.ProcessingSearchResults => JobActivityPhase.ProcessingSearchResults,
            ServerJobActivityPhase.Organizing => JobActivityPhase.Organizing,
            ServerJobActivityPhase.RunningOnComplete => JobActivityPhase.RunningOnComplete,
            ServerJobActivityPhase.RunningFallback => JobActivityPhase.RunningFallback,
            _ => JobActivityPhase.None,
        };

        if (corePhase == JobActivityPhase.None)
            job.UpdateActivity(JobActivityPhase.RunningChildren);
        else
            job.UpdateActivity(corePhase);
    }

    private static bool TryToCoreFailureReason(ServerJobFailureReason? reason, out JobFailureReason coreReason)
    {
        if (reason == null)
        {
            coreReason = default;
            return false;
        }

        return Enum.TryParse(reason.Value.ToString(), out coreReason);
    }

    private static SongDownloadSource ToSongDownloadSource(ServerSongDownloadSource source)
        => Enum.TryParse<SongDownloadSource>(source.ToString(), out var coreSource)
            ? coreSource
            : SongDownloadSource.None;

    private static JobSkipReason ToCoreSkipReason(ServerJobSkipReason? reason)
        => reason == null
            ? JobSkipReason.None
            : Enum.TryParse(reason.Value.ToString(), out JobSkipReason coreReason)
                ? coreReason
                : JobSkipReason.None;

    private static JobCancellationSource ToCoreCancellationSource(ServerJobCancellationSource source)
        => source == ServerJobCancellationSource.None
            ? JobCancellationSource.InternalEngine
            : Enum.TryParse(source.ToString(), out JobCancellationSource coreSource)
                ? coreSource
                : JobCancellationSource.InternalEngine;

    private static AlbumFolder ToAlbumFolder(AlbumFolderDto folder)
        => new(
            folder.Username,
            folder.FolderPath,
            folder.Files?.Select(ToAlbumFile).ToList() ?? [])
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };

    private static AlbumFile ToAlbumFile(FileCandidateDto file)
    {
        var candidate = ToFileCandidate(file);
        return AlbumFile.WithLazyQuery(
            () => Searcher.InferSongQuery(candidate.Filename, new SongQuery()),
            candidate);
    }

    private static SongQuery ToSongQuery(SongQueryDto query)
        => new()
        {
            Artist = query.Artist ?? "",
            Title = query.Title ?? "",
            Album = query.Album ?? "",
            URI = query.Uri ?? "",
            Length = query.Length ?? -1,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    private static AlbumQuery ToAlbumQuery(AlbumQueryDto query)
        => new()
        {
            Artist = query.Artist ?? "",
            Album = query.Album ?? "",
            SearchHint = query.SearchHint ?? "",
            URI = query.Uri ?? "",
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    private static FileCandidate ToFileCandidate(FileCandidateDto candidate)
        => new(
            new PeerFileTarget(
                new PeerFileIdentity(candidate.Username, candidate.Filename),
                candidate.File.Size < 0 ? null : candidate.File.Size,
                candidate.File.Extension ?? Path.GetExtension(candidate.Filename),
                candidate.File.BitRate,
                candidate.File.BitDepth,
                candidate.File.SampleRate,
                candidate.File.Length,
                candidate.File.Attributes?.Select(x => new FileAttributeSnapshot(x.Type, x.Value)).ToList()),
            new SearchPeerSnapshot(
                candidate.Username,
                responseFileCount: 0,
                candidate.Peer.UploadSpeed,
                candidate.Peer.HasFreeUploadSlot));
}
