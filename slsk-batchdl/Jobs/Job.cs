using Enums;
using Models;
using Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jobs
{
    public abstract class Job : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public Guid Id { get; } = Guid.NewGuid();

        private Config? _config;
        public Config Config
        {
            get => _config!;
            set { if (_config != value) { _config = value; OnPropertyChanged(); } }
        }

        private JobState _state = JobState.Pending;
        public JobState State
        {
            get => _state;
            set { if (_state != value) { _state = value; OnPropertyChanged(); } }
        }

        // Transient init fields — set by extractors, consumed and cleared by JobPreparer
        public FileConditions? ExtractorCond     { get; set; }
        public FileConditions? ExtractorPrefCond { get; set; }
        public bool            EnablesIndexByDefault { get; set; }

        // Display / identity
        public string? ItemName           { get; set; }
        public string? SubItemName        { get; set; }

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

        // Type-specific contract
        public abstract bool OutputsDirectory { get; }

        // Subclasses declare their default; callers can override with CanBeSkippedOverride.
        protected abstract bool DefaultCanBeSkipped { get; }
        public bool? CanBeSkippedOverride { get; set; }
        public bool  CanBeSkipped => CanBeSkippedOverride ?? DefaultCanBeSkipped;

        // Every job has a primary query used for display and key computation.
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
