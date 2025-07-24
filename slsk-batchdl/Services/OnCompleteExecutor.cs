using System.Diagnostics;
using Models;
using Enums;

public static class OnCompleteExecutor
{
    private static readonly SemaphoreSlim _lockingSemaphore = new(1, 1);

    // Helper struct to hold parsed flags and the remaining command
    private struct CommandConfig
    {
        public string Command { get; set; }
        public bool UseShellExecute { get; set; }
        public bool CreateNoWindow { get; set; }
        public bool OnlyTrackOnComplete { get; set; }
        public bool OnlyAlbumOnComplete { get; set; }
        public bool ReadOutput { get; set; }
        public bool UseOutputToUpdateIndex { get; set; }
        public int? RequiredTrackState { get; set; }
        public bool UseLocking { get; set; }
    }

    private struct ProcessResult
    {
        public int ExitCode { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
    }

    /// <summary>
    /// Executes the on-complete actions defined in the TrackListEntry configuration.
    /// </summary>
    public static async Task ExecuteAsync(TrackListEntry tle, Track track, M3uEditor? indexEditor, M3uEditor? playlistEditor)
    {
        if (!tle.config.HasOnComplete || tle.config.onComplete == null)
            return;

        bool isAlbumOnComplete = track.Type == TrackType.Album;
        bool needUpdateIndex = false;

        ProcessResult? firstCommandResult = null;
        ProcessResult? prevCommandResult = null;

        for (int i = 0; i < tle.config.onComplete.Count; i++)
        {
            string rawCommand = tle.config.onComplete[i];
            if (string.IsNullOrWhiteSpace(rawCommand))
                continue;

            CommandConfig config = ParseCommandFlags(rawCommand);

            if (!ShouldExecuteCommand(config, track.State, isAlbumOnComplete))
                continue;

            string preparedCommand = PrepareCommandString(config.Command, tle, track, prevCommandResult, firstCommandResult);
            if (string.IsNullOrWhiteSpace(preparedCommand))
            {
                Logger.Warn($"Skipping on-complete action {i + 1} because the prepared command is empty after variable replacement.");
                continue;
            }

            (string fileName, string arguments) = ParseFileNameAndArguments(preparedCommand);

            ProcessStartInfo startInfo = ConfigureProcessStartInfo(fileName, arguments, config);

            ProcessResult? currentResult = null;
            bool acquiredLock = false;

            try
            {
                if (config.UseLocking)
                {
                    Logger.Debug($"on-complete [{i + 1}/{tle.config.onComplete.Count}]: Waiting for lock...");
                    await _lockingSemaphore.WaitAsync();
                    acquiredLock = true;
                }

                Logger.Debug($"on-complete [{i + 1}/{tle.config.onComplete.Count}]: Executing: FileName='{startInfo.FileName}', Arguments='{startInfo.Arguments}', UseShellExecute={startInfo.UseShellExecute}, CreateNoWindow={startInfo.CreateNoWindow}, RedirectOutput={startInfo.RedirectStandardOutput}");

                currentResult = await ExecuteProcessAsync(startInfo);
            }
            finally
            {
                if (acquiredLock)
                {
                    _lockingSemaphore.Release();
                }
            }
            if (currentResult == null)
            {
                Logger.Error($"Execution failed for command {i + 1}. Stopping further on-complete actions for this item.");
                return;
            }

            prevCommandResult = currentResult;
            if (i == 0)
            {
                firstCommandResult = currentResult;
            }

            if (ProcessCommandResult(currentResult.Value, config, track))
            {
                needUpdateIndex = true;
            }
        }

        if (needUpdateIndex)
        {
            indexEditor?.Update();
            playlistEditor?.Update();
            Logger.Debug($"Index/Playlist updated based on on-complete action output for track: {track}");
        }
    }

