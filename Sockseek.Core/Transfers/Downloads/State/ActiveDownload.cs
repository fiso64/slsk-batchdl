using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Soulseek;

namespace Sockseek.Core.Transfers.Downloads.State;

// Engine-internal session object for one file download in progress.
// Holds only what the engine needs for an in-flight transfer.
// No progress bar or display logic — those belong in the CLI layer.
internal sealed class ActiveDownload
{
    public SongJob Song { get; }
    public FileCandidate Candidate { get; }
    public CancellationTokenSource Cts { get; }
    public Job? ParentJob { get; }

    // Set by the Soulseek client callbacks for live display and command handling.
    public Transfer? Transfer { get; set; }
    public bool IsManuallySkipped { get; set; }
    public bool IsStaleCancelled => StaleMaxStaleTimeMs.HasValue;
    public int? StaleMaxStaleTimeMs { get; private set; }

    public ActiveDownload(SongJob song, FileCandidate candidate, CancellationTokenSource cts, Job? parentJob = null)
    {
        Song = song;
        Candidate = candidate;
        Cts = cts;
        ParentJob = parentJob;
    }

    public void MarkStaleCancelled(int maxStaleTimeMs)
        => StaleMaxStaleTimeMs = maxStaleTimeMs;
}
