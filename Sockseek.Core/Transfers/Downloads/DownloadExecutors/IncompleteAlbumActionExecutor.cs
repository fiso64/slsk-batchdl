using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Sockseek.Core;

internal sealed class IncompleteAlbumActionExecutor
{
    private readonly DownloadExecutionContext context;

    public IncompleteAlbumActionExecutor(DownloadExecutionContext context)
    {
        this.context = context;
    }

    public void HandleIncompleteAlbum(AlbumJob job, AlbumFolder folder, ResolvedIncompleteAlbumAction action, DownloadSettings config)
    {
        if (action.Kind == IncompleteAlbumActionKind.Keep)
            return;

        var failedAlbumPath = action.Path;
        var outputParentDir = config.Output.ParentDir;
        var filesToHandle = job.EnsureTrackJobs(folder)
            .Where(IsIncompleteAlbumActionFile)
            .ToList();

        if (filesToHandle.Count == 0)
        {
            SockseekLog.Jobs.Debug($"[{job.DisplayId}] AlbumJob: skipping failed-album action; no completed files were downloaded for failed folder {folder.FolderPath}");
            return;
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
        {
            context.Events.RaiseJobStatus(job, "deleting files");
            SockseekLog.Jobs.Info(job, "Deleting album files");
        }
        else if (action.Kind == IncompleteAlbumActionKind.Move)
        {
            if (string.IsNullOrEmpty(outputParentDir))
                throw new InvalidOperationException("Cannot move incomplete album files because Output.ParentDir is not set.");
            if (string.IsNullOrEmpty(failedAlbumPath))
                throw new InvalidOperationException("Cannot move incomplete album files because incomplete album action path is not set.");

            context.Events.RaiseJobStatus(job, $"moving to {failedAlbumPath}");
            SockseekLog.Jobs.Info(job, $"Moving album files to {failedAlbumPath}");
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
                SockseekLog.Jobs.Error(job, $"Unable to move or delete file '{downloadPath}' after album fail: {e}");
            }
        }

        if (action.Kind == IncompleteAlbumActionKind.Delete)
            context.Events.RaiseJobStatus(job, "deleted files");
        else if (action.Kind == IncompleteAlbumActionKind.Move)
            context.Events.RaiseJobStatus(job, $"moved to {failedAlbumPath}");
    }

    static bool IsIncompleteAlbumActionFile(SongJob song)
        => song.TerminalOutcome == JobTerminalOutcome.Succeeded
            && !string.IsNullOrEmpty(song.DownloadPath)
            && !song.DownloadPath.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)
            && File.Exists(song.DownloadPath);
}