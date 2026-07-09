using Microsoft.Extensions.Logging;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

internal sealed class CliOutputController : IDisposable
{
    private readonly bool _installedConsoleSink;
    private readonly bool _forceHumanLogsToError;
    private readonly object _liveGate = new();
    private TerminalLiveRenderer? _live;
    private bool _canUseLiveRendering;
    private LogLevel _liveLogMinimumLevel = LogLevel.Information;
    private bool _liveLogSinkAttached;
    private bool _disposed;

    private CliOutputController(bool installedConsoleSink, bool forceHumanLogsToError)
    {
        _installedConsoleSink = installedConsoleSink;
        _forceHumanLogsToError = forceHumanLogsToError;
    }

    public bool UsesLiveRendering => _live != null;

    public bool IsPaused
    {
        get => _live?.IsPaused ?? false;
        set
        {
            if (_live != null)
                _live.IsPaused = value;
            else
                Printing.SetBuffering(value);
        }
    }

    public static CliOutputController Install(IReadOnlyList<string> args)
    {
        var controller = new CliOutputController(
            installedConsoleSink: true,
            forceHumanLogsToError: ArgsRequestProgressJson(args));
        controller.AttachConsoleSink();
        return controller;
    }

    public static CliOutputController CreateDetached(
        CliSettings? cliSettings = null,
        LogLevel liveLogMinimumLevel = LogLevel.Information)
    {
        var controller = new CliOutputController(
            installedConsoleSink: false,
            forceHumanLogsToError: false);

        if (cliSettings != null)
            controller.ConfigureLiveRendering(cliSettings, liveLogMinimumLevel);

        return controller;
    }

    public static bool ArgsRequestProgressJson(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--progress-json", StringComparison.OrdinalIgnoreCase))
                return i + 1 >= args.Count || !args[i + 1].Equals("false", StringComparison.OrdinalIgnoreCase);

            if (arg.StartsWith("--progress-json=", StringComparison.OrdinalIgnoreCase))
                return !arg["--progress-json=".Length..].Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool WouldUseLiveRendering(CliSettings cliSettings)
        => !cliSettings.NoProgress
            && !cliSettings.ProgressJson
            && !Console.IsOutputRedirected;

    public void ConfigureLiveRendering(CliSettings cliSettings, LogLevel minimumLevel)
    {
        lock (_liveGate)
        {
            _canUseLiveRendering = WouldUseLiveRendering(cliSettings);
            _liveLogMinimumLevel = minimumLevel;
            AttachLiveLogSinkIfReady();
        }
    }

    public void StartLiveRenderingIfNeeded()
    {
        if (!_canUseLiveRendering || _live != null || _disposed)
            return;

        lock (_liveGate)
        {
            if (_live != null || !_canUseLiveRendering || _disposed)
                return;

            if (_installedConsoleSink)
                SockseekLog.RemoveConsoleOutputs();

            _live = new TerminalLiveRenderer();
            Printing.LiveWriteLine = (line, _) => Publish(new CliOutputEvent.RawLine(line));
            AttachLiveLogSinkIfReady();
        }
    }

    public void Publish(CliOutputEvent outputEvent)
    {
        if (_disposed)
            return;

        _live?.Publish(outputEvent);
    }

    public void UpsertJob(JobView job)
        => Publish(new CliOutputEvent.UpsertJobView(job));

    public void UpsertJobRecord(TerminalJobRecord job)
        => Publish(new CliOutputEvent.UpsertJobRecord(job));

    public void RemoveJob(string id)
        => Publish(new CliOutputEvent.RemoveJob(id));

    public void SetRateLimited(DateTimeOffset? resetsAt)
        => Publish(new CliOutputEvent.RateLimit(resetsAt));

    public void SetStatusMessage(string? message)
        => Publish(new CliOutputEvent.StatusMessage(message));

    public void StopLiveRendering(bool printSummary = true)
    {
        lock (_liveGate)
        {
            if (_live == null)
                return;

            _live.Dispose(printSummary);
            _live = null;
            Printing.LiveWriteLine = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopLiveRendering();
    }

    private void AttachConsoleSink()
        => SockseekLog.AddStructuredConsoleSink(WriteLog);

    private void AttachLiveLogSinkIfReady()
    {
        if (!_installedConsoleSink || _live == null || _liveLogSinkAttached)
            return;

        SockseekLog.AddStructuredConsoleSink(WriteLog, _liveLogMinimumLevel);
        _liveLogSinkAttached = true;
    }

    private void WriteLog(SockseekLog.StructuredLogEntry entry, string _)
    {
        var outputEvent = CliOutputEvent.FromLogEntry(entry);

        if (_live != null)
        {
            if (outputEvent is CliOutputEvent.JobLog { Line.ShowInLive: false })
                return;

            _live.Publish(outputEvent);
            return;
        }

        CliLogStyle.WriteConsoleEvent(outputEvent, _forceHumanLogsToError);
    }
}
