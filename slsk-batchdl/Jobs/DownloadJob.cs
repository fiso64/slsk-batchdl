using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jobs
{
    public abstract class DownloadJob : Job
    {
        private string? _downloadPath;
        public string? DownloadPath
        {
            get => _downloadPath;
            set { if (_downloadPath != value) { _downloadPath = value; OnPropertyChanged(); } }
        }
    }
}
