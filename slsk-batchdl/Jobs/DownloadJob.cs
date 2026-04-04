using Enums;
using Models;
using Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jobs
{
    public enum JobState
    {
        Pending    = 0,
        Searching  = 1,
        Downloading = 2,
        Done       = 3,
        Failed     = 4,
        Skipped    = 5,
    }

    public abstract class DownloadJob : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private JobState _state = JobState.Pending;
        public JobState State
        {
            get => _state;
            set { if (_state != value) { _state = value; OnPropertyChanged(); } }
        }

        // Search/download context
        public Config         Config           { get; set; } = null!;
        public FileConditions? ExtractorCond   { get; set; }
        public FileConditions? ExtractorPrefCond { get; set; }
        public M3uEditor?     PlaylistEditor   { get; set; }
        public M3uEditor?     IndexEditor      { get; set; }
        public TrackSkipper?  OutputDirSkipper { get; set; }
        public TrackSkipper?  MusicDirSkipper  { get; set; }

        // Display / identity
        public string? ItemName           { get; set; }
        public string? SubItemName        { get; set; }
        public bool    EnablesIndexByDefault { get; set; }
        public bool    PreprocessTracks   { get; set; } = true;

        // Source provenance (position in the input file)
        public int ItemNumber { get; set; } = 1;
        public int LineNumber { get; set; } = 1;

        // Job-level outcome (set after the job completes or fails)
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

        // Type-specific contract
        public abstract bool OutputsDirectory { get; }

        // Subclasses declare their default; callers can override with CanBeSkippedOverride.
        protected abstract bool DefaultCanBeSkipped { get; }
        public bool? CanBeSkippedOverride { get; set; }
        public bool  CanBeSkipped => CanBeSkippedOverride ?? DefaultCanBeSkipped;

        // Every job has a primary query used for display and key computation.
        // AlbumJob overrides this to return an AlbumQuery-based string via its own ToString().
        public abstract SongQuery QueryTrack { get; }

        private List<string>? _printLines;

        public void AddPrintLine(string line)
        {
            _printLines ??= new List<string>();
            _printLines.Add(line);
        }

        public void PrintLines()
        {
            if (_printLines == null) return;
            foreach (var line in _printLines)
                Logger.Info(line);
            _printLines = null;
        }

        public string DefaultFolderName()
        {
            return Path.Join(
                (ItemName    ?? "").ReplaceInvalidChars(" ").Trim(),
                (SubItemName ?? "").ReplaceInvalidChars(" ").Trim());
        }

        public string ItemNameOrSource() => ItemName ?? ToString(noInfo: true);

        public string DefaultPlaylistName()
        {
            var name = ItemName ?? ToString(noInfo: true);
            return $"_{name.ReplaceInvalidChars(" ").Trim()}.m3u8";
        }

        public virtual string ToString(bool noInfo) => ItemName ?? QueryTrack.ToString(noInfo);
    }
}
