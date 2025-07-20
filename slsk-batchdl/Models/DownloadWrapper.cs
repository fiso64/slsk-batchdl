using Soulseek;
using System.Collections.Concurrent;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;

namespace Models
{
    public class DownloadWrapper
    {
        public string savePath;
        public string displayText = "";
        public int barState = 0;
        public Soulseek.File file;
        public Transfer? transfer;
        public SearchResponse response;
        public ProgressBar progress;
        public Track track;
        public TrackListEntry tle;
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

        public DownloadWrapper(string savePath, SearchResponse response, Soulseek.File file, Track track, CancellationTokenSource cts, ProgressBar progress, TrackListEntry tle)
        {
            this.savePath = savePath;
            this.response = response;
            this.file = file;
            this.cts = cts;
            this.tle = tle;
            this.track = track;
            this.progress = progress;
            this.displayText = Printing.DisplayString(track, file, response);

            Printing.RefreshOrPrint(progress, 0, "Initialize: " + displayText, true);
            Printing.RefreshOrPrint(progress, 0, displayText, false);
        }

        public void UpdateText()
        {
            float percentage = file.Size > 0 ? bytesTransferred / (float)file.Size : 0;
            queued = (transfer?.State & TransferStates.Queued) != 0;
            string bar;
            string state;
            bool downloading = false;

            if (stalled)
            {
                state = "Stalled";
                bar = "  ";
            }
            else if (transfer != null)
            {
                if (queued)
                {
                    if ((transfer.State & TransferStates.Remotely) != 0)
                        state = "Queued (R)";
                    else
                        state = "Queued (L)";
                    bar = "  ";
                }
                else if ((transfer.State & TransferStates.Initializing) != 0)
                {
                    state = "Initialize";
                    bar = "  ";
                }
                else if ((transfer.State & TransferStates.Completed) != 0)
                {
                    var flag = transfer.State & (TransferStates.Succeeded | TransferStates.Cancelled
                        | TransferStates.TimedOut | TransferStates.Errored | TransferStates.Rejected
                        | TransferStates.Aborted);
                    state = flag.ToString();

                    if (flag == TransferStates.Succeeded)
                    {
                        success = true;
                        if (file.Size < 0) percentage = 1;
                    }

                    bar = "";
                }
                else
                {
                    state = transfer.State.ToString();
                    if ((transfer.State & TransferStates.InProgress) != 0)
                    {
                        downloading = true;
                        barState = (barState + 1) % bars.Length;
                        bar = bars[barState] + " ";
                    }
                    else
                    {
                        bar = "  ";
                    }
                }
            }
            else
            {
                state = "NullState";
                bar = "  ";
            }

            bool needSimplePrintUpdate = (downloading && !updatedTextDownload) || (success && !updatedTextSuccess);
            bar = needSimplePrintUpdate ? "" : bar;

            string mbStr = file.Size <= 0 && bytesTransferred > 0 ? $" ({bytesTransferred / (float)(1024 * 1024):F2}MB)" : "";
            string txt = $"{bar}{state}{mbStr}:".PadRight(14) + $" {displayText}";
            updatedTextDownload |= downloading;
            updatedTextSuccess |= success;

            Console.ResetColor();
            Printing.RefreshOrPrint(progress, (int)(percentage * 100), txt, needSimplePrintUpdate, needSimplePrintUpdate);

        }

        public DateTime UpdateLastChangeTime(ConcurrentDictionary<string, DownloadWrapper> downloads, bool updateAllFromThisUser = true, bool forceChanged = false)
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
                            dl.UpdateLastChangeTime(downloads, updateAllFromThisUser: false, forceChanged: true);
                    }
                }
            }
            prevTransferState = transfer?.State;
            prevBytesTransferred = bytesTransferred;
            return lastChangeTime;
        }
    }
}
