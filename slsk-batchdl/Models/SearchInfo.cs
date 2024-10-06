using System.Collections.Concurrent;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;

namespace Models
{
    public class SearchInfo
    {
        public ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results;
        public ProgressBar progress;

        public SearchInfo(ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results, ProgressBar progress)
        {
            this.results = results;
            this.progress = progress;
        }
    }
}


