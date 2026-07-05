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
    private readonly Lock rawResultsLock = new();
    private readonly List<SearchRawResult> rawResults = [];
    private int _revision;
    private int _lockedFileCount;
    private long _sequence;
    private int _isComplete;

    public Guid JobId { get; }
    public ConcurrentDictionary<string, (SearchResponse Response, Soulseek.File File)> Results { get; } = new();

    public int Revision => Volatile.Read(ref _revision);
    public int LockedFileCount => Volatile.Read(ref _lockedFileCount);
    public bool IsComplete => Volatile.Read(ref _isComplete) != 0;

    public event Action<SearchRawResult>? RawResultReceived;
    public event Action<SearchRawResult>? RawResultAdded;
    public event Action<SearchResultsAddedChange>? ResultsAdded;
    public event Action<SearchCompletedChange>? SearchCompleted;
    public event Action<CoreChange>? ChangePublished;
    public event Action? Completed;

    public SearchSession()
        : this(Guid.Empty)
    {
    }

    public SearchSession(Guid jobId)
    {
        JobId = jobId;
    }

    public IReadOnlyCollection<(SearchResponse Response, Soulseek.File File)> Snapshot()
        => Results.Values.ToList();

    public IReadOnlyList<SearchRawResult> RawSnapshot(long afterSequence = 0)
    {
        lock (rawResultsLock)
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

    public void AddResponse(SearchResponse response)
    {
        Interlocked.Add(ref _lockedFileCount, response.LockedFileCount);

        if (response.Files.Count == 0)
            return;

        var added = new List<SearchResultSnapshot>();
        int revision = Revision;
        foreach (var file in response.Files)
        {
            if (Results.TryAdd(response.Username + '\\' + file.Filename, (response, file)))
            {
                revision = Interlocked.Increment(ref _revision);
                long sequence = Interlocked.Increment(ref _sequence);
                var rawResult = new SearchRawResult(sequence, revision, response, file);
                lock (rawResultsLock)
                    rawResults.Add(rawResult);
                added.Add(CoreSnapshotFactory.CreateSearchResult(rawResult));

                RawResultReceived?.Invoke(rawResult);
                RawResultAdded?.Invoke(rawResult);
            }
        }

        if (added.Count > 0)
        {
            Publish(new SearchResultsAddedChange(
                CoreChangeSequencer.Next(),
                DateTimeOffset.UtcNow,
                JobId,
                revision,
                SnapshotCollections.Freeze(added)));
        }
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _isComplete, 1) == 0)
        {
            Publish(new SearchCompletedChange(
                CoreChangeSequencer.Next(),
                DateTimeOffset.UtcNow,
                JobId,
                Revision));
            Completed?.Invoke();
        }
    }

    private void Publish(CoreChange change)
    {
        switch (change)
        {
            case SearchResultsAddedChange added:
                ResultsAdded?.Invoke(added);
                break;
            case SearchCompletedChange completed:
                SearchCompleted?.Invoke(completed);
                break;
        }

        ChangePublished?.Invoke(change);
    }
}