    /// <summary>
    /// Parses the command prefix flags (e.g., "s:", "t:", "h:").
    /// </summary>
    private static CommandConfig ParseCommandFlags(string rawCommand)
    {
        var config = new CommandConfig { Command = rawCommand };

        while (config.Command.Length > 2 && config.Command[1] == ':')
        {
            char flag = config.Command[0];
            string remaining = config.Command[2..];

            switch (flag)
            {
                case 's': config.UseShellExecute = true; config.Command = remaining; break;
                case 't': config.OnlyTrackOnComplete = true; config.Command = remaining; break;
                case 'a': config.OnlyAlbumOnComplete = true; config.Command = remaining; break;
                case 'h': config.CreateNoWindow = true; config.Command = remaining; break;
                case 'u': config.UseOutputToUpdateIndex = true; config.Command = remaining; break;
                case 'r': config.ReadOutput = true; config.Command = remaining; break;
                case 'l': config.UseLocking = true; config.Command = remaining; break;
                default:
                    if (char.IsDigit(flag) && int.TryParse(flag.ToString(), out int state))
                    {
                        config.RequiredTrackState = state;
                        config.Command = remaining;
                    }
                    else
                    {
                        return config;
                    }
                    break;
            }
        }
        return config;
    }

    /// <summary>
    /// Checks if the command should be executed based on parsed flags and track state/type.
    /// </summary>
    private static bool ShouldExecuteCommand(CommandConfig config, TrackState currentState, bool isAlbum)
    {
        if (config.OnlyTrackOnComplete && isAlbum) return false;
        if (config.OnlyAlbumOnComplete && !isAlbum) return false;
        if (config.RequiredTrackState.HasValue && (int)currentState != config.RequiredTrackState.Value) return false;

        return true;
    }

    /// <summary>
    /// Replaces variables in the command string.
    /// </summary>
    private static string PrepareCommandString(string commandTemplate, TrackListEntry tle, Track track, ProcessResult? prevResult, ProcessResult? firstResult)
    {
        TagLib.File? audio = null;
        if (FileManager.HasTagVariables(commandTemplate))
        {
            try
            {
                if (!string.IsNullOrEmpty(track.DownloadPath) && System.IO.File.Exists(track.DownloadPath))
                {
                    audio = TagLib.File.Create(track.DownloadPath);
                }
                else
                {
                    Logger.Warn($"Cannot load tags for variable replacement: DownloadPath is null or file does not exist ('{track.DownloadPath}')");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load audio tags for variable replacement from '{track.DownloadPath}': {ex.Message}");
            }
        }

        try
        {
            string command = FileManager.ReplaceVariables(commandTemplate, tle, audio, track.FirstDownload, track, null);

            command = command
                .Replace("{exitcode}", prevResult?.ExitCode.ToString() ?? "-1")
                .Replace("{first-exitcode}", firstResult?.ExitCode.ToString() ?? "-1")
                .Replace("{stdout}", string.IsNullOrWhiteSpace(prevResult?.Stdout) ? "null" : prevResult.Value.Stdout)
                .Replace("{stderr}", string.IsNullOrWhiteSpace(prevResult?.Stderr) ? "null" : prevResult.Value.Stderr)
                .Replace("{first-stdout}", string.IsNullOrWhiteSpace(firstResult?.Stdout) ? "null" : firstResult.Value.Stdout)
                .Replace("{first-stderr}", string.IsNullOrWhiteSpace(firstResult?.Stderr) ? "null" : firstResult.Value.Stderr);

            return command.Trim();
        }
        finally
        {
            audio?.Dispose();
        }
    }


    /// <summary>
    /// Parses the command string into FileName and Arguments, handling quotes.
    /// </summary>
    private static (string FileName, string Arguments) ParseFileNameAndArguments(string preparedCommand)
    {
        string fileName;
        string arguments = "";

        preparedCommand = preparedCommand.Trim();

        if (string.IsNullOrEmpty(preparedCommand))
        {
            return ("", "");
        }

        if (preparedCommand.StartsWith('"'))
        {
            int endQuoteIndex = preparedCommand.IndexOf('"', 1);
            if (endQuoteIndex > 0)
            {
                fileName = preparedCommand.Substring(1, endQuoteIndex - 1);
                if (preparedCommand.Length > endQuoteIndex + 1)
                {
                    arguments = preparedCommand.Substring(endQuoteIndex + 1).TrimStart();
                }
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
                fileName = preparedCommand.Substring(0, firstSpaceIndex);
                arguments = preparedCommand.Substring(firstSpaceIndex + 1).TrimStart();
            }
            else
            {
                fileName = preparedCommand;
            }
        }

        return (fileName, arguments);
    }

    /// <summary>
    /// Configures the ProcessStartInfo based on parsed flags.
    /// </summary>
    private static ProcessStartInfo ConfigureProcessStartInfo(string fileName, string arguments, CommandConfig config)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = config.UseShellExecute,
            CreateNoWindow = config.CreateNoWindow
        };

