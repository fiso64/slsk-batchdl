using Enums;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Models
{
    public class AlbumFolder
    {
        public string Username   { get; }
        public string FolderPath { get; }
        public List<AlbumFile> Files { get; }

        public AlbumFolder(string username, string folderPath, List<AlbumFile> files)
        {
            Username   = username;
            FolderPath = folderPath;
            Files      = files;
        }
    }

    public class AlbumFile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Metadata inferred from the remote filename (title, artist, length).
        public SongQuery Info { get; }

        // The actual Soulseek file this entry refers to.
        public FileCandidate Candidate { get; }

        // True for non-audio files inside album folders (cover art, .txt, etc.).
        public bool IsNotAudio { get; }

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

        public long FileSize => Candidate.File.Size;
        public double Progress => FileSize > 0 ? (double)BytesTransferred / FileSize : 0;

        // Updated whenever bytes transferred or transfer state changes; used for stale detection.
        public DateTime? LastActivityTime { get; set; }

        public AlbumFile(SongQuery info, FileCandidate candidate, bool isNotAudio = false)
        {
            Info       = info;
            Candidate  = candidate;
            IsNotAudio = isNotAudio;
        }
    }
}
