using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Core.Services;

internal sealed record InitialDownloadTarget(string Path, bool PublishToDuplicateCache);

internal sealed record OutputFinalizationResult(JobOutcome Outcome, FileOrganizationException? OrganizationException)
{
    public static OutputFinalizationResult Completed(JobOutcome outcome)
        => new(outcome, null);

    public static OutputFinalizationResult Failed(FileOrganizationException exception, string? retainedPath = null)
        => new(
            JobOutcome.Failed(
                JobFailureReason.Other,
                retainedPath == null
                    ? exception.Message
                    : $"{exception.Message}{Environment.NewLine}Downloaded payload retained at: {retainedPath}",
                Diagnostics.ExceptionText.Detail(exception)),
            exception);
}

// Owns the post-download boundary where a temporary/staged path becomes the
// user-visible final path. A job should not commit success until this layer has
// either published the final duplicate-cache entry or returned a failure outcome.
internal sealed class OutputFinalizer
{
    private readonly DownloadedFileCache downloadedFiles;
    private readonly DownloadEvents? events;
    private readonly ILogger<OutputFinalizer> logger;

    public OutputFinalizer(
        DownloadedFileCache downloadedFiles,
        DownloadEvents? events = null,
        ILogger<OutputFinalizer>? logger = null)
    {
        this.downloadedFiles = downloadedFiles;
        this.events = events;
        this.logger = logger ?? NullLogger<OutputFinalizer>.Instance;
    }

    public InitialDownloadTarget GetInitialDownloadTarget(
        DownloadSettings config,
        SongJob song,
        FileManager organizer,
        FileCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(config.Output.NameFormat))
            return new(organizer.GetSavePath(candidate.Filename), PublishToDuplicateCache: true);

        var sourceFileName = Utils.GetFileNameSlsk(candidate.Filename).CleanPath(config.Output.InvalidReplaceStr);
        var stagingPath = Path.Join(OutputStaging.Root(config.Output), song.Id.ToString("N"), sourceFileName);

