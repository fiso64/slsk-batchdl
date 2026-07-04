using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;

namespace Sockseek.Core.Services;


public sealed record FileDownloadResult(string OutputPath, FileCandidate Candidate);

public enum FileDownloadStatus
{
    Completed,
    ManuallySkipped,
}

public sealed record FileDownloadOutcome(FileDownloadStatus Status, FileDownloadResult? Result, FileCandidate Candidate)
{
    public static FileDownloadOutcome Completed(FileDownloadResult result)
        => new(FileDownloadStatus.Completed, result, result.Candidate);

    public static FileDownloadOutcome ManuallySkipped(FileCandidate candidate)
        => new(FileDownloadStatus.ManuallySkipped, null, candidate);
}

public class Downloader
{
    private readonly ISoulseekClient client;
    private readonly SoulseekClientManager clientManager;
    private readonly ActiveDownloadTracker activeDownloads;
    private readonly DownloadedFileCache downloadedFiles;
    private readonly DownloadEvents events;
    private readonly StaleDownloadCoordinator staleDownloads;

    internal Downloader(ISoulseekClient client,
                        SoulseekClientManager clientManager,
                        ActiveDownloadTracker activeDownloads,
                        DownloadedFileCache downloadedFiles,
                        DownloadEvents events,
                        StaleDownloadCoordinator staleDownloads)
    {
        this.client = client;
        this.clientManager = clientManager;
        this.activeDownloads = activeDownloads;
        this.downloadedFiles = downloadedFiles;
        this.events = events;
        this.staleDownloads = staleDownloads;
    }

    public async Task<FileDownloadOutcome> DownloadFile(
        FileCandidate candidate,
        string outputPath,
        SongJob song,
        TransferSettings transfer,
        string? parentDir,
        CancellationToken? ct = null,
        bool publishToDuplicateCache = true,
        Job? parentJob = null)
    {
        if (downloadedFiles.TryGetReusable(candidate, out var existingDownload))
        {
            var existingPath     = existingDownload.OutputPath;
            var outputFileInfo   = new FileInfo(outputPath);
            var existingFileInfo = new FileInfo(existingPath);

            SockseekLog.Jobs.Debug($"File \"{candidate.Filename}\" already downloaded at {existingPath}");

            if (!outputFileInfo.Exists || outputFileInfo.Length != existingFileInfo.Length)
            {
                SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: copying existing download from '{existingPath}' to '{outputPath}'");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.Copy(existingPath!, outputPath, true);
            }

            return FileDownloadOutcome.Completed(new FileDownloadResult(outputPath, existingDownload.Candidate));
        }

        await clientManager.WaitUntilReadyAsync(ct ?? CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string incompleteOutputPath = transfer.NoIncompleteExt ? outputPath : outputPath + ".incomplete";

        SockseekLog.Soulseek.Debug($"Downloading: {song} from '{candidate.Username}\\{candidate.Filename}' to '{incompleteOutputPath}'");

        StaleDownloadCoordinator.PeerTransferActivity? staleActivity = null;
        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                staleActivity?.ReportState(state.Transfer);
                events.RaiseDownloadStateChanged(song, state.Transfer.State);
            },
            progressUpdated: (progress) =>
            {
                staleActivity?.ReportProgress(progress.Transfer);
                if (activeDownloads.TryGet(candidate.Filename, out var x))
                    x.Song.BytesTransferred = progress.PreviousBytesTransferred;
                events.RaiseDownloadProgress(song, progress.PreviousBytesTransferred, candidate.File.Size > 0 ? candidate.File.Size : 0);
            }
        );

        ActiveDownload? activeDownload = null;
        void DeleteIncompleteDownloadAfterFailure()
        {
            if (!File.Exists(incompleteOutputPath))
                return;

            try
            {
                Utils.DeleteFileAndParentsIfEmpty(incompleteOutputPath, CleanupRootForIncompletePath(incompleteOutputPath, parentDir));
                SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: deleted incomplete download '{incompleteOutputPath}' after failure");
            }
            catch (Exception ex)
            {
                SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: failed to delete incomplete download '{incompleteOutputPath}' after failure: {ex.Message}");
            }
        }

