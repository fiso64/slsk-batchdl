using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Core.Services;
using Microsoft.Extensions.Logging;

using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Sockseek.Core;

internal sealed class IncompleteAlbumActionExecutor
{
    private readonly DownloadExecutionContext context;
    private readonly ILogger<IncompleteAlbumActionExecutor> logger;

    public IncompleteAlbumActionExecutor(DownloadExecutionContext context)
    {
        this.context = context;
        logger = context.LoggerFactory.CreateLogger<IncompleteAlbumActionExecutor>();
    }

    public void HandleIncompleteAlbum(AlbumJob job, AlbumFolder folder, ResolvedIncompleteAlbumAction action, DownloadSettings config)
    {
        if (action.Kind == IncompleteAlbumActionKind.Keep)
            return;

        var failedAlbumPath = action.Path;
        var outputParentDir = config.Output.ParentDir;
        var filesToHandle = job.EnsureTrackJobs(folder)
            .Where(song => IsIncompleteAlbumActionFile(song, config.Output))
            .ToList();

        if (filesToHandle.Count == 0)
        {
            DownloadLogMessages.JobDecision(
                logger,
                job.Id,
                "incomplete-album-action-not-needed",
                0);
            return;
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
        {
            context.Events.RaiseJobStatus(job, "deleting files");
        }
        else if (action.Kind == IncompleteAlbumActionKind.Move)
        {
            if (string.IsNullOrEmpty(outputParentDir))
                throw new InvalidOperationException("Cannot move incomplete album files because Output.ParentDir is not set.");
            if (string.IsNullOrEmpty(failedAlbumPath))
                throw new InvalidOperationException("Cannot move incomplete album files because incomplete album action path is not set.");

            context.Events.RaiseJobStatus(job, $"moving to {failedAlbumPath}");
        }

        foreach (var af in filesToHandle)
        {
            var downloadPath = af.DownloadPath!;
            try
            {
                if (action.Kind == IncompleteAlbumActionKind.Delete)
                {
                    File.Delete(downloadPath);
                }
                else if (action.Kind == IncompleteAlbumActionKind.Move)
                {
                    var relativeBase = outputParentDir
                        ?? throw new InvalidOperationException("Cannot move incomplete album files because Output.ParentDir is not set.");
                    var targetBase = failedAlbumPath
                        ?? throw new InvalidOperationException("Cannot move incomplete album files because incomplete album action path is not set.");
                    var newPath = Path.Join(targetBase, Path.GetRelativePath(relativeBase, downloadPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Utils.Move(downloadPath, newPath);
                }

                var downloadParent = Path.GetDirectoryName(downloadPath);
                if (!string.IsNullOrEmpty(downloadParent) && !string.IsNullOrEmpty(outputParentDir))
                    Utils.DeleteAncestorsIfEmpty(downloadParent, outputParentDir);
            }
            catch (Exception e)
            {
                DownloadLogMessages.ComponentFailed(
                    logger,
                    e,
                    "incomplete-album-action",
                    job.Id);
                context.Events.RaiseJobMessage(
                    job,
                    LogLevel.Error,
                    null,
                    "unable to move or delete a completed file after the album failed");
            }
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
            context.Events.RaiseJobStatus(job, "deleted files");
        else if (action.Kind == IncompleteAlbumActionKind.Move)
            context.Events.RaiseJobStatus(job, $"moved to {failedAlbumPath}");
    }

    static bool IsIncompleteAlbumActionFile(SongJob song, OutputSettings output)
        => song.TerminalOutcome == JobTerminalOutcome.Succeeded
            && !string.IsNullOrEmpty(song.DownloadPath)
            && !OutputStaging.Contains(song.DownloadPath, output)
            && !song.DownloadPath.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)
            && File.Exists(song.DownloadPath);
}
