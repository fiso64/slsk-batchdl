using Soulseek;
using Sockseek.Core.Transfers.Downloads.State;

namespace Sockseek.Core.Services;

internal sealed class StaleDownloadCoordinator
{
    private readonly ActiveDownloadTracker activeDownloads;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Attempt> attempts = new();
    // Keep recent peer activity after an attempt completes, so queued same-user
    // siblings don't become stale the moment the active transfer leaves tracking.
    private readonly Dictionary<string, long> latestActivityByUser = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource deadlinesChanged = NewSignal();

    public StaleDownloadCoordinator(ActiveDownloadTracker activeDownloads, TimeProvider? timeProvider = null)
    {
        this.activeDownloads = activeDownloads;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Arms stale detection only for the Soulseek peer-transfer call. Search,
    // setup, fallback, organization, and on-complete work must stay outside this scope.
    internal async Task<T> WatchPeerTransferAsync<T>(
        ActiveDownload download,
        int maxStaleTimeMs,
        Func<PeerTransferActivity, Task<T>> transfer)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(transfer);

        var attemptId = BeginPeerTransfer(download, maxStaleTimeMs);
        try
        {
            return await transfer(new PeerTransferActivity(this, attemptId));
        }
        finally
        {
            CompletePeerTransfer(attemptId);
        }
    }

    private Guid BeginPeerTransfer(ActiveDownload download, int maxStaleTimeMs)
    {
        var attempt = new Attempt(
            Guid.NewGuid(),
            download,
            Math.Max(0, maxStaleTimeMs),
            timeProvider.GetTimestamp());

        lock (gate)
            attempts[attempt.Id] = attempt;

        SignalDeadlinesChanged();
        return attempt.Id;
    }

    private void ReportState(Guid attemptId, Transfer transfer)
        => ReportActivity(attemptId, transfer);

    private void ReportProgress(Guid attemptId, Transfer transfer)
        => ReportActivity(attemptId, transfer);

