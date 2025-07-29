/// Utility class to handle interval-based logging.
///
/// See: https://johnscolaro.xyz/blog/log-by-time-not-by-count
public class IntervalLogger
{
    public readonly TimeSpan Interval;
    private DateTime lastLogged = new DateTime(0, DateTimeKind.Utc);

    public IntervalLogger(TimeSpan interval)
    {
        this.Interval = interval;
    }

    public void MaybeLog(Action action)
    {
        var now = DateTime.UtcNow;
        if (now - lastLogged > Interval)
        {
            lastLogged = now;
            action();
        }
    }
}