        try
        {
            using var downloadCts = ct != null
                ? CancellationTokenSource.CreateLinkedTokenSource((CancellationToken)ct)
                : new CancellationTokenSource();

            using var outputStream = new FileStream(incompleteOutputPath, FileMode.Create);

            song.FileSize = candidate.File.Size;
            activeDownload = new ActiveDownload(song, candidate, downloadCts, parentJob);
            activeDownloads.TryAdd(activeDownload);

            events.RaiseDownloadStarted(song, candidate);

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await staleDownloads.WatchPeerTransferAsync(
                        activeDownload,
                        song.Config?.Search.MaxStaleTime ?? 30_000,
                        async activity =>
                        {
                            staleActivity = activity;
                            try
                            {
                                return await client.DownloadAsync(candidate.Username, candidate.Filename,
                                    () => Task.FromResult((Stream)outputStream),
                                    candidate.File.Size == -1 ? null : candidate.File.Size,
                                    startOffset: outputStream.Position,
                                    options: transferOptions,
                                    cancellationToken: downloadCts.Token);
                            }
                            finally
                            {
                                staleActivity = null;
                            }
                        });
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    retryCount++;
                    bool canRetry = e is SoulseekClientException
                        && retryCount < maxRetries
                        && !clientManager.IsConnectedAndLoggedIn;
                    int reportedMaxRetries = canRetry || (e is SoulseekClientException && !clientManager.IsConnectedAndLoggedIn)
                        ? maxRetries
                        : retryCount;

                    SockseekLog.Soulseek.Debug(
                        $"Error while downloading '{candidate.Username}\\{candidate.Filename}' to '{incompleteOutputPath}' " +
                        $"(attempt {retryCount}/{maxRetries}): {SockseekLog.ExceptionSummary(e)}");
                    events.RaiseDownloadAttemptFailed(song, candidate, incompleteOutputPath, retryCount, reportedMaxRetries, e);

                    if (!canRetry)
                        throw;

                    await clientManager.WaitUntilReadyAsync(downloadCts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (activeDownload?.IsStaleCancelled == true)
        {
            DeleteIncompleteDownloadAfterFailure();

            var staleException = new StaleDownloadException(
                candidate,
                activeDownload.StaleMaxStaleTimeMs ?? song.Config?.Search.MaxStaleTime ?? 30_000);
            if (parentJob is not AlbumJob || song.IsNotAudio)
                events.RaiseDownloadAttemptFailed(song, candidate, incompleteOutputPath, 1, 1, staleException);
            throw staleException;
        }
        catch
        {
            DeleteIncompleteDownloadAfterFailure();
            
            if (activeDownloads.TryRemove(candidate.Filename, out var ad) && ad.IsManuallySkipped)
                return FileDownloadOutcome.ManuallySkipped(candidate);

            throw;
        }


        if (!transfer.NoIncompleteExt)
        {
            try
            {
                Utils.Move(incompleteOutputPath, outputPath);
            }
            catch (Exception ex)
            {
                activeDownloads.TryRemove(candidate.Filename, out _);
                try
                {
                    if (File.Exists(incompleteOutputPath))
                        File.Delete(incompleteOutputPath);
                }
                catch (Exception cleanupEx)
                {
                    SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: failed to delete incomplete download '{incompleteOutputPath}' after final rename failure: {cleanupEx.Message}");
                }

                throw new IOException(
                    $"Failed to rename incomplete file from '{incompleteOutputPath}' to '{outputPath}'.",
                    ex);
            }
        }

        var result = new FileDownloadResult(outputPath, candidate);
        if (publishToDuplicateCache)
            downloadedFiles.Publish(result);
        activeDownloads.TryRemove(candidate.Filename, out _);

        if (candidate.File.Size > 0)
            song.BytesTransferred = candidate.File.Size;

        return FileDownloadOutcome.Completed(result);
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

    private static string CleanupRootForIncompletePath(string path, string? parentDir)
    {
        if (string.IsNullOrWhiteSpace(parentDir))
            return "";

        var stagingRoot = Path.Join(parentDir, ".sockseek-staging");
        return Utils.IsInDirectory(path, stagingRoot, strict: true)
            ? stagingRoot
            : parentDir;
    }
}
