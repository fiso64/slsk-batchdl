using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers;
using Sockseek.Core.Transfers.Downloads.State;
using Sockseek.Core.Events;

namespace Sockseek.Core.Services;


public sealed record FileDownloadResult(
    string OutputPath,
    FileCandidate Candidate,
    Guid? TransferId = null,
    int AttemptCount = 0);

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
        var transferId = TransferIds.New();
        int attemptCount = 0;
        Guid currentAttemptId = Guid.Empty;
        long totalBytes = candidate.Size > 0 ? candidate.Size : 0;

        SockseekLog.Soulseek.Debug($"Downloading: {song} from '{candidate.Username}\\{candidate.Filename}' to '{incompleteOutputPath}'");

        StaleDownloadCoordinator.PeerTransferActivity? staleActivity = null;
        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                staleActivity?.ReportState(state.Transfer);
                events.RaiseDownloadStateChanged(
                    transferId,
                    song,
                    candidate,
                    outputPath,
                    state.Transfer.State,
                    state.Transfer.BytesTransferred,
                    candidate.Size > 0 ? candidate.Size : 0);
            },
            progressUpdated: (progress) =>
            {
                staleActivity?.ReportProgress(progress.Transfer);
                song.BytesTransferred = progress.PreviousBytesTransferred;
                events.RaiseDownloadProgress(
                    transferId,
                    song,
                    candidate,
                    outputPath,
                    progress.PreviousBytesTransferred,
                    candidate.Size > 0 ? candidate.Size : 0);
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

            song.FileSize = candidate.Size;
            activeDownload = new ActiveDownload(transferId, song, candidate, outputPath, downloadCts, parentJob);
            activeDownloads.TryAdd(activeDownload);

            events.RaiseDownloadStarted(transferId, song, candidate, outputPath);

            int maxRetries = 3;
            while (true)
            {
                try
                {
                    attemptCount++;
                    currentAttemptId = TransferAttemptIds.New();
                    events.RaiseTransferAttemptStarted(
                        transferId,
                        currentAttemptId,
                        attemptCount,
                        song,
                        candidate,
                        outputPath,
                        incompleteOutputPath);
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
                                    candidate.Size == -1 ? null : candidate.Size,
                                    startOffset: outputStream.Position,
                                    options: transferOptions,
                                    cancellationToken: downloadCts.Token);
                            }
                            finally
                            {
                                staleActivity = null;
                            }
                        });
                    events.RaiseTransferAttemptCompleted(
                        transferId,
                        currentAttemptId,
                        attemptCount,
                        song,
                        candidate,
                        outputPath);
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    bool canRetry = e is SoulseekClientException
                        && !clientManager.IsConnectedAndLoggedIn;
                    int reportedMaxRetries = canRetry
                        ? Math.Max(maxRetries, attemptCount + 1)
                        : attemptCount;

                    SockseekLog.Soulseek.Debug(
                        $"Error while downloading '{candidate.Username}\\{candidate.Filename}' to '{incompleteOutputPath}' " +
                        $"(attempt {attemptCount}/{maxRetries}): {SockseekLog.ExceptionSummary(e)}");
                    events.RaiseTransferAttemptFailed(
                        transferId,
                        currentAttemptId,
                        attemptCount,
                        song,
                        candidate,
                        outputPath,
                        e);
                    events.RaiseDownloadAttemptFailed(transferId, song, candidate, outputPath, incompleteOutputPath, attemptCount, reportedMaxRetries, e);

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
                events.RaiseDownloadAttemptFailed(transferId, song, candidate, outputPath, incompleteOutputPath, Math.Max(attemptCount, 1), Math.Max(attemptCount, 1), staleException);
            events.RaiseTransferAttemptCancelled(
                transferId,
                currentAttemptId,
                Math.Max(attemptCount, 1),
                song,
                candidate,
                outputPath,
                TransferCancellationReason.Stale);
            events.RaiseTransferFailed(
                transferId,
                song,
                candidate,
                outputPath,
                song.BytesTransferred,
                totalBytes,
                Math.Max(attemptCount, 1),
                TransferFailureReason.Stale,
                staleException);
            throw staleException;
        }
        catch (Exception ex)
        {
            DeleteIncompleteDownloadAfterFailure();

            if (activeDownloads.TryRemove(transferId, out var ad) && ad.IsManuallySkipped)
            {
                events.RaiseTransferAttemptCancelled(
                    transferId,
                    currentAttemptId,
                    Math.Max(attemptCount, 1),
                    song,
                    candidate,
                    outputPath,
                    TransferCancellationReason.ManualSkip);
                events.RaiseTransferCancelled(
                    transferId,
                    song,
                    candidate,
                    outputPath,
                    song.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferCancellationReason.ManualSkip);
                return FileDownloadOutcome.ManuallySkipped(candidate);
            }

            if (ex is OperationCanceledException)
            {
                events.RaiseTransferAttemptCancelled(
                    transferId,
                    currentAttemptId,
                    Math.Max(attemptCount, 1),
                    song,
                    candidate,
                    outputPath,
                    TransferCancellationReason.Requested);
                events.RaiseTransferCancelled(
                    transferId,
                    song,
                    candidate,
                    outputPath,
                    song.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferCancellationReason.Requested);
            }
            else
            {
                events.RaiseTransferFailed(
                    transferId,
                    song,
                    candidate,
                    outputPath,
                    song.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferFailureReason.PeerFailure,
                    ex);
            }

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
                activeDownloads.TryRemove(transferId, out _);
                try
                {
                    if (File.Exists(incompleteOutputPath))
                        File.Delete(incompleteOutputPath);
                }
                catch (Exception cleanupEx)
                {
                    SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: failed to delete incomplete download '{incompleteOutputPath}' after final rename failure: {cleanupEx.Message}");
                }

                var finalizationException = new IOException(
                    $"Failed to rename incomplete file from '{incompleteOutputPath}' to '{outputPath}'.",
                    ex);
                events.RaiseTransferFailed(
                    transferId,
                    song,
                    candidate,
                    outputPath,
                    song.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferFailureReason.Finalization,
                    finalizationException);
                throw finalizationException;
            }
        }

        var result = new FileDownloadResult(outputPath, candidate, transferId, Math.Max(attemptCount, 1));
        if (publishToDuplicateCache)
            downloadedFiles.Publish(result);
        activeDownloads.TryRemove(transferId, out _);

        if (candidate.Size > 0)
            song.BytesTransferred = candidate.Size;

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
