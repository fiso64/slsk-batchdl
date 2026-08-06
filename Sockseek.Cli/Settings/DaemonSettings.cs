namespace Sockseek.Cli;

/// Settings consumed by the CLI launcher when hosting `Sockseek daemon`.
public class DaemonSettings
{
    /// IP/interface used by `Sockseek daemon` for the HTTP/SignalR API.
    public string ListenIp { get; set; } = "127.0.0.1";

    /// Port used by `Sockseek daemon` for the HTTP/SignalR API.
    public int ListenPort { get; set; } = 5030;
    public string? DataDirectory { get; set; }
    public bool RetentionEnabled { get; set; } = true;
    public TimeSpan? CompletedJobRetention { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan? UnsuccessfulJobRetention { get; set; } = TimeSpan.FromDays(180);
    public TimeSpan? SearchResultRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan? TransferRetention { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan? PrivateMessageRetention { get; set; }
    public TimeSpan? RoomMessageRetention { get; set; } = TimeSpan.FromDays(30);
    public int? MaximumRetainedJobs { get; set; } = 100_000;
}
