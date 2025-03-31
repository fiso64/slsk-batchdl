using Soulseek;

using Models;
using static Program;

using File = System.IO.File;
using Directory = System.IO.Directory;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;


public class Downloader
{
    private ISoulseekClient client;

    public Downloader(ISoulseekClient client)
    {
        this.client = client;
    }

    public async Task DownloadFile(SearchResponse response, Soulseek.File file, string outputPath, Track track, ProgressBar progress, TrackListEntry tle, Config config, CancellationToken? ct = null, CancellationTokenSource? searchCts = null)
    {
        if (downloadedFiles.TryGetValue(response.Username + '\\' + file.Filename, out var trackObj))
        {
            lock (trackLists)
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
                    downloadedFiles.TryRemove(existingPath, out _);
                }
            }
        }

        await Program.WaitForLogin(config);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        string incompleteOutputPath = outputPath + ".incomplete";

        Logger.Debug($"Downloading: {track} to '{incompleteOutputPath}'");

        var transferOptions = new TransferOptions(
            stateChanged: (state) =>
            {
                if (Program.downloads.TryGetValue(file.Filename, out var x))
                    x.transfer = state.Transfer;
            },
            progressUpdated: (progress) =>
            {
                if (downloads.TryGetValue(file.Filename, out var x))
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
            downloads.TryAdd(file.Filename, wrapper);

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await client.DownloadAsync(response.Username, file.Filename,
                        () => Task.FromResult((Stream)outputStream),
                        file.Size == -1 ? null : file.Size, startOffset: outputStream.Position,
                        options: transferOptions, cancellationToken: downloadCts.Token);

                    break;
                }
                catch (SoulseekClientException e)
                {
                    retryCount++;

                    Logger.DebugError($"Error while downloading: {e}");

                    if (retryCount >= maxRetries || IsConnectedAndLoggedIn())
                        throw;

                    await WaitForLogin(config);
                }
            }
        }
        catch
        {
            if (File.Exists(incompleteOutputPath))
                try { Utils.DeleteFileAndParentsIfEmpty(incompleteOutputPath, config.parentDir); } catch { }
            downloads.TryRemove(file.Filename, out var d);
            if (d != null)
                lock (d) { d.UpdateText(); }
            throw;
        }

        try { searchCts?.Cancel(); } catch { }

        try 
        { 
            Utils.Move(incompleteOutputPath, outputPath); 
            downloadedFiles[response.Username + '\\' + file.Filename] = track;
        }
        catch (IOException) { Logger.Error($"Failed to rename .incomplete file"); }

        downloads.TryRemove(file.Filename, out var x);

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