    private void CompletePeerTransfer(Guid attemptId)
    {
        bool removed;
        lock (gate)
            removed = attempts.Remove(attemptId);

        if (removed)
            SignalDeadlinesChanged();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextStaleCheck();

                if (delay == null)
                {
                    await WaitForDeadlinesChangedAsync(cancellationToken);
                    continue;
                }

                if (delay <= TimeSpan.Zero)
                {
                    CancelStaleDownloads();
                    continue;
                }

                await WaitForDeadlinesChangedOrDelayAsync(delay.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SockseekLog.Jobs.Error(ex, "Error in stale download scheduler");
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            }
        }
    }

    public int CancelStaleDownloads()
    {
        var now = timeProvider.GetTimestamp();
        List<Attempt> staleAttempts;

        lock (gate)
        {
            staleAttempts = GetStaleAttempts(now).ToList();

            foreach (var attempt in staleAttempts)
                attempts.Remove(attempt.Id);
        }

        if (staleAttempts.Count > 0)
            SignalDeadlinesChanged();

        foreach (var attempt in staleAttempts)
        {
            var download = attempt.Download;
            download.MarkStaleCancelled(attempt.MaxStaleTimeMs);
            try { download.Cts.Cancel(); } catch { }
            activeDownloads.TryRemove(download.Candidate.Filename, out _);
        }

        return staleAttempts.Count;
    }

    private void ReportActivity(Guid attemptId, Transfer transfer)
    {
        Attempt? attempt;
        bool changed;

        lock (gate)
        {
            if (!attempts.TryGetValue(attemptId, out attempt))
                return;

            attempt.Download.Transfer = transfer;

            var stateChanged = attempt.State != transfer.State;
            var bytesChanged = attempt.BytesTransferred != transfer.BytesTransferred;
            changed = stateChanged || bytesChanged;

            attempt.State = transfer.State;
            attempt.BytesTransferred = transfer.BytesTransferred;

            if (changed)
            {
                var now = timeProvider.GetTimestamp();
                attempt.LastOwnActivityTimestamp = now;
                latestActivityByUser[attempt.Download.Candidate.Username] = now;
            }
        }

        if (changed)
            SignalDeadlinesChanged();
    }

    private TimeSpan? GetDelayUntilNextStaleCheck()
    {
        var now = timeProvider.GetTimestamp();
        TimeSpan? nextDelay = null;

        lock (gate)
        {
            if (attempts.Count == 0)
                return null;

            var latestActivityByUser = GetLatestActivityByUser();
            foreach (var attempt in attempts.Values)
            {
                var referenceTimestamp = GetReferenceTimestamp(attempt, latestActivityByUser);
                var elapsed = timeProvider.GetElapsedTime(referenceTimestamp, now);
                var remaining = TimeSpan.FromMilliseconds(attempt.MaxStaleTimeMs) - elapsed;

                if (nextDelay == null || remaining < nextDelay)
                    nextDelay = remaining;
            }
        }

        return nextDelay < TimeSpan.Zero ? TimeSpan.Zero : nextDelay;
    }

    private List<Attempt> GetStaleAttempts(long now)
    {
        var staleAttempts = new List<Attempt>();
        var latestActivityByUser = GetLatestActivityByUser();
        foreach (var attempt in attempts.Values)
        {
            var referenceTimestamp = GetReferenceTimestamp(attempt, latestActivityByUser);
            if (timeProvider.GetElapsedTime(referenceTimestamp, now).TotalMilliseconds < attempt.MaxStaleTimeMs)
                continue;

            staleAttempts.Add(attempt);
        }

        return staleAttempts;
    }

    private Dictionary<string, long> GetLatestActivityByUser()
    {
        var latest = new Dictionary<string, long>(latestActivityByUser, StringComparer.OrdinalIgnoreCase);
        foreach (var attempt in attempts.Values)
        {
            var username = attempt.Download.Candidate.Username;
            if (!latest.TryGetValue(username, out var latestActivity)
                || attempt.LastOwnActivityTimestamp > latestActivity)
            {
                latest[username] = attempt.LastOwnActivityTimestamp;
            }
        }

        return latest;
    }

    private static long GetReferenceTimestamp(
        Attempt attempt,
        IReadOnlyDictionary<string, long> latestActivityByUser)
    {
        var referenceTimestamp = attempt.LastOwnActivityTimestamp;
        // Queued siblings from the same user are protected by any fresh same-user activity;
        // an in-progress transfer must make progress on its own.
        if (!IsInProgress(attempt.State)
            && latestActivityByUser.TryGetValue(attempt.Download.Candidate.Username, out var latestUserActivity)
            && latestUserActivity > referenceTimestamp)
        {
            referenceTimestamp = latestUserActivity;
        }

        return referenceTimestamp;
    }

    private async Task WaitForDeadlinesChangedOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var signal = GetDeadlinesChangedTask();
        var signalTask = signal.WaitAsync(cancellationToken);
        var delayTask = Task.Delay(delay, timeProvider, cancellationToken);
        await await Task.WhenAny(signalTask, delayTask);
    }

    private async Task WaitForDeadlinesChangedAsync(CancellationToken cancellationToken)
    {
        var signal = GetDeadlinesChangedTask();
        await signal.WaitAsync(cancellationToken);
    }

    private Task GetDeadlinesChangedTask()
    {
        lock (gate)
            return deadlinesChanged.Task;
    }

    private void SignalDeadlinesChanged()
    {
        TaskCompletionSource previousSignal;
        lock (gate)
        {
            previousSignal = deadlinesChanged;
            deadlinesChanged = NewSignal();
        }

        previousSignal.TrySetResult();
    }

    private static bool IsInProgress(TransferStates? state)
        => state.HasValue && state.Value.HasFlag(TransferStates.InProgress);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal readonly struct PeerTransferActivity
    {
        private readonly StaleDownloadCoordinator coordinator;
        private readonly Guid attemptId;

        internal PeerTransferActivity(StaleDownloadCoordinator coordinator, Guid attemptId)
        {
            this.coordinator = coordinator;
            this.attemptId = attemptId;
        }

        public void ReportState(Transfer transfer)
            => coordinator.ReportState(attemptId, transfer);

        public void ReportProgress(Transfer transfer)
            => coordinator.ReportProgress(attemptId, transfer);
    }

    private sealed class Attempt
    {
        public Attempt(Guid id, ActiveDownload download, int maxStaleTimeMs, long registeredAtTimestamp)
        {
            Id = id;
            Download = download;
            MaxStaleTimeMs = maxStaleTimeMs;
            LastOwnActivityTimestamp = registeredAtTimestamp;
        }

        public Guid Id { get; }
        public ActiveDownload Download { get; }
        public int MaxStaleTimeMs { get; }
        public long LastOwnActivityTimestamp { get; set; }
        public TransferStates? State { get; set; }
        public long? BytesTransferred { get; set; }
    }
}
