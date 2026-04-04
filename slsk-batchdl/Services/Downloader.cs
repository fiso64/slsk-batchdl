using Soulseek;
using Jobs;
using Models;
using Enums;
using Utilities;

using File = System.IO.File;
using Directory = System.IO.Directory;


public class Downloader
{
    private readonly ISoulseekClient client;
    private readonly SoulseekClientManager clientManager;
    private readonly IDownloadRegistry downloadRegistry;
    private readonly IProgressReporter progressReporter;

    public Downloader(ISoulseekClient client, 
                      SoulseekClientManager clientManager, 
                      IDownloadRegistry downloadRegistry,
                      IProgressReporter progressReporter)
    {
        this.client = client;
        this.clientManager = clientManager;
        this.downloadRegistry = downloadRegistry;
        this.progressReporter = progressReporter;
    }

    public async Task DownloadFile(FileCandidate candidate, string outputPath, SongJob song, Config config, CancellationToken? ct = null, CancellationTokenSource? searchCts = null)
    {
        string fileKey = candidate.Username + '\\' + candidate.Filename;

        if (downloadRegistry.DownloadedFiles.TryGetValue(fileKey, out var existingSong))
        {
            lock (downloadRegistry.DownloadedFiles)
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
                    downloadRegistry.DownloadedFiles.TryRemove(fileKey, out _);
                }
            }
        }

        await clientManager.WaitUntilReadyAsync(ct ?? CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string incompleteOutputPath = config.noIncompleteExt ? outputPath : outputPath + ".incomplete";

        Logger.Debug($"Downloading: {song} to '{incompleteOutputPath}'");

        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                if (downloadRegistry.Downloads.TryGetValue(candidate.Filename, out var x))
                    x.Transfer = state.Transfer;
                progressReporter.ReportDownloadStateChanged(song, GetStateLabel(state.Transfer.State));
            },
            progressUpdated: (progress) =>
            {
                if (downloadRegistry.Downloads.TryGetValue(candidate.Filename, out var x))
                    x.Song.BytesTransferred = progress.PreviousBytesTransferred;
                progressReporter.ReportDownloadProgress(song, progress.PreviousBytesTransferred, candidate.File.Size > 0 ? candidate.File.Size : 0);
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
            downloadRegistry.Downloads.TryAdd(candidate.Filename, activeDownload);

            progressReporter.ReportDownloadStart(song, candidate);

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await client.DownloadAsync(candidate.Username, candidate.Filename,
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
                    if (retryCount >= maxRetries || clientManager.IsConnectedAndLoggedIn)
                        throw;
                    await clientManager.WaitUntilReadyAsync(downloadCts.Token);
                }
            }
        }
        catch
        {
            if (File.Exists(incompleteOutputPath))
                try { Utils.DeleteFileAndParentsIfEmpty(incompleteOutputPath, config.parentDir); } catch { }
            downloadRegistry.Downloads.TryRemove(candidate.Filename, out _);
            throw;
        }

        try { searchCts?.Cancel(); } catch { }

        if (!config.noIncompleteExt)
        {
            try { Utils.Move(incompleteOutputPath, outputPath); }
            catch (IOException e) { Logger.Error($"Failed to rename .incomplete file. Error: {e}"); }
        }

        downloadRegistry.DownloadedFiles[fileKey] = song;
        downloadRegistry.Downloads.TryRemove(candidate.Filename, out _);

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