        // If output needs to be read (for update or just capture), force off ShellExecute and enable redirection.
        if (config.UseOutputToUpdateIndex || config.ReadOutput)
        {
            startInfo.UseShellExecute = false; // Cannot redirect streams with ShellExecute=true
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }

        return startInfo;
    }

    /// <summary>
    /// Executes the configured process and captures its results.
    /// </summary>
    /// <returns>A ProcessResult containing exit code and output, or null if execution failed.</returns>
    private static async Task<ProcessResult?> ExecuteProcessAsync(ProcessStartInfo startInfo)
    {
        using (var process = new Process { StartInfo = startInfo })
        {
            try
            {
                if (!process.Start())
                {
                    Logger.Error($"Failed to start process: FileName='{startInfo.FileName}', Arguments='{startInfo.Arguments}'");
                    return null;
                }

                Task<string>? readStdoutTask = null;
                Task<string>? readStderrTask = null;
                string? stdout = null;
                string? stderr = null;

                if (startInfo.RedirectStandardOutput)
                {
                    readStdoutTask = process.StandardOutput.ReadToEndAsync();
                }
                if (startInfo.RedirectStandardError)
                {
                    readStderrTask = process.StandardError.ReadToEndAsync();
                }

                await process.WaitForExitAsync();

                if (readStdoutTask != null)
                {
                    stdout = (await readStdoutTask).Trim().Trim('"');
                }
                if (readStderrTask != null)
                {
                    stderr = (await readStderrTask).Trim().Trim('"');
                }

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Stdout = stdout,
                    Stderr = stderr
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error executing process: FileName='{startInfo.FileName}', Arguments='{startInfo.Arguments}'. Exception: {ex}");
                return null;
            }
        }
    }

    /// <summary>
    /// Processes the result of a command execution, updating track state if configured.
    /// </summary>
    /// <returns>True if the index needs updating, false otherwise.</returns>
    private static bool ProcessCommandResult(ProcessResult result, CommandConfig config, Track track)
    {
        bool needsUpdate = false;
        if (config.UseOutputToUpdateIndex && !string.IsNullOrWhiteSpace(result.Stdout))
        {
            string[] parts = result.Stdout.Split(';', 2);
            if (int.TryParse(parts[0], out int newState))
            {
                var newTrackState = (TrackState)newState;
                if (track.State != newTrackState)
                {
                    Logger.Info($"Updating track {track} state from {track.State} to {newTrackState} based on stdout.");
                    track.State = newTrackState;
                    needsUpdate = true;
                }

                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    string newPath = parts[1].Trim();
                    if (track.DownloadPath != newPath)
                    {
                        Logger.Info($"Updating track {track} path to '{newPath}' based on stdout.");
                        track.DownloadPath = newPath;
                        needsUpdate = true;
                    }
                }
            }
            else
            {
                Logger.Warn($"Could not parse new state from stdout for track {track}. Stdout: '{result.Stdout}'");
            }
        }

        if (result.ExitCode != 0)
        {
            Logger.DebugError($"Command finished with non-zero exit code {result.ExitCode}. Stdout: '{result.Stdout ?? "N/A"}', Stderr: '{result.Stderr ?? "N/A"}'");
        }


        return needsUpdate;
    }
}
