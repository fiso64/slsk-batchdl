using Soulseek;

using Models;

using File = System.IO.File;
using Directory = System.IO.Directory;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;


public class Downloader
{
    private DownloaderApplication app;

    public Downloader(DownloaderApplication app)
    {
        this.app = app;
    }

    public async Task DownloadFile(SearchResponse response, Soulseek.File file, string outputPath, Track track, ProgressBar progress, TrackListEntry tle, Config config, CancellationToken? ct = null, CancellationTokenSource? searchCts = null)
    {
        if (app.downloadedFiles.TryGetValue(response.Username + '\\' + file.Filename, out var trackObj))
        {
            lock (app.trackLists)
            {
                var existingPath = trackObj.DownloadPath;
                var outputFileInfo = new FileInfo(outputPath);
                var existingFileInfo = new FileInfo(existingPath);

                if (existingFileInfo.Exists && existingFileInfo.Length == file.Size)
                {
                    Logger.Debug($"File \"{file.Filename}\" already downloaded at {existingPath}");

                    if (!outputFileInfo.Exists || outputFileInfo.Length != existingFileInfo.Length)
                    {
                        Logger.Debug($"Copying to new output path");
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                        File.Copy(existingPath, outputPath, true);
                    }

                    Printing.RefreshOrPrint(progress, 100, $"Succeeded (already downloaded): {Printing.DisplayString(track, file, response)}", true, true);

                    return;
                }
                else
                {
                    app.downloadedFiles.TryRemove(existingPath, out _);
                }
            }
        }

        await app.EnsureClientReadyAsync(config);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        string incompleteOutputPath = config.noIncompleteExt ? outputPath : outputPath + ".incomplete";

        Logger.Debug($"Downloading: {track} to '{incompleteOutputPath}'");

        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                if (app.downloads.TryGetValue(file.Filename, out var x))
                    x.transfer = state.Transfer;
            },
            progressUpdated: (progress) =>
            {
                if (app.downloads.TryGetValue(file.Filename, out var x))
                    x.bytesTransferred = progress.PreviousBytesTransferred;
            }
        );

        try
        {
            using var downloadCts = ct != null ?
                CancellationTokenSource.CreateLinkedTokenSource((CancellationToken)ct) :
                new CancellationTokenSource();

            using var outputStream = new FileStream(incompleteOutputPath, FileMode.Create);
            var wrapper = new DownloadWrapper(outputPath, response, file, track, downloadCts, progress, tle);
            app.downloads.TryAdd(file.Filename, wrapper);

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await app.Client.DownloadAsync(response.Username, file.Filename,
                        () => Task.FromResult((Stream)outputStream),
                        file.Size == -1 ? null : file.Size, startOffset: outputStream.Position,
                        options: transferOptions, cancellationToken: downloadCts.Token);

                    break;
                }
                catch (SoulseekClientException e)
                {
                    retryCount++;

                    Logger.DebugError($"Error while downloading: {e}");

                    if (retryCount >= maxRetries || app.IsConnectedAndLoggedIn)
                        throw;

                    await app.EnsureClientReadyAsync(config);
                }
            }
        }
        catch
        {
            if (File.Exists(incompleteOutputPath))
                try { Utils.DeleteFileAndParentsIfEmpty(incompleteOutputPath, config.parentDir); } catch { }
            app.downloads.TryRemove(file.Filename, out var d);
            if (d != null)
                lock (d) { d.UpdateText(); }
            throw;
        }

        try { searchCts?.Cancel(); } catch { }

        if (!config.noIncompleteExt)
        {
            try
            {
                Utils.Move(incompleteOutputPath, outputPath);
                app.downloadedFiles[response.Username + '\\' + file.Filename] = track;
            }
            catch (IOException e) { Logger.Error($"Failed to rename .incomplete file. Error: {e}"); }
        }
        else
        {
            app.downloadedFiles[response.Username + '\\' + file.Filename] = track;
        }

        app.downloads.TryRemove(file.Filename, out var x);

        if (x != null)
        {
            lock (x)
            {
                x.success = true;
                x.UpdateText();
            }
        }
    }
}
