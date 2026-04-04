using Enums;
using Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jobs
{
    // Per-song download state: query + search results + download progress.
    // Lives inside SongListQueryJob.Songs or AggregateQueryJob.Songs.
    public class SongJob : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public SongQuery Query { get; set; }

        // YouTube-specific display metadata (title + uploader JSON). Not a search hint.
        public string? Other { get; set; }

        // Source provenance (position in the input file). Mirrors Job.ItemNumber/LineNumber.
        public int ItemNumber { get; set; } = 1;
        public int LineNumber { get; set; } = 1;

        // Populated after search; ordered best-first. Null = not yet searched.
        public List<FileCandidate>? Candidates { get; set; }

        // The candidate actually downloaded. Set by Downloader after a successful transfer.
        public FileCandidate? ChosenCandidate { get; set; }

        private TrackState _state = TrackState.Initial;
        public TrackState State
        {
            get => _state;
            set { if (_state != value) { _state = value; OnPropertyChanged(); } }
        }

        private FailureReason _failureReason = FailureReason.None;
        public FailureReason FailureReason
        {
            get => _failureReason;
            set { if (_failureReason != value) { _failureReason = value; OnPropertyChanged(); } }
        }

        private string? _downloadPath;
        public string? DownloadPath
        {
            get => _downloadPath;
            set { if (_downloadPath != value) { _downloadPath = value; OnPropertyChanged(); } }
        }

        private long _bytesTransferred;
        public long BytesTransferred
        {
            get => _bytesTransferred;
            set
            {
                if (_bytesTransferred != value)
                {
                    _bytesTransferred = value;
                    LastActivityTime = DateTime.Now;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Progress));
                }
            }
        }

        private long _fileSize;
        public long FileSize
        {
            get => _fileSize;
            set { if (_fileSize != value) { _fileSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(Progress)); } }
        }

        public double Progress => FileSize > 0 ? (double)BytesTransferred / FileSize : 0;

        // Updated when bytes change; used for stale detection.
        public DateTime? LastActivityTime { get; set; }

        public SongJob(SongQuery query)
        {
            Query = query;
        }

        public override string ToString() => Query.ToString();
    }
}
