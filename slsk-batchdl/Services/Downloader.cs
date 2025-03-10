using Soulseek;

using Models;
using static Program;

using File = System.IO.File;
using Directory = System.IO.Directory;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using System.Diagnostics;


public class Downloader
{
    private ISoulseekClient client;

    public Downloader(ISoulseekClient client)
    {
        this.client = client;
    }

    public async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, Track track, ProgressBar progress, TrackListEntry tle, Config config, CancellationToken? ct = null, CancellationTokenSource? searchCts = null)
    {
        await Program.WaitForLogin(config);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        string origPath = filePath;
        filePath += ".incomplete";

        Logger.Debug($"Downloading: {track} to '{filePath}'");

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

            using var outputStream = new FileStream(filePath, FileMode.Create);
            var wrapper = new DownloadWrapper(origPath, response, file, track, downloadCts, progress, tle);
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
            if (File.Exists(filePath))
                try { Utils.DeleteFileAndParentsIfEmpty(filePath, config.parentDir); } catch { }
            downloads.TryRemove(file.Filename, out var d);
            if (d != null)
                lock (d) { d.UpdateText(); }
            throw;
        }

        try { searchCts?.Cancel(); } catch { }

        try { Utils.Move(filePath, origPath); }
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
