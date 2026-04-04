using Soulseek;
using Jobs;
using Models;
using Enums;

using File = System.IO.File;
using Directory = System.IO.Directory;


public class Downloader
{
    private readonly DownloadEngine engine;

    public Downloader(DownloadEngine engine)
    {
        this.engine = engine;
    }

    public async Task DownloadFile(FileCandidate candidate, string outputPath, SongJob song, Config config, CancellationToken? ct = null, CancellationTokenSource? searchCts = null)
    {
        string fileKey = candidate.Username + '\\' + candidate.Filename;

        if (engine.downloadedFiles.TryGetValue(fileKey, out var existingSong))
        {
            lock (engine.downloadedFiles)
            {
                var existingPath     = existingSong.DownloadPath;
                var outputFileInfo   = new FileInfo(outputPath);
                var existingFileInfo = new FileInfo(existingPath ?? "");

                if (existingFileInfo.Exists && existingFileInfo.Length == candidate.File.Size)
                {
                    Logger.Debug($"File \"{candidate.Filename}\" already downloaded at {existingPath}");

                    if (!outputFileInfo.Exists || outputFileInfo.Length != existingFileInfo.Length)
                    {
                        Logger.Debug("Copying to new output path");
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        File.Copy(existingPath!, outputPath, true);
                    }

                    song.DownloadPath = outputPath;
                    song.State        = TrackState.Downloaded;
                    return;
                }
                else
                {
                    engine.downloadedFiles.TryRemove(fileKey, out _);
                }
            }
        }

        await engine.EnsureClientReadyAsync(config);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string incompleteOutputPath = config.noIncompleteExt ? outputPath : outputPath + ".incomplete";

        Logger.Debug($"Downloading: {song} to '{incompleteOutputPath}'");

        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                if (engine.downloads.TryGetValue(candidate.Filename, out var x))
                    x.Transfer = state.Transfer;
                engine.ProgressReporter.ReportDownloadStateChanged(song, GetStateLabel(state.Transfer.State));
            },
            progressUpdated: (progress) =>
            {
                if (engine.downloads.TryGetValue(candidate.Filename, out var x))
                    x.Song.BytesTransferred = progress.PreviousBytesTransferred;
                engine.ProgressReporter.ReportDownloadProgress(song, progress.PreviousBytesTransferred, candidate.File.Size > 0 ? candidate.File.Size : 0);
            }
        );

        try
        {
            using var downloadCts = ct != null
                ? CancellationTokenSource.CreateLinkedTokenSource((CancellationToken)ct)
                : new CancellationTokenSource();

            using var outputStream = new FileStream(incompleteOutputPath, FileMode.Create);

            song.FileSize = candidate.File.Size;
            var activeDownload = new ActiveDownload(song, candidate, downloadCts);
            engine.downloads.TryAdd(candidate.Filename, activeDownload);

            engine.ProgressReporter.ReportDownloadStart(song, candidate);

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await engine.Client!.DownloadAsync(candidate.Username, candidate.Filename,
                        () => Task.FromResult((Stream)outputStream),
                        candidate.File.Size == -1 ? null : candidate.File.Size,
                        startOffset: outputStream.Position,
                        options: transferOptions,
                        cancellationToken: downloadCts.Token);
                    break;
                }
                catch (SoulseekClientException e)
                {
                    retryCount++;
                    Logger.DebugError($"Error while downloading: {e}");
                    if (retryCount >= maxRetries || engine.IsConnectedAndLoggedIn)
                        throw;
                    await engine.EnsureClientReadyAsync(config);
                }
            }
        }
        catch
        {
            if (File.Exists(incompleteOutputPath))
                try { Utils.DeleteFileAndParentsIfEmpty(incompleteOutputPath, config.parentDir); } catch { }
            engine.downloads.TryRemove(candidate.Filename, out _);
            throw;
        }

        try { searchCts?.Cancel(); } catch { }

        if (!config.noIncompleteExt)
        {
            try { Utils.Move(incompleteOutputPath, outputPath); }
            catch (IOException e) { Logger.Error($"Failed to rename .incomplete file. Error: {e}"); }
        }

        engine.downloadedFiles[fileKey] = song;
        engine.downloads.TryRemove(candidate.Filename, out _);

        song.ChosenCandidate = candidate;
        song.DownloadPath    = outputPath;
        song.State           = TrackState.Downloaded;
    }

    static string GetStateLabel(TransferStates s)
    {
        if (s.HasFlag(TransferStates.InProgress))   return "InProgress";
        if (s.HasFlag(TransferStates.Queued))
            return s.HasFlag(TransferStates.Remotely) ? "Queued (R)" :
                   s.HasFlag(TransferStates.Locally)  ? "Queued (L)" : "Queued";
        if (s.HasFlag(TransferStates.Initializing)) return "Initialising";
        return "Requested";
    }
}
