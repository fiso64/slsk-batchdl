using Enums;

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
        public string? defaultFolderName = null;
        public FileConditions? additionalConds = null;
        public FileConditions? additionalPrefConds = null;

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

        public TrackListEntry(List<List<Track>> list, Track source, bool needSourceSearch = false, bool sourceCanBeSkipped = false,
            bool needSkipExistingAfterSearch = false, bool gotoNextAfterSearch = false, string? defaultFoldername = null)
        {
            this.list = list;
            this.source = source;
            this.needSourceSearch = needSourceSearch;
            this.sourceCanBeSkipped = sourceCanBeSkipped;
            this.needSkipExistingAfterSearch = needSkipExistingAfterSearch;
            this.gotoNextAfterSearch = gotoNextAfterSearch;
            this.defaultFolderName = defaultFoldername;
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
    }
}
