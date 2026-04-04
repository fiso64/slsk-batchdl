using Enums;
using Jobs;


/// Utility class to handle interval-based progress reports.
///
/// See: https://johnscolaro.xyz/blog/log-by-time-not-by-count
public class IntervalProgressReporter
{
    public readonly TimeSpan Interval;
    private readonly int countInterval;
    private int loggedCount = 0;
    private DateTime lastLoggedTime = new(0, DateTimeKind.Utc);

    private int downloadedCount = 0;
    private int failedCount = 0;
    private readonly int totalCount = 0;
    private readonly object _reportLock = new();

    public IntervalProgressReporter(TimeSpan interval, int countInterval, IEnumerable<SongJob> songs)
    {
        this.Interval      = interval;
        this.countInterval = countInterval;

        foreach (var song in songs)
        {
            if (song.State == TrackState.Downloaded || song.State == TrackState.AlreadyExists)
                downloadedCount++;
            else if (song.State == TrackState.Failed || song.State == TrackState.NotFoundLastTime)
                failedCount++;
            totalCount++;
        }
    }

    public void MaybeReport(TrackState state)
    {
        lock (_reportLock)
        {
            if (state == TrackState.Downloaded)
                downloadedCount++;
            else if (state == TrackState.Failed)
                failedCount++;
            else
                return;

            loggedCount++;

            var now = DateTime.UtcNow;
            var timeConditionMet  = (now - lastLoggedTime) > Interval;
            var countConditionMet = countInterval <= 0 || (loggedCount >= countInterval);

            if (timeConditionMet && countConditionMet)
            {
                lastLoggedTime = now;
                loggedCount    = 0;

                var failedStr       = failedCount > 0 ? $", Failed {failedCount}" : "";
                var percentComplete = (double)(downloadedCount + failedCount) / totalCount;
                Logger.Info($"Downloaded {downloadedCount}{failedStr} of Total {totalCount} ({percentComplete:P})", color: ConsoleColor.DarkGray);
            }
        }
    }
}
