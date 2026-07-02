using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

using Directory = System.IO.Directory;

namespace Tests.Core;

[TestClass]
public class StaleDownloadTests
{
    private static readonly TimeSpan MaxStaleTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(2);

    [TestMethod]
    public async Task SongDownload_ChosenResultCancelsAfterMaxStaleTimeWithoutActivity()
    {
        var outputDir = CreateOutputDir();
        var (response, candidate) = CreateCandidate("stalled-user", @"Music\Test Artist - Test Song.mp3");
        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Test Song" })
        {
            ResolvedTarget = candidate,
        };
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, song, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());

            clock.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            Assert.IsFalse(song.Cts?.IsCancellationRequested == true);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertCancelled(song);
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongDownload_QueuedResultCancelsAfterMaxStaleTimeWithoutActivity()
    {
        var outputDir = CreateOutputDir();
        var (response, candidate) = CreateCandidate("queued-user", @"Music\Test Artist - Queued Song.mp3");
        var queued = NewSignal();
        var releaseQueued = NewSignal();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            AfterDownloadStateChangedAsync = (_, _, state, ct) =>
            {
                if (!state.HasFlag(TransferStates.Queued))
                    return Task.CompletedTask;

                queued.TrySetResult();
                return releaseQueued.Task.WaitAsync(ct);
            },
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Queued Song" })
        {
            ResolvedTarget = candidate,
        };
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, song, settings, runCts.Token);
        try
        {
            await queued.Task.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertCancelled(song);
        }
        finally
        {
            runCts.Cancel();
            releaseQueued.TrySetResult();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongDownload_DownloadStartedWorkIsOutsideStaleWindow()
    {
        var outputDir = CreateOutputDir();
        var (response, candidate) = CreateCandidate("started-user", @"Music\Test Artist - Started Song.mp3");
        var client = new ClientTests.MockSoulseekClient([response]);
        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Started Song" })
        {
            ResolvedTarget = candidate,
            Config = settings,
            Cts = new CancellationTokenSource(),
        };
        var registry = new SessionRegistry();
        var events = new EngineEvents();
        var staleDownloads = new StaleDownloadCoordinator(registry, clock);
        var downloader = new Downloader(
            client,
            TestHelpers.CreateMockClientManager(client, engineSettings),
            registry,
            events,
            staleDownloads);
        var started = NewSignal();
        var releaseStarted = NewSignal();
        events.DownloadStarted += (_, _) =>
        {
            started.TrySetResult();
            releaseStarted.Task.GetAwaiter().GetResult();
        };
        var outputPath = Path.Combine(outputDir, "started.mp3");

        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(song.Cts.Token);
        var downloadTask = Task.Run(() => downloader.DownloadFile(
            candidate,
            outputPath,
            song,
            settings.Transfer,
            settings.Output.ParentDir,
            downloadCts.Token));
        try
        {
            await started.Task.WaitAsync(SignalTimeout);

            clock.Advance(MaxStaleTime);
            Assert.AreEqual(0, staleDownloads.CancelStaleDownloads(),
                "Work before the Soulseek transfer is established must not be part of stale cancellation.");
            Assert.IsFalse(song.Cts?.IsCancellationRequested == true);

            releaseStarted.TrySetResult();
            var outcome = await downloadTask.WaitAsync(SignalTimeout);
            Assert.AreEqual(FileDownloadStatus.Completed, outcome.Status);
        }
        finally
        {
            downloadCts.Cancel();
            releaseStarted.TrySetResult();
            try { await IgnoreRunCancellation(downloadTask.WaitAsync(SignalTimeout)); }
            catch (TimeoutException) { }
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongDownload_FallbackWorkIsOutsideStaleWindow()
    {
        var outputDir = CreateOutputDir();
        var client = new ClientTests.MockSoulseekClient([]);
        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var fallback = new BlockingFallback();
        var song = new SongJob(new SongQuery { Artist = "Test Artist", Title = "Fallback Song" })
        {
            Candidates = [],
        };
        var engine = CreateEngine(engineSettings, client, clock, fallback);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, song, settings, runCts.Token);
        try
        {
            await fallback.Started.WaitAsync(SignalTimeout);

            clock.Advance(MaxStaleTime);
            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting(),
                "Fallback download work must not be part of Soulseek stale cancellation.");
            Assert.IsFalse(song.Cts?.IsCancellationRequested == true);

            var fallbackPath = Path.Combine(outputDir, "fallback.mp3");
            await System.IO.File.WriteAllBytesAsync(fallbackPath, [1, 2, 3], runCts.Token);
            fallback.Release(fallbackPath);
            await runTask.WaitAsync(SignalTimeout);

            Assert.AreEqual(JobTerminalOutcome.Succeeded, song.TerminalOutcome);
            Assert.AreEqual(SongDownloadSource.Fallback, song.DownloadSource);
        }
        finally
        {
            runCts.Cancel();
            fallback.Release(null);
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AlbumDownload_StaleTrackCancelsAlbum()
    {
        var outputDir = CreateOutputDir();
        var file = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - Test Song.mp3", size: 5000, length: 180);
        var response = CreateResponse("album-user", file);
        var folder = CreateAlbumFolder(response, file);
        var album = new AlbumJob(new AlbumQuery { Artist = "Test Artist", Album = "Test Album" })
        {
            ResolvedTarget = folder,
            Results = [folder],
            AllowBrowseResolvedTarget = false,
            SkipResolvedTargetTrackCountVerification = true,
        };

        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };
        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, album, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertCancelled(album);
            Assert.AreEqual(JobCancellationSource.InternalEngine, album.CancellationSource);
            StringAssert.Contains(album.FailureMessage, "Album download became stale");
            Assert.IsTrue(album.TrackJobs.Any(song => song.FailureReason == JobFailureReason.Cancelled));
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AggregateDownload_StaleChildCancelsAggregate()
    {
        var outputDir = CreateOutputDir();
        var file = TestHelpers.CreateSlFile(@"Music\Test Artist - Test Song.mp3", size: 5000, length: 180);
        var response = CreateResponse("aggregate-user", file);
        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        settings.Search.MinSharesAggregate = 1;
        var aggregate = new AggregateJob(new SongQuery { Artist = "Test Artist", Title = "Test Song" });
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, aggregate, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertCancelled(aggregate);
            Assert.IsTrue(aggregate.Songs.Any(song => song.FailureReason == JobFailureReason.Cancelled));
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AlbumAggregateDownload_StaleTrackCancelsAlbumAggregate()
    {
        var outputDir = CreateOutputDir();
        var file = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - Test Song.mp3", size: 5000, length: 180);
        var response = CreateResponse("album-aggregate-user", file);
        var downloadGate = new TestHelpers.DownloadGate();
        var client = new ClientTests.MockSoulseekClient([response])
        {
            BeforeDownloadCompletesAsync = downloadGate.BlockAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        settings.Search.MinSharesAggregate = 1;
        settings.Search.NoBrowseFolder = true;
        var aggregate = new AlbumAggregateJob(new AlbumQuery { Artist = "Test Artist" });
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, aggregate, settings, runCts.Token);
        try
        {
            await downloadGate.WaitForStartedCountAsync(1);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());
            clock.Advance(MaxStaleTime);
            Assert.AreEqual(1, engine.RunStaleDownloadCheckForTesting());

            await runTask.WaitAsync(SignalTimeout);
            AssertCancelled(aggregate);
            Assert.IsTrue(aggregate.Albums.SelectMany(album => album.TrackJobs).Any(song => song.FailureReason == JobFailureReason.Cancelled));
        }
        finally
        {
            runCts.Cancel();
            downloadGate.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task SongJobList_QueuedSameUserSongUsesSiblingActivityForStaleTimer()
    {
        var outputDir = CreateOutputDir();
        var firstFile = TestHelpers.CreateSlFile(@"Music\Test Artist - First Single.mp3", size: 50000, length: 180);
        var secondFile = TestHelpers.CreateSlFile(@"Music\Other Artist - Second Single.mp3", size: 50000, length: 181);
        var response = CreateResponse("single-slot-song-user", firstFile, secondFile);
        var firstSong = new SongJob(new SongQuery { Artist = "Test Artist", Title = "First Single" })
        {
            ResolvedTarget = new FileCandidate(response, firstFile),
        };
        var secondSong = new SongJob(new SongQuery { Artist = "Other Artist", Title = "Second Single" })
        {
            ResolvedTarget = new FileCandidate(response, secondFile),
        };
        var songList = new JobList("same-user songs", [firstSong, secondSong]);

        var sameUserQueue = new SameUserQueuedSiblingProbe();

        var client = new ClientTests.MockSoulseekClient([response])
        {
            AfterDownloadStateChangedAsync = sameUserQueue.OnStateChangedAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, songList, settings, runCts.Token);
        try
        {
            await sameUserQueue.BothQueued.WaitAsync(SignalTimeout);
            await sameUserQueue.ActiveInitializing.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());

            clock.Advance(MaxStaleTime);
            sameUserQueue.ReleaseInitializing();
            await sameUserQueue.ActiveInProgress.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting(),
                "A queued song job should inherit fresh activity from another active download by the same user.");
            var queuedSong = new[] { firstSong, secondSong }.Single(song =>
                !song.ResolvedTarget!.Filename.Equals(sameUserQueue.ActiveFilename, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(queuedSong.Cts?.IsCancellationRequested == true);
            Assert.AreNotEqual(JobFailureReason.Cancelled, queuedSong.FailureReason);
        }
        finally
        {
            runCts.Cancel();
            sameUserQueue.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    [TestMethod]
    public async Task AlbumDownload_QueuedSameUserTrackUsesSiblingActivityForStaleTimer()
    {
        var outputDir = CreateOutputDir();
        var firstFile = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\01. Test Artist - First.mp3", size: 50000, length: 180);
        var secondFile = TestHelpers.CreateSlFile(@"Music\Test Artist\Test Album\02. Test Artist - Second.mp3", size: 50000, length: 181);
        var response = CreateResponse("single-slot-user", firstFile, secondFile);
        var folder = CreateAlbumFolder(response, firstFile, secondFile);
        var album = new AlbumJob(new AlbumQuery { Artist = "Test Artist", Album = "Test Album" })
        {
            ResolvedTarget = folder,
            Results = [folder],
            AllowBrowseResolvedTarget = false,
            SkipResolvedTargetTrackCountVerification = true,
        };

        var sameUserQueue = new SameUserQueuedSiblingProbe();

        var client = new ClientTests.MockSoulseekClient([response])
        {
            AfterDownloadStateChangedAsync = sameUserQueue.OnStateChangedAsync,
        };

        var clock = new ManualTimeProvider();
        var engineSettings = CreateEngineSettings();
        var settings = CreateSettings(outputDir);
        var engine = CreateEngine(engineSettings, client, clock);

        using var runCts = new CancellationTokenSource();
        var runTask = Start(engine, album, settings, runCts.Token);
        try
        {
            await sameUserQueue.BothQueued.WaitAsync(SignalTimeout);
            await sameUserQueue.ActiveInitializing.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting());

            clock.Advance(MaxStaleTime);
            sameUserQueue.ReleaseInitializing();
            await sameUserQueue.ActiveInProgress.WaitAsync(SignalTimeout);

            Assert.AreEqual(0, engine.RunStaleDownloadCheckForTesting(),
                "A queued download should inherit fresh activity from another active download by the same user.");
            var queuedSong = album.TrackJobs.Single(song =>
                !song.ResolvedTarget!.Filename.Equals(sameUserQueue.ActiveFilename, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(queuedSong.Cts?.IsCancellationRequested == true);
            Assert.AreNotEqual(JobFailureReason.Cancelled, queuedSong.FailureReason);
        }
        finally
        {
            runCts.Cancel();
            sameUserQueue.ReleaseAll();
            await IgnoreRunCancellation(runTask);
            DeleteOutputDir(outputDir);
        }
    }

    private static Task Start(DownloadEngine engine, Job job, DownloadSettings settings, CancellationToken cancellationToken)
    {
        engine.Enqueue(job, settings);
        engine.CompleteEnqueue();
        return engine.RunAsync(cancellationToken);
    }

    private static DownloadEngine CreateEngine(
        EngineSettings engineSettings,
        ClientTests.MockSoulseekClient client,
        ManualTimeProvider clock,
        ISongDownloadFallback? songDownloadFallback = null)
    {
        var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings),
            songDownloadFallback: songDownloadFallback,
            timeProvider: clock);
        engine.AutomaticStaleChecksEnabled = false;
        return engine;
    }

    private static EngineSettings CreateEngineSettings()
        => new() { Username = "u", Password = "p", ConcurrentSearches = 10 };

    private static DownloadSettings CreateSettings(string outputDir)
    {
        var settings = new DownloadSettings();
        settings.Output.ParentDir = outputDir;
        settings.Search.MaxStaleTime = (int)MaxStaleTime.TotalMilliseconds;
        settings.Search.NoBrowseFolder = true;
        settings.Transfer.UnknownErrorRetries = 1;
        settings.Transfer.MaxDownloadRetries = 1;
        settings.Skip.SkipExisting = false;
        return settings;
    }

    private static (SearchResponse Response, FileCandidate Candidate) CreateCandidate(
        string username,
        string filename,
        long size = 5000)
    {
        var file = TestHelpers.CreateSlFile(filename, size: size, length: 180);
        var response = CreateResponse(username, file);
        return (response, new FileCandidate(response, file));
    }

    private static SearchResponse CreateResponse(string username, params Soulseek.File[] files)
        => new(
            username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100_000,
            queueLength: 0,
            fileList: files);

    private static AlbumFolder CreateAlbumFolder(SearchResponse response, params Soulseek.File[] files)
        => new(
            response.Username,
            Utils.GetDirectoryNameSlsk(files[0].Filename),
            files.Select(file => TestHelpers.CreateAlbumFile(response, file)).ToList())
        {
            IsFullyRetrieved = true,
        };

    private static string CreateOutputDir()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "sockseek-stale-download-" + Guid.NewGuid());
        Directory.CreateDirectory(outputDir);
        return outputDir;
    }

    private static void DeleteOutputDir(string outputDir)
    {
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertCancelled(Job job)
    {
        Assert.AreEqual(JobLifecycleState.Terminal, job.LifecycleState);
        Assert.AreEqual(JobTerminalOutcome.Cancelled, job.TerminalOutcome);
        Assert.AreEqual(JobFailureReason.Cancelled, job.FailureReason);
    }

    private static async Task IgnoreRunCancellation(Task runTask)
    {
        try { await runTask; }
        catch (OperationCanceledException) { }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException)) { }
    }

    private sealed class BlockingFallback : ISongDownloadFallback
    {
        private readonly TaskCompletionSource started = NewSignal();
        private readonly TaskCompletionSource release = NewSignal();
        private string? downloadPath;

        public Task Started => started.Task;

        public bool CanRun(SongJob song, DownloadSettings settings)
            => true;

        public async Task<JobOutcome?> TryDownloadAsync(
            SongJob song,
            DownloadSettings settings,
            FileManager organizer,
            IJobLog? log,
            CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return string.IsNullOrWhiteSpace(downloadPath)
                ? null
                : JobOutcome.Done(downloadPath, downloadSource: SongDownloadSource.Fallback);
        }

        public void Release(string? path)
        {
            downloadPath = path;
            release.TrySetResult();
        }
    }

    private sealed class SameUserQueuedSiblingProbe
    {
        private readonly object gate = new();
        private readonly HashSet<string> queuedFilenames = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource bothQueued = NewSignal();
        private readonly TaskCompletionSource activeInitializing = NewSignal();
        private readonly TaskCompletionSource activeInProgress = NewSignal();
        private readonly TaskCompletionSource releaseInitializing = NewSignal();
        private readonly TaskCompletionSource releaseInProgress = NewSignal();

        public string? ActiveFilename { get; private set; }
        public Task BothQueued => bothQueued.Task;
        public Task ActiveInitializing => activeInitializing.Task;
        public Task ActiveInProgress => activeInProgress.Task;

        public async Task OnStateChangedAsync(string username, string filename, TransferStates state, CancellationToken ct)
        {
            if (state.HasFlag(TransferStates.Queued))
            {
                lock (gate)
                {
                    queuedFilenames.Add(filename);
                    if (queuedFilenames.Count == 2)
                        bothQueued.TrySetResult();
                }
            }

            if (state.HasFlag(TransferStates.Initializing) && TryMarkActive(filename))
            {
                activeInitializing.TrySetResult();
                await releaseInitializing.Task.WaitAsync(ct);
                return;
            }

            if (state.HasFlag(TransferStates.InProgress) && IsActive(filename))
            {
                activeInProgress.TrySetResult();
                await releaseInProgress.Task.WaitAsync(ct);
            }
        }

        public void ReleaseInitializing()
            => releaseInitializing.TrySetResult();

        public void ReleaseAll()
        {
            releaseInitializing.TrySetResult();
            releaseInProgress.TrySetResult();
        }

        private bool TryMarkActive(string filename)
        {
            lock (gate)
            {
                if (ActiveFilename != null)
                    return false;

                ActiveFilename = filename;
                return true;
            }
        }

        private bool IsActive(string filename)
        {
            lock (gate)
                return filename.Equals(ActiveFilename, StringComparison.OrdinalIgnoreCase);
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
