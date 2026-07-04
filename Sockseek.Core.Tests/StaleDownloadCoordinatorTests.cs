using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Transfers.Downloads.State;
using System.Text.RegularExpressions;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Tests.Core;

[TestClass]
public class StaleDownloadCoordinatorTests
{
    private static readonly TimeSpan MaxStaleTime = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void StaleCoordinator_IsOnlyArmedByDownloaderPeerTransferScope()
    {
        var repositoryRoot = FindRepositoryRoot();
        var coreRoot = Path.Combine(repositoryRoot, "Sockseek.Core");
        var forbiddenPatterns = new[]
        {
            new Regex(@"\bstaleDownloads\.(Register|Complete|ReportState|ReportProgress)\s*\(", RegexOptions.Singleline),
            new Regex(@"\.BeginPeerTransfer\s*\(", RegexOptions.Singleline),
            new Regex(@"\.CompletePeerTransfer\s*\(", RegexOptions.Singleline),
        };
        var watchPattern = new Regex(@"\.WatchPeerTransferAsync\s*\(", RegexOptions.Singleline);
        var allowedWatchFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(coreRoot, "Transfers", "Downloads", "Downloader.cs"),
            Path.Combine(coreRoot, "Transfers", "Downloads", "StaleDetection", "StaleDownloadCoordinator.cs"),
        };

