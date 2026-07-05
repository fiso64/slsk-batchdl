using System.Diagnostics;
using System.Text;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public static class OnCompleteExecutor
{
    private const int MaxLoggedCommandOutputChars = 600;
    private const int MaxCapturedCommandOutputChars = 64 * 1024;
    private static readonly SemaphoreSlim _lockingSemaphore = new(1, 1);

    private enum CommandScope
    {
        Any,
        Track,
        Album,
    }

    private enum CommandWhen
    {
        Default,
        Any,
        Success,
        Failure,
        Skipped,
        AlreadyExists,
        NotFoundLastTime,
        Cancelled,
        PartialSuccess,
    }

    private struct CommandConfig
    {
        public string  Command                { get; set; }
        public bool    UseShellExecute        { get; set; }
        public bool    CreateNoWindow         { get; set; }
        public CommandScope Scope             { get; set; }
        public CommandWhen  When              { get; set; }
        public bool    UseOutputToUpdateIndex { get; set; }
        public bool    UseLocking             { get; set; }
    }

    private struct ProcessResult
    {
        public int     ExitCode { get; set; }
        public string? Stdout   { get; set; }
        public string? Stderr   { get; set; }
        public int     StdoutCharsRead { get; set; }
        public int     StderrCharsRead { get; set; }
        public bool    StdoutTruncated { get; set; }
        public bool    StderrTruncated { get; set; }
    }

    private readonly record struct CapturedProcessOutput(string? Text, int CharsRead, bool Truncated);

    private readonly record struct OnCompleteContext(FileManagerContext Variables, string? TagSourcePath);
    private readonly record struct CommandProcessingResult(JobOutcome Outcome, bool NeedsIndexUpdate);
    private readonly record struct UpdateIndexStateResult(JobOutcome? Outcome, bool AllowsPathUpdate);

    public static void ValidateCommand(string rawCommand)
        => _ = ParseCommand(rawCommand);

    public static void ValidateCommands(IEnumerable<string>? commands)
    {
        if (commands == null)
            return;

        foreach (var command in commands)
            ValidateCommand(command);
    }

    // Execute on-complete actions for a job.
    // song is null when called for an album-level completion (no individual song).
    public static async Task<JobOutcome> ExecuteAsync(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
    {
        if (!job.Config.HasOnComplete || job.Config.Output.OnComplete == null)
            return outcome;

        bool isAlbumOnComplete = IsAlbumOnComplete(job, song);

        // Build a FileManagerContext for variable substitution.
        string extractorName = job.Config.Extraction.InputType.ToString();
        string inputSource   = job.Config.Extraction.Input ?? "";
        string outputDir     = job.Config.Output.ParentDir ?? "";
        string configDir     = job.Config.RuntimePathContext.ConfigDir ?? "";

        var onCompleteContext = song != null
            ? BuildSongOnCompleteContext(song, job)
            : job is AlbumJob albumJob
                ? BuildAlbumOnCompleteContext(albumJob)
                : BuildJobOnCompleteContext(job);

        onCompleteContext = onCompleteContext with
        {
            Variables = onCompleteContext.Variables with
            {
                ExtractorName = extractorName,
                InputSource = inputSource,
                OutputDir = outputDir,
                ConfigDir = configDir,
            },
        };
        onCompleteContext = onCompleteContext with
        {
            Variables = ApplyOutcomeToContext(onCompleteContext.Variables, outcome),
        };

        var currentOutcome = outcome;
        bool needUpdateIndex    = false;
        ProcessResult? firstCommandResult = null;
        ProcessResult? prevCommandResult  = null;

        for (int i = 0; i < job.Config.Output.OnComplete.Count; i++)
        {
            string rawCommand = job.Config.Output.OnComplete[i];
            if (string.IsNullOrWhiteSpace(rawCommand))
                continue;

            CommandConfig config = ParseCommand(rawCommand);

            if (!ShouldExecuteCommand(config, outcome, isTrack: song != null, isAlbum: isAlbumOnComplete))
                continue;

            string preparedCommand = PrepareCommandString(config.Command, onCompleteContext, prevCommandResult, firstCommandResult);
            if (string.IsNullOrWhiteSpace(preparedCommand))
            {
                SockseekLog.Jobs.Warn(OnCompleteLogJob(job, song), $"skipping on-complete action {i + 1} because the prepared command is empty after variable replacement.");
                continue;
            }

            (string fileName, string argString) = ParseFileNameAndArguments(preparedCommand);
            ProcessStartInfo startInfo = ConfigureProcessStartInfo(fileName, argString, config);

            ProcessResult? currentResult = null;
            bool acquiredLock = false;

            try
            {
                if (config.UseLocking)
                {
                    SockseekLog.Jobs.Debug($"{OnCompleteLogPrefix(job, song)} on-complete [{i + 1}/{job.Config.Output.OnComplete.Count}]: waiting for lock");
                    await _lockingSemaphore.WaitAsync();
                    acquiredLock = true;
                }

                SockseekLog.Jobs.Debug($"{OnCompleteLogPrefix(job, song)} on-complete [{i + 1}/{job.Config.Output.OnComplete.Count}]: executing FileName='{startInfo.FileName}', {FormatProcessArgumentsForLog(startInfo)}, UseShellExecute={startInfo.UseShellExecute}, CreateNoWindow={startInfo.CreateNoWindow}, RedirectOutput={startInfo.RedirectStandardOutput}");
                currentResult = await ExecuteProcessAsync(startInfo);
            }
            finally
            {
                if (acquiredLock)
                    _lockingSemaphore.Release();
            }

            if (currentResult == null)
            {
                SockseekLog.Jobs.Error(OnCompleteLogJob(job, song), $"execution failed for on-complete command {i + 1}. Stopping further on-complete actions for this item.");
                return currentOutcome;
            }

            var processedResult = ProcessCommandResult(currentResult.Value, config, song, job, currentOutcome);
            currentOutcome = processedResult.Outcome;
            if (processedResult.NeedsIndexUpdate)
                needUpdateIndex = true;

            prevCommandResult = currentResult;
            if (i == 0) firstCommandResult = currentResult;
        }

        if (needUpdateIndex)
        {
            SockseekLog.Jobs.Debug($"{OnCompleteLogPrefix(job, song)} index/playlist will be updated based on on-complete action output.");
        }

        return currentOutcome;
    }

    public static bool HasApplicableCommand(Job job, SongJob? song, JobOutcome outcome)
    {
        if (!job.Config.HasOnComplete || job.Config.Output.OnComplete == null)
            return false;

        bool isAlbumOnComplete = IsAlbumOnComplete(job, song);

        foreach (var rawCommand in job.Config.Output.OnComplete)
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
                continue;

            if (ShouldExecuteCommand(ParseCommand(rawCommand), outcome, isTrack: song != null, isAlbum: isAlbumOnComplete))
                return true;
        }

        return false;
    }

    private static bool IsAlbumOnComplete(Job job, SongJob? song)
        => song == null && job is AlbumJob;

    private static OnCompleteContext BuildSongOnCompleteContext(SongJob song, Job parentJob)
    {
        var variables = FileManagerContext.FromSongJob(song, parentJob);
        return new OnCompleteContext(variables, song.DownloadPath);
    }

    private static OnCompleteContext BuildJobOnCompleteContext(Job job)
        => new(new FileManagerContext
        {
            Job = job,
            Query = job.QueryTrack ?? new SongQuery(),
            LifecycleState = job.LifecycleState,
            ActivityPhase = job.ActivityPhase,
            TerminalOutcome = job.TerminalOutcome,
            SkipReason = job.SkipReason,
            FailureReason = job.FailureReason,
            LineNumber = job.LineNumber,
            ItemNumber = job.ItemNumber,
        }, TagSourcePath: null);

    private static OnCompleteContext BuildAlbumOnCompleteContext(AlbumJob albumJob)
    {
        // Album-level on-complete uses the album as the event context, but
        // reads tag variables from the first audio file as its representative.
        var representativeFile = albumJob.TrackJobs.FirstOrDefault(f => !f.IsNotAudio);

        var variables = new FileManagerContext
        {
            Job = albumJob,
            Query = new SongQuery
            {
                Artist = albumJob.Query.Artist,
                Album = albumJob.Query.Album,
                Title = albumJob.Query.SearchHint,
                URI = albumJob.Query.URI,
                ArtistMaybeWrong = albumJob.Query.ArtistMaybeWrong,
            },
            Candidate = representativeFile?.ChosenCandidate ?? representativeFile?.Candidates?.FirstOrDefault(),
            DownloadPath = albumJob.DownloadPath,
            LifecycleState = albumJob.LifecycleState,
            ActivityPhase = albumJob.ActivityPhase,
            TerminalOutcome = albumJob.TerminalOutcome,
            SkipReason = albumJob.SkipReason,
            FailureReason = albumJob.FailureReason,
            IsNotAudio = false,
            LineNumber = albumJob.LineNumber,
            ItemNumber = albumJob.ItemNumber,
        };

        return new OnCompleteContext(variables, representativeFile?.DownloadPath);
    }

    private static FileManagerContext ApplyOutcomeToContext(FileManagerContext ctx, JobOutcome outcome)
    {
        if (!outcome.IsTerminal)
            return ctx;

        return ctx with
        {
            DownloadPath = outcome.DownloadPath ?? ctx.DownloadPath,
            LifecycleState = JobLifecycleState.Terminal,
            ActivityPhase = JobActivityPhase.None,
            TerminalOutcome = outcome.TerminalOutcome,
            SkipReason = outcome.SkipReason,
            FailureReason = outcome.FailureReason,
        };
    }

    private static string OnCompleteLogPrefix(Job job, SongJob? song)
    {
        if (song != null)
            return $"[{song.DisplayId}] SongJob:";

        return job switch
        {
            AlbumJob => $"[{job.DisplayId}] AlbumJob:",
            JobList => $"[{job.DisplayId}] Job List:",
            SearchJob => $"[{job.DisplayId}] SearchJob:",
            _ => $"[{job.DisplayId}] {job.GetType().Name}:",
        };
    }

    private static Job OnCompleteLogJob(Job job, SongJob? song)
        => song ?? job;

    private static CommandConfig ParseCommand(string rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
            throw InvalidOnCompleteCommand(rawCommand, "Command is empty.");

        var delimiterIndex = FindCommandDelimiter(rawCommand);
        if (delimiterIndex < 0)
            throw InvalidOnCompleteCommand(rawCommand, "Missing `--` command delimiter.");

        var optionText = rawCommand[..delimiterIndex].Trim();
        var command = rawCommand[(delimiterIndex + 2)..].Trim();
        if (string.IsNullOrWhiteSpace(command))
            throw InvalidOnCompleteCommand(rawCommand, "Command after `--` is empty.");

        var config = new CommandConfig { Command = command };
        foreach (var option in SplitOptionTokens(optionText))
            ApplyCommandOption(ref config, option, rawCommand);

        return config;
    }

    private static int FindCommandDelimiter(string rawCommand)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < rawCommand.Length - 1; i++)
        {
            var c = rawCommand[i];
            if (c == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            if (inSingleQuote || inDoubleQuote)
                continue;

            if (rawCommand[i] != '-' || rawCommand[i + 1] != '-')
                continue;

            var beforeOk = i == 0 || char.IsWhiteSpace(rawCommand[i - 1]);
            var afterIndex = i + 2;
            var afterOk = afterIndex == rawCommand.Length || char.IsWhiteSpace(rawCommand[afterIndex]);
            if (beforeOk && afterOk)
                return i;
        }

        return -1;
    }

    private static string[] SplitOptionTokens(string optionText)
        => string.IsNullOrWhiteSpace(optionText)
            ? []
            : optionText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ApplyCommandOption(ref CommandConfig config, string option, string rawCommand)
    {
        switch (option)
        {
            case "hidden":
                config.CreateNoWindow = true;
                return;
            case "shell":
                config.UseShellExecute = true;
                return;
            case "lock":
                config.UseLocking = true;
                return;
            case "update-index":
                config.UseOutputToUpdateIndex = true;
                return;
        }

        if (option.StartsWith("scope=", StringComparison.OrdinalIgnoreCase))
        {
            SetScope(ref config, option["scope=".Length..], rawCommand);
            return;
        }

        if (option.StartsWith("when=", StringComparison.OrdinalIgnoreCase))
        {
            SetWhen(ref config, option["when=".Length..], rawCommand);
            return;
        }

        throw InvalidOnCompleteCommand(rawCommand, $"Unknown option `{option}`.");
    }

    private static void SetScope(ref CommandConfig config, string value, string rawCommand)
    {
        if (config.Scope != CommandScope.Any)
            throw InvalidOnCompleteCommand(rawCommand, "`scope` was specified more than once.");

        config.Scope = value.ToLowerInvariant() switch
        {
            "track" => CommandScope.Track,
            "album" => CommandScope.Album,
            _ => throw InvalidOnCompleteCommand(rawCommand, $"Unknown scope `{value}`. Use `scope=track` or `scope=album`.")
        };
    }

    private static void SetWhen(ref CommandConfig config, string value, string rawCommand)
    {
        if (config.When != CommandWhen.Default)
            throw InvalidOnCompleteCommand(rawCommand, "`when` was specified more than once.");

        config.When = value.ToLowerInvariant() switch
        {
            "any" => CommandWhen.Any,
            "completed" => CommandWhen.Default,
            "success" or "succeeded" => CommandWhen.Success,
            "failure" or "failed" => CommandWhen.Failure,
            "skipped" => CommandWhen.Skipped,
            "already-exists" or "alreadyexists" => CommandWhen.AlreadyExists,
            "not-found-last-time" or "not-found" or "notfound" => CommandWhen.NotFoundLastTime,
            "cancelled" or "canceled" => CommandWhen.Cancelled,
            // PartialSuccess is currently produced by container-style jobs, but on-complete
            // actions are only invoked for track completions and AlbumJob-level completions.
            // Do not document it as usable unless container-level on-complete execution is added.
            "partial" or "partial-success" => CommandWhen.PartialSuccess,
            _ => throw InvalidOnCompleteCommand(rawCommand, $"Unknown when value `{value}`.")
        };
    }

    private static ArgumentException InvalidOnCompleteCommand(string rawCommand, string reason)
    {
        var legacyHint = LooksLikeLegacyPrefixSyntax(rawCommand)
            ? " Legacy one-letter prefixes are no longer supported in 3.0."
            : "";
        return new ArgumentException(
            $"Input error: Invalid on-complete command. {reason}{legacyHint} Use `--` to separate Sockseek options from the command, for example: `on-complete = when=success scope=album hidden -- cmd /d /c notify.cmd \"{{path}}\"`.");
    }

    private static bool LooksLikeLegacyPrefixSyntax(string rawCommand)
    {
        var command = rawCommand.TrimStart();
        var consumedAny = false;
        while (command.Length > 2 && command[1] == ':')
        {
            var flag = command[0];
            if (!char.IsDigit(flag) && flag is not ('s' or 't' or 'a' or 'h' or 'u' or 'l' or 'r'))
                return consumedAny;

            consumedAny = true;
            command = command[2..];
        }

        return consumedAny;
    }

    private static bool ShouldExecuteCommand(CommandConfig config, JobOutcome outcome, bool isTrack, bool isAlbum)
    {
        if (config.Scope == CommandScope.Track && !isTrack) return false;
        if (config.Scope == CommandScope.Album && !isAlbum) return false;

        return config.When switch
        {
            CommandWhen.Default => outcome.TerminalOutcome != JobTerminalOutcome.Skipped,
            CommandWhen.Any => true,
            CommandWhen.Success => outcome.TerminalOutcome == JobTerminalOutcome.Succeeded,
            CommandWhen.Failure => outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.PartialSuccess,
            CommandWhen.Skipped => outcome.TerminalOutcome == JobTerminalOutcome.Skipped,
            CommandWhen.AlreadyExists => outcome.TerminalOutcome == JobTerminalOutcome.Skipped
                && outcome.SkipReason == JobSkipReason.AlreadyExists,
            CommandWhen.NotFoundLastTime => outcome.TerminalOutcome == JobTerminalOutcome.Skipped
                && outcome.SkipReason == JobSkipReason.NotFoundLastTime,
            CommandWhen.Cancelled => outcome.TerminalOutcome == JobTerminalOutcome.Cancelled,
            CommandWhen.PartialSuccess => outcome.TerminalOutcome == JobTerminalOutcome.PartialSuccess,
            _ => false,
        };
    }

    private static string PrepareCommandString(string commandTemplate, OnCompleteContext ctx, ProcessResult? prevResult, ProcessResult? firstResult)
    {
        TagLib.File? audio = null;
        if (FileManager.HasTagVariables(commandTemplate))
        {
            try
            {
                var tagSourcePath = ctx.TagSourcePath ?? ctx.Variables.DownloadPath;
                if (!string.IsNullOrEmpty(tagSourcePath) && System.IO.File.Exists(tagSourcePath))
                    audio = TagLib.File.Create(tagSourcePath);
                else
                    SockseekLog.Warn($"Cannot load tags for variable replacement: tag source path is null or file does not exist ('{tagSourcePath}')");
            }
            catch (Exception ex)
            {
                SockseekLog.Warn($"Failed to load audio tags for variable replacement from '{ctx.TagSourcePath ?? ctx.Variables.DownloadPath}': {ex.Message}");
            }
        }

        try
        {
            string command = FileManager.ReplaceVariables(commandTemplate, ctx.Variables, audio);

            command = command
                .Replace("{exitcode}",       prevResult?.ExitCode.ToString()  ?? "-1")
                .Replace("{first-exitcode}", firstResult?.ExitCode.ToString() ?? "-1")
                .Replace("{stdout}",         string.IsNullOrWhiteSpace(prevResult?.Stdout)  ? "null" : prevResult.Value.Stdout)
                .Replace("{stderr}",         string.IsNullOrWhiteSpace(prevResult?.Stderr)  ? "null" : prevResult.Value.Stderr)
                .Replace("{first-stdout}",   string.IsNullOrWhiteSpace(firstResult?.Stdout) ? "null" : firstResult.Value.Stdout)
                .Replace("{first-stderr}",   string.IsNullOrWhiteSpace(firstResult?.Stderr) ? "null" : firstResult.Value.Stderr);

            return command.Trim();
        }
        finally
        {
            audio?.Dispose();
        }
    }

    private static (string FileName, string ArgumentsString) ParseFileNameAndArguments(string preparedCommand)
    {
        preparedCommand = preparedCommand.Trim();
        if (string.IsNullOrEmpty(preparedCommand)) return ("", "");

        string fileName;
        string arguments = "";

        if (preparedCommand.StartsWith('"'))
        {
            int endQuoteIndex = preparedCommand.IndexOf('"', 1);
            if (endQuoteIndex > 0)
            {
                fileName = preparedCommand.Substring(1, endQuoteIndex - 1);
                if (preparedCommand.Length > endQuoteIndex + 1)
                    arguments = preparedCommand.Substring(endQuoteIndex + 1).TrimStart();
            }
            else
            {
                fileName = preparedCommand.Trim('"');
            }
        }
        else
        {
            int firstSpaceIndex = preparedCommand.IndexOf(' ');
            if (firstSpaceIndex > 0)
            {
                fileName  = preparedCommand.Substring(0, firstSpaceIndex);
                arguments = preparedCommand.Substring(firstSpaceIndex + 1).TrimStart();
            }
            else
            {
                fileName = preparedCommand;
            }
        }

        return (fileName, arguments);
    }

    private static ProcessStartInfo ConfigureProcessStartInfo(string fileName, string argString, CommandConfig config)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName        = fileName,
            Arguments       = argString,
            UseShellExecute = config.UseShellExecute,
            CreateNoWindow  = config.CreateNoWindow,
        };

        if (!config.UseShellExecute || config.UseOutputToUpdateIndex)
        {
            startInfo.UseShellExecute          = false;
            startInfo.RedirectStandardOutput   = true;
            startInfo.RedirectStandardError    = true;
            startInfo.StandardOutputEncoding   = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding    = System.Text.Encoding.UTF8;
        }

        return startInfo;
    }

    private static string FormatProcessArgumentsForLog(ProcessStartInfo startInfo)
    {
        if (startInfo.ArgumentList.Count == 0)
            return $"Arguments='{startInfo.Arguments}'";

        var args = string.Join(", ", startInfo.ArgumentList.Select(arg => $"'{arg.Replace("'", "\\'")}'"));
        return $"ArgumentList=[{args}]";
    }

    private static async Task<ProcessResult?> ExecuteProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                SockseekLog.Error($"Failed to start process: FileName='{startInfo.FileName}', {FormatProcessArgumentsForLog(startInfo)}");
                return null;
            }

            Task<CapturedProcessOutput>? readStdoutTask = startInfo.RedirectStandardOutput ? CaptureProcessOutputAsync(process.StandardOutput) : null;
            Task<CapturedProcessOutput>? readStderrTask = startInfo.RedirectStandardError  ? CaptureProcessOutputAsync(process.StandardError)  : null;

            await process.WaitForExitAsync();

            var stdout = readStdoutTask != null ? await readStdoutTask : default;
            var stderr = readStderrTask != null ? await readStderrTask : default;

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                Stdout = CleanCapturedOutput(stdout.Text),
                Stderr = CleanCapturedOutput(stderr.Text),
                StdoutCharsRead = stdout.CharsRead,
                StderrCharsRead = stderr.CharsRead,
                StdoutTruncated = stdout.Truncated,
                StderrTruncated = stderr.Truncated,
            };
        }
        catch (Exception ex)
        {
            SockseekLog.Error($"Error executing process: FileName='{startInfo.FileName}', {FormatProcessArgumentsForLog(startInfo)}. Exception: {ex}");
            return null;
        }
    }

    private static async Task<CapturedProcessOutput> CaptureProcessOutputAsync(StreamReader reader)
    {
        var builder = new StringBuilder(Math.Min(MaxCapturedCommandOutputChars, 4096));
        var buffer = new char[4096];
        var charsRead = 0;

        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            charsRead += read;
            var remaining = MaxCapturedCommandOutputChars - builder.Length;
            if (remaining > 0)
                builder.Append(buffer, 0, Math.Min(read, remaining));
        }

        return new CapturedProcessOutput(
            builder.Length > 0 ? builder.ToString() : null,
            charsRead,
            charsRead > MaxCapturedCommandOutputChars);
    }

    private static string? CleanCapturedOutput(string? output)
        => string.IsNullOrWhiteSpace(output) ? null : output.Trim().Trim('"');

    private static CommandProcessingResult ProcessCommandResult(ProcessResult result, CommandConfig config, SongJob? song, Job job, JobOutcome currentOutcome)
    {
        bool needsUpdate = false;
        var nextOutcome = currentOutcome;
        var logJob = OnCompleteLogJob(job, song);
        var logPrefix = OnCompleteLogPrefix(job, song);

        if (config.UseOutputToUpdateIndex && !string.IsNullOrWhiteSpace(result.Stdout))
        {
            if (result.StdoutTruncated)
            {
                SockseekLog.Jobs.Warn(logJob, $"ignored on-complete stdout for index update because command output exceeded the capture limit.\n{FormatCommandOutputBlock("Stdout", result.Stdout, result.StdoutTruncated, result.StdoutCharsRead)}");
                return new CommandProcessingResult(nextOutcome, needsUpdate);
            }

            string[] parts = result.Stdout.Split(';', 2);
            string stateText = parts[0].Trim();
            string? newPath = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                ? parts[1].Trim()
                : null;
            var indexTarget = GetUpdateIndexTarget(job, song);

            var stateResult = TryGetUpdateIndexStateOutcome(stateText, indexTarget, logJob, result, nextOutcome, newPath);
            if (stateResult.Outcome is { } stateOutcome)
            {
                nextOutcome = stateOutcome;
                needsUpdate = true;
            }

            if (stateResult.AllowsPathUpdate && newPath != null && indexTarget != null)
            {
                var currentPath = nextOutcome.DownloadPath ?? GetDownloadPath(indexTarget);
                if (currentPath != newPath)
                {
                    SockseekLog.Jobs.Debug($"{logPrefix} updating index path from '{currentPath}' to '{newPath}' based on stdout: {indexTarget}");
                    nextOutcome = WithDownloadPath(nextOutcome, newPath);
                    needsUpdate = true;
                }
            }
            else if (stateResult.AllowsPathUpdate && newPath != null && indexTarget == null)
            {
                SockseekLog.Jobs.Warn(logJob, $"ignored on-complete stdout path update because index path can only be updated for track- or album-level completions.\n{FormatCommandOutputBlock("Stdout", result.Stdout, result.StdoutTruncated, result.StdoutCharsRead)}");
            }
        }

        if (result.ExitCode != 0)
            SockseekLog.Jobs.Warn(logJob, $"on-complete command exited with code {result.ExitCode}. {FormatCommandOutputForLog(result)}");

        return new CommandProcessingResult(nextOutcome, needsUpdate);
    }

    private static Job? GetUpdateIndexTarget(Job job, SongJob? song)
        => song ?? (job is AlbumJob ? job : null);

    private static string? GetDownloadPath(Job job)
        => job switch
        {
            SongJob song => song.DownloadPath,
            AlbumJob album => album.DownloadPath,
            _ => null,
        };

    private static UpdateIndexStateResult TryGetUpdateIndexStateOutcome(string stateText, Job? indexTarget, Job logJob, ProcessResult result, JobOutcome currentOutcome, string? newPath)
    {
        if (string.IsNullOrWhiteSpace(stateText)
            || stateText.Equals("ignored", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("ignore", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("unchanged", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("no-change", StringComparison.OrdinalIgnoreCase))
            return new UpdateIndexStateResult(null, AllowsPathUpdate: true);

        if (indexTarget == null)
        {
            SockseekLog.Jobs.Warn(logJob, $"ignored on-complete stdout state update because index state can only be updated for track- or album-level completions.\n{FormatCommandOutputBlock("Stdout", result.Stdout!, result.StdoutTruncated, result.StdoutCharsRead)}");
            return new UpdateIndexStateResult(null, AllowsPathUpdate: false);
        }

        switch (stateText.ToLowerInvariant())
        {
            case "success":
            case "succeeded":
            case "done":
            case "downloaded":
                var path = newPath ?? currentOutcome.DownloadPath ?? GetDownloadPath(indexTarget);
                var successOutcome = indexTarget is SongJob song
                    ? JobOutcome.Done(path, currentOutcome.ChosenCandidate ?? song.ChosenCandidate, currentOutcome.DownloadSource != SongDownloadSource.None ? currentOutcome.DownloadSource : song.DownloadSource)
                    : JobOutcome.Done(path);
                return new UpdateIndexStateResult(successOutcome, AllowsPathUpdate: true);

            case "failure":
            case "failed":
                var failureReason = currentOutcome.FailureReason is JobFailureReason.None or JobFailureReason.Cancelled
                    ? indexTarget.FailureReason is JobFailureReason.None or JobFailureReason.Cancelled
                        ? JobFailureReason.Other
                        : indexTarget.FailureReason
                    : currentOutcome.FailureReason;
                if (newPath != null)
                    SockseekLog.Jobs.Warn(logJob, $"ignored path after failed on-complete index state because failed index entries clear their stored path.\n{FormatCommandOutputBlock("Stdout", result.Stdout!, result.StdoutTruncated, result.StdoutCharsRead)}");
                return new UpdateIndexStateResult(
                    JobOutcome.Failed(failureReason, currentOutcome.FailureMessage, currentOutcome.FailureDetail, clearDownloadPath: true),
                    AllowsPathUpdate: false);

            default:
                SockseekLog.Jobs.Warn(logJob, $"ignored unknown on-complete index state '{stateText}'. Use success, failed, or ignored.\n{FormatCommandOutputBlock("Stdout", result.Stdout!, result.StdoutTruncated, result.StdoutCharsRead)}");
                return new UpdateIndexStateResult(null, AllowsPathUpdate: false);
        }
    }

    private static JobOutcome WithDownloadPath(JobOutcome outcome, string path)
    {
        return outcome.TerminalOutcome switch
        {
            JobTerminalOutcome.Succeeded => JobOutcome.Done(path, outcome.ChosenCandidate, outcome.DownloadSource),
            JobTerminalOutcome.Failed => JobOutcome.Failed(
                FailureReasonForFailedIndexUpdate(outcome.FailureReason),
                outcome.FailureMessage,
                outcome.FailureDetail,
                path),
            JobTerminalOutcome.Cancelled => JobOutcome.Cancelled(
                outcome.CancellationSource == JobCancellationSource.None
                    ? JobCancellationSource.InternalEngine
                    : outcome.CancellationSource,
                outcome.FailureMessage,
                outcome.FailureDetail,
                path),
            JobTerminalOutcome.Skipped => JobOutcome.Skipped(outcome.SkipReason, outcome.FailureReason, path),
            JobTerminalOutcome.PartialSuccess => JobOutcome.PartialSuccess(outcome.FailureMessage, outcome.CancellationSource, path),
            _ => outcome,
        };
    }

    private static JobFailureReason FailureReasonForFailedIndexUpdate(JobFailureReason reason)
        => reason is JobFailureReason.None or JobFailureReason.Cancelled
            ? JobFailureReason.Other
            : reason;

    private static string FormatCommandOutputForLog(ProcessResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            parts.Add(FormatCommandOutputBlock("Stdout", result.Stdout, result.StdoutTruncated, result.StdoutCharsRead));
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            parts.Add(FormatCommandOutputBlock("Stderr", result.Stderr, result.StderrTruncated, result.StderrCharsRead));

        return parts.Count == 0
            ? " No captured output."
            : "\n" + string.Join("\n", parts);
    }

    private static string FormatCommandOutputBlock(string label, string output, bool captureTruncated = false, int charsRead = 0)
        => $"    {label}:\n{IndentCommandOutput(SummarizeCommandOutput(output, captureTruncated, charsRead))}";

    private static string SummarizeCommandOutput(string output, bool captureTruncated = false, int charsRead = 0)
    {
        var normalized = output
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();

        var totalLength = normalized.Length;
        if (totalLength <= MaxLoggedCommandOutputChars && !captureTruncated)
            return normalized;

        var excerpt = normalized[..Math.Min(totalLength, MaxLoggedCommandOutputChars)].TrimEnd();
        var logOmitted = Math.Max(0, totalLength - MaxLoggedCommandOutputChars);
        var logNote = logOmitted > 0
            ? $"log excerpt truncated, {logOmitted} chars omitted"
            : "output capture truncated";
        var captureNote = captureTruncated
            ? $"; captured first {MaxCapturedCommandOutputChars} of {charsRead} chars"
            : "";
        return $"{excerpt}\n... ({logNote}{captureNote})";
    }

    private static string IndentCommandOutput(string output)
    {
        var lines = output.Split('\n');
        return string.Join('\n', lines.Select(line => $"      {line}"));
    }
}
