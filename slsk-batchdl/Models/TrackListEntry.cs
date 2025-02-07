using Enums;
using Services;

namespace Models
{
    public class TrackListEntry
    {
        public List<List<Track>>? list;
        public Track source;
        public bool needSourceSearch = false;
        public bool sourceCanBeSkipped = false;
        public bool needSkipExistingAfterSearch = false;
        public bool gotoNextAfterSearch = false;
        public bool enablesIndexByDefault = false;
        public bool preprocessTracks = true;
        public string? defaultFolderName = null;

        public Config config = null!;
        public FileConditions? extractorCond = null;
        public FileConditions? extractorPrefCond = null;
        public M3uEditor? playlistEditor = null;
        public M3uEditor? indexEditor = null;
        public FileSkipper? outputDirSkipper = null;
        public FileSkipper? musicDirSkipper = null;

        public bool CanParallelSearch => source.Type == TrackType.Album || source.Type == TrackType.Aggregate;

        private List<string>? printLines = null;

        public TrackListEntry(TrackType trackType)
        {
            list = new List<List<Track>>();
            this.source = new Track() { Type = trackType };
            SetDefaults();
        }

        public TrackListEntry(Track source)
        {
            list = new List<List<Track>>();
            this.source = source;
            SetDefaults();
        }

        public TrackListEntry(List<List<Track>> list, Track source)
        {
            this.list = list;
            this.source = source;
            SetDefaults();
        }

        public TrackListEntry(List<List<Track>> list, Track source, Config config, bool needSourceSearch = false, bool sourceCanBeSkipped = false,
            bool needSkipExistingAfterSearch = false, bool gotoNextAfterSearch = false, string? defaultFoldername = null, bool preprocessTracks = true)
        {
            this.list = list;
            this.source = source;
            this.needSourceSearch = needSourceSearch;
            this.sourceCanBeSkipped = sourceCanBeSkipped;
            this.needSkipExistingAfterSearch = needSkipExistingAfterSearch;
            this.gotoNextAfterSearch = gotoNextAfterSearch;
            this.defaultFolderName = defaultFoldername;
            this.config = config;
            this.preprocessTracks = preprocessTracks;
        }

        public void SetDefaults()
        {
            needSourceSearch = source.Type != TrackType.Normal;
            needSkipExistingAfterSearch = source.Type == TrackType.Aggregate;
            gotoNextAfterSearch = source.Type == TrackType.AlbumAggregate;
            sourceCanBeSkipped = source.Type != TrackType.Normal
                && source.Type != TrackType.Aggregate
                && source.Type != TrackType.AlbumAggregate;
        }

        public void AddTrack(Track track)
        {
            if (list == null)
                list = new List<List<Track>>() { new List<Track>() { track } };
            else if (list.Count == 0)
                list.Add(new List<Track>() { track });
            else
                list[0].Add(track);
        }

        public void AddPrintLine(string line)
        {
            if (printLines == null)
                printLines = new List<string>();
            printLines.Add(line);
        }

        public void PrintLines()
        {
            if (printLines == null) return;
            foreach (var line in printLines)
                Console.WriteLine(line);
            printLines = null;
        }
    }
}