        var offenders = Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !PathContainsSegment(path, "bin") && !PathContainsSegment(path, "obj"))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                var matches = forbiddenPatterns
                    .SelectMany(pattern => pattern.Matches(text).Cast<Match>());

                if (!allowedWatchFiles.Contains(path))
                    matches = matches.Concat(watchPattern.Matches(text).Cast<Match>());

                return matches.Select(match => $"{Path.GetRelativePath(repositoryRoot, path)}:{LineNumber(text, match.Index)}");
            })
            .Distinct()
            .OrderBy(line => line)
            .ToList();

        if (offenders.Count > 0)
            Assert.Fail("Stale cancellation must only be armed by Downloader via StaleDownloadCoordinator.WatchPeerTransferAsync:\n" + string.Join("\n", offenders));
    }

    [TestMethod]
    public void QueuedAttempt_CancelsTransferAfterMaxStaleTimeWithoutActivity()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(attempt);
        Assert.IsFalse(scenario.ActiveDownloads.Contains(attempt.Download.Candidate.Filename));
    }

    [TestMethod]
    public void StateChangesBeforeMaxStaleTimeRefreshDeadline()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportState(attempt, TransferStates.Initializing, bytesTransferred: 0);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, scenario.CancelStaleDownloads());

        scenario.ReportState(attempt, TransferStates.InProgress, bytesTransferred: 0);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(attempt);
    }

    [TestMethod]
    public void ProgressBeforeMaxStaleTimeRefreshesInProgressDeadline()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(attempt, bytesTransferred: 4096);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(attempt);
    }

    [TestMethod]
    public void UnchangedStateAndBytesDoNotRefreshDeadline()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(attempt);
    }

    [TestMethod]
    public void QueuedAttemptUsesFreshActivityFromSameUserSibling()
    {
        using var scenario = new Scenario();
        var queued = scenario.Start("user-a", @"Music\Artist - Queued.mp3");
        var active = scenario.Start("user-a", @"Music\Artist - Active.mp3");
        scenario.ReportState(queued, TransferStates.Queued | TransferStates.Remotely, bytesTransferred: 0);
        scenario.ReportState(active, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(active, bytesTransferred: 4096);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(queued.Download.Cts.IsCancellationRequested);
        Assert.IsFalse(active.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(2, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(queued);
        AssertStaleTransferCancelled(active);
    }

    [TestMethod]
    public void QueuedAttemptUsesFreshActivityFromCompletedSameUserSibling()
    {
        using var scenario = new Scenario();
        var queued = scenario.Start("user-a", @"Music\Artist - Queued.mp3");
        var active = scenario.Start("user-a", @"Music\Artist - Active.mp3");
        scenario.ReportState(queued, TransferStates.Queued | TransferStates.Remotely, bytesTransferred: 0);
        scenario.ReportState(active, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(active, bytesTransferred: 4096);
        scenario.Complete(active);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(0, scenario.CancelStaleDownloads(),
            "A queued attempt should inherit recent activity from a same-user sibling even after that sibling completes.");
        Assert.IsFalse(queued.Download.Cts.IsCancellationRequested);

        scenario.Advance(MaxStaleTime);
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(queued);
    }

    [TestMethod]
    public void InProgressAttemptDoesNotUseSiblingActivity()
    {
        using var scenario = new Scenario();
        var stalled = scenario.Start("user-a", @"Music\Artist - Stalled.mp3");
        var active = scenario.Start("user-a", @"Music\Artist - Active.mp3");
        scenario.ReportState(stalled, TransferStates.InProgress, bytesTransferred: 0);
        scenario.ReportState(active, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(active, bytesTransferred: 4096);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(stalled);
        Assert.IsFalse(active.Download.Cts.IsCancellationRequested);
    }

    [TestMethod]
    public void ActivityFromDifferentUserDoesNotProtectQueuedAttempt()
    {
        using var scenario = new Scenario();
        var queued = scenario.Start("user-a", @"Music\Artist - Queued.mp3");
        var otherUser = scenario.Start("user-b", @"Music\Artist - Other.mp3");
        scenario.ReportState(queued, TransferStates.Queued | TransferStates.Remotely, bytesTransferred: 0);
        scenario.ReportState(otherUser, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(otherUser, bytesTransferred: 4096);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(queued);
        Assert.IsFalse(otherUser.Download.Cts.IsCancellationRequested);
    }

    [TestMethod]
    public void CompletedAttemptIsRemovedFromStaleTracking()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);
        scenario.Complete(attempt);

        scenario.Advance(MaxStaleTime);

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);
    }

    [TestMethod]
    public void StaleCoordinator_DoesNotWriteUserFacingAttemptLogs()
    {
        SockseekLog.RemoveNonFileOutputs();
        var entries = new List<SockseekLog.StructuredLogEntry>();
        SockseekLog.AddStructuredSink((entry, _) => entries.Add(entry));
        try
        {
            using var scenario = new Scenario();
            scenario.Start("user-a", @"Music\Artist - Song.mp3");

            scenario.Advance(MaxStaleTime);
            Assert.AreEqual(1, scenario.CancelStaleDownloads());

            Assert.IsFalse(entries.Any(entry => entry.CategoryName == SockseekLog.Categories.Jobs),
                "The coordinator is only the watchdog; Downloader/album orchestration own user-facing stale diagnostics.");
        }
        finally
        {
            SockseekLog.RemoveNonFileOutputs();
        }
    }

    private static void AssertStaleTransferCancelled(AttemptHandle attempt)
    {
        Assert.IsFalse(attempt.Song.Cts?.IsCancellationRequested == true);
        Assert.IsTrue(attempt.Download.Cts.IsCancellationRequested);
        Assert.IsTrue(attempt.Download.IsStaleCancelled);
        Assert.AreEqual((int)MaxStaleTime.TotalMilliseconds, attempt.Download.StaleMaxStaleTimeMs);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sockseek.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Sockseek.sln.");
    }

    private static bool PathContainsSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private static int LineNumber(string text, int index)
        => text.AsSpan(0, index).Count('\n') + 1;

    private sealed class Scenario : IDisposable
    {
        private readonly ManualTimeProvider clock = new();
        private readonly List<AttemptHandle> attempts = [];

        public Scenario()
        {
            Coordinator = new StaleDownloadCoordinator(ActiveDownloads, clock);
        }

        public ActiveDownloadTracker ActiveDownloads { get; } = new();
        public StaleDownloadCoordinator Coordinator { get; }

        public AttemptHandle Start(string username, string filename, Job? parentJob = null)
        {
            var response = new SearchResponse(username, 1, true, 100_000, 0, []);
            var file = TestHelpers.CreateSlFile(filename, size: 50_000, length: 180);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = Path.GetFileNameWithoutExtension(filename) })
            {
                Cts = new CancellationTokenSource(),
            };
            var activeDownload = new ActiveDownload(song, candidate, new CancellationTokenSource(), parentJob);
            ActiveDownloads.TryAdd(activeDownload);
            var activityReady = new TaskCompletionSource<StaleDownloadCoordinator.PeerTransferActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Coordinator.WatchPeerTransferAsync(
                activeDownload,
                (int)MaxStaleTime.TotalMilliseconds,
                async activity =>
                {
                    activityReady.TrySetResult(activity);
                    await release.Task;
                    return true;
                });

            var attempt = new AttemptHandle(
                song,
                activeDownload,
                activityReady.Task.GetAwaiter().GetResult(),
                release,
                task);
            attempts.Add(attempt);
            return attempt;
        }

        public void ReportState(AttemptHandle attempt, TransferStates state, long bytesTransferred)
            => attempt.Activity.ReportState(CreateTransfer(attempt, state, bytesTransferred));

        public void ReportProgress(AttemptHandle attempt, long bytesTransferred)
            => attempt.Activity.ReportProgress(CreateTransfer(attempt, TransferStates.InProgress, bytesTransferred));

        public void Complete(AttemptHandle attempt)
            => attempt.Complete();

        public int CancelStaleDownloads()
            => Coordinator.CancelStaleDownloads();

        public void Advance(TimeSpan timeSpan)
            => clock.Advance(timeSpan);

        private static Transfer CreateTransfer(AttemptHandle attempt, TransferStates state, long bytesTransferred)
            => new(
                TransferDirection.Download,
                attempt.Download.Candidate.Username,
                attempt.Download.Candidate.Filename,
                1,
                state,
                attempt.Download.Candidate.File.Size,
                0,
                bytesTransferred);

        public void Dispose()
        {
            foreach (var attempt in attempts)
                attempt.Complete();
        }
    }

    private sealed record AttemptHandle(
        SongJob Song,
        ActiveDownload Download,
        StaleDownloadCoordinator.PeerTransferActivity Activity,
        TaskCompletionSource Release,
        Task Task)
    {
        public void Complete()
        {
            Release.TrySetResult();
            Task.GetAwaiter().GetResult();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan timeSpan)
        {
            utcNow += timeSpan;
            timestamp += timeSpan.Ticks;
        }
    }
}
