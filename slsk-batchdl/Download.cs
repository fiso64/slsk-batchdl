using Soulseek;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Data;
using Enums;
using static Program;

using File = System.IO.File;
using Directory = System.IO.Directory;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;


static class Download
{
    public static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, Track track, ProgressBar progress, CancellationTokenSource? searchCts = null)
    {
        if (Config.DoNotDownload)
            throw new Exception();

        await Program.WaitForLogin();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        string origPath = filePath;
        filePath += ".incomplete";

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
            using var cts = new CancellationTokenSource();
            using var outputStream = new FileStream(filePath, FileMode.Create);
            var wrapper = new DownloadWrapper(origPath, response, file, track, cts, progress);
            downloads.TryAdd(file.Filename, wrapper);

            // Attempt to make it resume downloads after a network interruption.
            // Does not work: The resumed download will be queued until it goes stale.
            // The host (slskd) reports that "Another upload to {user} is already in progress"
            // when attempting to resume. Must wait until timeout, which can take minutes.

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await client.DownloadAsync(response.Username, file.Filename,
                        () => Task.FromResult((Stream)outputStream),
                        file.Size, startOffset: outputStream.Position,
                        options: transferOptions, cancellationToken: cts.Token);

                    break;
                }
                catch (SoulseekClientException)
                {
                    retryCount++;

                    if (retryCount >= maxRetries || IsConnectedAndLoggedIn())
                        throw;

                    await WaitForLogin();
                }
            }
        }
        catch
        {
            if (File.Exists(filePath))
                try { File.Delete(filePath); } catch { }
            downloads.TryRemove(file.Filename, out var d);
            if (d != null)
                lock (d) { d.UpdateText(); }
            throw;
        }

        try { searchCts?.Cancel(); }
        catch { }

        try { Utils.Move(filePath, origPath); }
        catch (IOException) { Printing.WriteLine($"Failed to rename .incomplete file", ConsoleColor.DarkYellow, true); }

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

public class DownloadWrapper
{
    public string savePath;
    public string displayText = "";
    public int downloadRotatingBarState = 0;
    public Soulseek.File file;
    public Transfer? transfer;
    public SearchResponse response;
    public ProgressBar progress;
    public Track track;
    public long bytesTransferred = 0;
    public bool stalled = false;
    public bool queued = false;
    public bool success = false;
    public CancellationTokenSource cts;
    public DateTime startTime = DateTime.Now;
    public DateTime lastChangeTime = DateTime.Now;

    TransferStates? prevTransferState = null;
    long prevBytesTransferred = 0;
    bool updatedTextDownload = false;
    bool updatedTextSuccess = false;
    readonly char[] bars = { '|', '/', '—', '\\' };

    public DownloadWrapper(string savePath, SearchResponse response, Soulseek.File file, Track track, CancellationTokenSource cts, ProgressBar progress)
    {
        this.savePath = savePath;
        this.response = response;
        this.file = file;
        this.cts = cts;
        this.track = track;
        this.progress = progress;
        this.displayText = Printing.DisplayString(track, file, response);

        Printing.RefreshOrPrint(progress, 0, "Initialize: " + displayText, true);
        Printing.RefreshOrPrint(progress, 0, displayText, false);
    }

    public void UpdateText()
    {
        downloadRotatingBarState++;
        downloadRotatingBarState %= bars.Length;
        float? percentage = bytesTransferred / (float)file.Size;
        queued = (transfer?.State & TransferStates.Queued) != 0;
        string bar;
        string state;
        bool downloading = false;

        if (stalled)
        {
            state = "Stalled";
            bar = "";
        }
        else if (transfer != null)
        {
            if (queued)
                state = "Queued";
            else if ((transfer.State & TransferStates.Initializing) != 0)
                state = "Initialize";
            else if ((transfer.State & TransferStates.Completed) != 0)
            {
                var flag = transfer.State & (TransferStates.Succeeded | TransferStates.Cancelled
                    | TransferStates.TimedOut | TransferStates.Errored | TransferStates.Rejected
                    | TransferStates.Aborted);
                state = flag.ToString();

                if (flag == TransferStates.Succeeded)
                    success = true;
            }
            else
            {
                state = transfer.State.ToString();
                if ((transfer.State & TransferStates.InProgress) != 0)
                    downloading = true;
            }

            bar = success ? "" : bars[downloadRotatingBarState] + " ";
        }
        else
        {
            state = "NullState";
            bar = "";
        }

        string txt = $"{bar}{state}:".PadRight(14) + $" {displayText}";
        bool needSimplePrintUpdate = (downloading && !updatedTextDownload) || (success && !updatedTextSuccess);
        updatedTextDownload |= downloading;
        updatedTextSuccess |= success;

        Console.ResetColor();
        Printing.RefreshOrPrint(progress, (int)((percentage ?? 0) * 100), txt, needSimplePrintUpdate, needSimplePrintUpdate);

    }

    public DateTime UpdateLastChangeTime(bool updateAllFromThisUser = true, bool forceChanged = false)
    {
        bool changed = prevTransferState != transfer?.State || prevBytesTransferred != bytesTransferred;
        if (changed || forceChanged)
        {
            lastChangeTime = DateTime.Now;
            stalled = false;
            if (updateAllFromThisUser)
            {
                foreach (var (_, dl) in downloads)
                {
                    if (dl != this && dl.response.Username == response.Username)
                        dl.UpdateLastChangeTime(updateAllFromThisUser: false, forceChanged: true);
                }
            }
        }
        prevTransferState = transfer?.State;
        prevBytesTransferred = bytesTransferred;
        return lastChangeTime;
    }
}
