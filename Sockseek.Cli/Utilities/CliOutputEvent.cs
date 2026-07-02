using Microsoft.Extensions.Logging;
using Sockseek.Core;

namespace Sockseek.Cli;

internal abstract record CliOutputEvent
{
    public sealed record JobLog(
        TerminalLogLine Line,
        LogLevel Level = LogLevel.Information,
        ConsoleColor? Color = null) : CliOutputEvent;

    public sealed record ProcessLog(TerminalProcessLogLine Line) : CliOutputEvent;

    public sealed record RawLine(string Text) : CliOutputEvent;

    public sealed record UpsertJobView(JobView Job) : CliOutputEvent;

    public sealed record UpsertJobRecord(TerminalJobRecord Job) : CliOutputEvent;

    public sealed record RemoveJob(string Id) : CliOutputEvent;

    public sealed record StatusMessage(string? Message) : CliOutputEvent;

    public sealed record RateLimit(DateTimeOffset? ResetsAt) : CliOutputEvent;

    public static CliOutputEvent FromLogEntry(SockseekLog.StructuredLogEntry entry)
    {
        if (entry.Context is JobLog jobLog)
        {
            return jobLog with
            {
                Level = entry.Level,
                Color = entry.Color ?? jobLog.Color,
            };
        }

        if (entry.Context is SockseekLog.JobLogContext jobLogContext)
        {
            return new JobLog(
                new TerminalLogLine(
                    TerminalLogKind.Status,
                    $"core:{jobLogContext.DisplayId}",
                    jobLogContext.DisplayId,
                    jobLogContext.JobType,
                    jobLogContext.Message,
                    jobLogContext.Source,
                    jobLogContext.Highlight,
                    jobLogContext.ShowInLive),
                entry.Level,
                entry.Color);
        }

        if (entry.Context is CliOutputEvent outputEvent)
            return outputEvent;

        return new ProcessLog(new TerminalProcessLogLine(
            entry.Level,
            entry.CategoryName,
            entry.Message,
            entry.Routing,
            entry.Color));
    }
}

internal sealed record TerminalProcessLogLine(
    LogLevel Level,
    string CategoryName,
    string Message,
    SockseekLog.LogRouting Routing,
    ConsoleColor? Color = null);