        return new(stagingPath, PublishToDuplicateCache: false);
    }

    public InitialDownloadTarget GetInitialDownloadTarget(
        DownloadSettings config,
        SongJob song,
        FileManager organizer,
        PeerFileTarget target)
    {
        if (string.IsNullOrWhiteSpace(config.Output.NameFormat))
            return new(organizer.GetSavePath(target.Filename), PublishToDuplicateCache: true);

        var sourceFileName = Utils.GetFileNameSlsk(target.Filename).CleanPath(config.Output.InvalidReplaceStr);
        var stagingPath = Path.Join(OutputStaging.Root(config.Output), song.Id.ToString("N"), sourceFileName);
        return new(stagingPath, PublishToDuplicateCache: false);
    }

    public OutputFinalizationResult FinalizeSongPlacement(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        FileManager organizer,
        bool finalizePlacement)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded || !finalizePlacement)
            return OutputFinalizationResult.Completed(outcome);

        return downloadedFiles.WithExclusiveAccess(() =>
        {
            song.UpdateActivity(JobActivityPhase.Organizing);
            try
            {
                organizer.OrganizeDownloadedFile(song);
                EnsureFileLeftStaging(song, parentJob.Config.Output);
                PublishDownloadedFileCache(song, outcome);
                return OutputFinalizationResult.Completed(outcome);
            }
            catch (Exception ex)
            {
                var organizationException = AsOrganizationException(ex, song.DownloadPath, targetPath: null);
                LogOrganizationFailure(song, organizationException);
                return OutputFinalizationResult.Failed(
                    organizationException,
                    RetainedStagedPayload(song.DownloadPath, parentJob.Config.Output));
            }
        });
    }

    public OutputFinalizationResult FinalizeAlbumPlacement(
        AlbumJob album,
        FileManager organizer,
        List<SongJob>? chosenFiles,
        List<SongJob>? additionalImages,
        JobOutcome outcome)
    {
        var filesToOrganize = (chosenFiles ?? [])
            .Concat(additionalImages ?? [])
            .Distinct()
            .ToList();
        if (filesToOrganize.Count == 0)
        {
            PublishDownloadedFileCache(chosenFiles);
            PublishDownloadedFileCache(additionalImages);
            return OutputFinalizationResult.Completed(outcome);
        }

        return downloadedFiles.WithExclusiveAccess(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(album.DownloadPath))
                    organizer.OrganizeAlbum(album, filesToOrganize, additionalImages);
                EnsureAlbumFilesLeftStaging(album, chosenFiles, additionalImages, outcome);
                RefreshSuccessfulAlbumPath(album, filesToOrganize, outcome);
                PublishDownloadedFileCache(chosenFiles);
                PublishDownloadedFileCache(additionalImages);
                return OutputFinalizationResult.Completed(outcome);
            }
            catch (Exception ex)
            {
                var strandedPath = SuccessfulFiles(chosenFiles, additionalImages)
                    .Select(file => file.DownloadPath)
                    .FirstOrDefault(path => OutputStaging.Contains(path, album.Config.Output));
                var organizationException = AsOrganizationException(ex, strandedPath, album.DownloadPath);
                LogOrganizationFailure(album, organizationException);
                return OutputFinalizationResult.Failed(
                    organizationException,
                    RetainedStagedPayload(strandedPath, album.Config.Output));
            }
        });
    }

    private void LogOrganizationFailure(Job job, FileOrganizationException exception)
    {
        DownloadLogMessages.ComponentFailed(
            logger,
            exception,
            "output-organization",
            job.Id);
        events?.RaiseJobMessage(
            job,
            LogLevel.Error,
            null,
            "download completed, but organizing the output failed");
    }

    public void PublishDownloadedFileCache(SongJob song)
    {
        if (song.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        PublishDownloadedFileCache(song, JobOutcome.Done(song.DownloadPath, song.ResolvedTarget, song.DownloadSource));
    }

    public void PublishDownloadedFileCache(SongJob song, JobOutcome outcome)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        var target = song.ResolvedPeerTarget;
        if (target == null || string.IsNullOrEmpty(song.DownloadPath))
            return;
        if (song.Config != null && OutputStaging.Contains(song.DownloadPath, song.Config.Output))
            return;

        downloadedFiles.Publish(song.DownloadPath, target);
    }

    public void PublishDownloadedFileCache(IEnumerable<SongJob>? songs)
    {
        if (songs == null)
            return;

        foreach (var song in songs)
            PublishDownloadedFileCache(song);
    }

    private static void EnsureFileLeftStaging(SongJob song, OutputSettings output)
    {
        if (!OutputStaging.Contains(song.DownloadPath, output))
            return;

        throw new FileOrganizationException(
            $"Finalization left the downloaded file in Sockseek staging: '{song.DownloadPath}'.",
            song.DownloadPath!,
            "",
            new InvalidOperationException("A leaf that owns final placement cannot commit from staging."));
    }

    private static void EnsureAlbumFilesLeftStaging(
        AlbumJob album,
        IEnumerable<SongJob>? chosenFiles,
        IEnumerable<SongJob>? additionalImages,
        JobOutcome outcome)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        var stranded = SuccessfulFiles(chosenFiles, additionalImages)
            .FirstOrDefault(file => OutputStaging.Contains(file.DownloadPath, album.Config.Output));
        if (stranded == null)
            return;

        throw new FileOrganizationException(
            $"Album finalization left a downloaded file in Sockseek staging: '{stranded.DownloadPath}'.",
            stranded.DownloadPath!,
            album.DownloadPath ?? "",
            new InvalidOperationException("A successful album cannot retain a successfully downloaded child in staging."));
    }

    private static IEnumerable<SongJob> SuccessfulFiles(
        IEnumerable<SongJob>? chosenFiles,
        IEnumerable<SongJob>? additionalImages)
        => (chosenFiles ?? [])
            .Concat(additionalImages ?? [])
            .Where(file => file.TerminalOutcome == JobTerminalOutcome.Succeeded)
            .Distinct();

    private static void RefreshSuccessfulAlbumPath(
        AlbumJob album,
        IEnumerable<SongJob> files,
        JobOutcome outcome)
    {
        if (outcome.TerminalOutcome != JobTerminalOutcome.Succeeded)
            return;

        var finalizedPaths = files
            .Where(file => file.TerminalOutcome == JobTerminalOutcome.Succeeded)
            .Select(file => file.DownloadPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !OutputStaging.Contains(path, album.Config.Output))
            .Cast<string>()
            .ToList();
        if (finalizedPaths.Count > 0)
            album.DownloadPath = Utils.GreatestCommonDirectory(finalizedPaths);
    }

    private static FileOrganizationException AsOrganizationException(
        Exception exception,
        string? sourcePath,
        string? targetPath)
        => exception as FileOrganizationException
            ?? new FileOrganizationException(
                "Failed to finalize downloaded file placement.",
                sourcePath ?? "",
                targetPath ?? "",
                exception);

    private static string? RetainedStagedPayload(string? path, OutputSettings output)
        => OutputStaging.Contains(path, output) && File.Exists(path)
            ? path
            : null;
}
