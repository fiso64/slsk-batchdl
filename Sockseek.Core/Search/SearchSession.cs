using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sockseek.Core.Events;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Soulseek;

namespace Sockseek.Core.Services;

public sealed class SearchSession
{
    private readonly object admissionGate = new();
    private readonly List<SearchRawResult> rawResults = [];
    private TimeProvider timeProvider;
    private int _revision;
    private int _lockedFileCount;
    private long _sequence;
    private int _isComplete;

    public Guid JobId { get; }
    public string QueryText { get; }
    internal ConcurrentDictionary<string, (SearchResponse Response, Soulseek.File File)> Results { get; } = new();

    public int ResultCount => Results.Count;
    public int Revision => Volatile.Read(ref _revision);
    public int LockedFileCount => Volatile.Read(ref _lockedFileCount);
    public bool IsComplete => Volatile.Read(ref _isComplete) != 0;

    public event Action<SearchRawResult>? RawResultReceived;
    public event Action<SearchRawResult>? RawResultAdded;
    public event Action<SearchResultsAddedChange>? ResultsAdded;
    public event Action<SearchCompletedChange>? SearchCompleted;
    public event Action<CoreChange>? ChangePublished;
    public event Action? Completed;
    internal event Action<string, Exception>? ObserverFailed;

    public SearchSession()
        : this(Guid.Empty)
    {
    }

    public SearchSession(Guid jobId, TimeProvider? timeProvider = null, string? queryText = null)
    {
        JobId = jobId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        QueryText = queryText ?? "";
    }

    internal void ConfigureTimeProvider(TimeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (admissionGate)
        {
            if (_revision != 0 || _isComplete != 0)
                throw new InvalidOperationException("The search clock must be configured before results or completion.");
            timeProvider = provider;
        }
    }

    internal IReadOnlyCollection<(SearchResponse Response, Soulseek.File File)> Snapshot()
        => Results.Values.ToList();

    public IReadOnlyList<SearchRawResult> RawSnapshot(long afterSequence = 0)
    {
        lock (admissionGate)
            return rawResults
                .Where(x => x.Sequence > afterSequence)
                .ToList();
    }

    public async IAsyncEnumerable<SearchRawResult> ReadRawResultsAsync(
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<SearchRawResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void OnRawResultAdded(SearchRawResult result)
        {
            if (result.Sequence > afterSequence)
                channel.Writer.TryWrite(result);
        }

        void OnCompleted()
            => channel.Writer.TryComplete();

        RawResultAdded += OnRawResultAdded;
        Completed += OnCompleted;

        try
        {
            long lastYielded = afterSequence;
            foreach (var result in RawSnapshot(afterSequence))
            {
                if (result.Sequence <= lastYielded)
                    continue;

                lastYielded = result.Sequence;
                yield return result;
            }

            if (IsComplete)
                channel.Writer.TryComplete();

            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var result))
                {
                    if (result.Sequence <= lastYielded)
                        continue;

                    lastYielded = result.Sequence;
                    yield return result;
                }
            }
        }
        finally
        {
            RawResultAdded -= OnRawResultAdded;
            Completed -= OnCompleted;
            channel.Writer.TryComplete();
        }
    }

    internal void AddResponse(SearchResponse response)
    {
        lock (admissionGate)
        {
            if (_isComplete != 0)
                return;

            _lockedFileCount += response.LockedFileCount;

            if (response.Files.Count == 0)
                return;

            var added = new List<SearchResultSnapshot>();
            int revision = _revision;
            foreach (var file in response.Files)
            {
                if (Results.TryAdd(response.Username + '\\' + file.Filename, (response, file)))
                {
                    revision = ++_revision;
                    long sequence = ++_sequence;
                    var rawResult = new SearchRawResult(sequence, revision, response, file, timeProvider.GetUtcNow());
                    rawResults.Add(rawResult);
                    added.Add(CoreSnapshotFactory.CreateSearchResult(rawResult));

                    InvokeObservers(RawResultReceived, rawResult, nameof(RawResultReceived));
                    InvokeObservers(RawResultAdded, rawResult, nameof(RawResultAdded));
                }
            }

            if (added.Count > 0)
            {
                Publish(new SearchResultsAddedChange(
                    CoreChangeSequencer.Next(),
                    timeProvider.GetUtcNow(),
                    JobId,
                    revision,
                    SnapshotCollections.Freeze(added)));
            }
        }
    }

    public void Complete()
    {
        lock (admissionGate)
        {
            if (_isComplete != 0)
                return;

            _isComplete = 1;
            int completionRevision = ++_revision;
            Publish(new SearchCompletedChange(
                CoreChangeSequencer.Next(),
                timeProvider.GetUtcNow(),
                JobId,
                completionRevision,
                QueryText,
                Results.Count,
                _lockedFileCount));
            InvokeObservers(Completed, nameof(Completed));
        }
    }

    private void Publish(CoreChange change)
    {
        switch (change)
        {
            case SearchResultsAddedChange added:
                InvokeObservers(ResultsAdded, added, nameof(ResultsAdded));
                break;
            case SearchCompletedChange completed:
                InvokeObservers(SearchCompleted, completed, nameof(SearchCompleted));
                break;
        }

        InvokeObservers(ChangePublished, change, nameof(ChangePublished));
    }

    private void InvokeObservers<T>(Action<T>? observers, T value, string eventName)
    {
        if (observers == null)
            return;

        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try
            {
                observer(value);
            }
            catch (Exception ex)
            {
                LogObserverFailure(eventName, ex);
            }
        }
    }

    private void InvokeObservers(Action? observers, string eventName)
    {
        if (observers == null)
            return;

        foreach (Action observer in observers.GetInvocationList())
        {
            try
            {
                observer();
            }
            catch (Exception ex)
            {
                LogObserverFailure(eventName, ex);
            }
        }
    }

    private void LogObserverFailure(string eventName, Exception exception)
    {
        if (ObserverFailed is null)
            return;

        foreach (Action<string, Exception> observer in ObserverFailed.GetInvocationList())
        {
            try { observer(eventName, exception); }
            catch { /* Observer-failure reporting must not affect search domain work. */ }
        }
    }
}
