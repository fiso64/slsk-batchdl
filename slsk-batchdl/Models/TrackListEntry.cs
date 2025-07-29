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
        public string? subItemName = null;
        public string? itemName = null;

        public Config config = null!;
        public FileConditions? extractorCond = null;
        public FileConditions? extractorPrefCond = null;
        public M3uEditor? playlistEditor = null;
        public M3uEditor? indexEditor = null;
        public TrackSkipper? outputDirSkipper = null;
        public TrackSkipper? musicDirSkipper = null;

        public bool CanParallelSearch => source.Type == TrackType.Album || source.Type == TrackType.Aggregate;

        public string DefaultFolderName()
        {
            return Path.Join((itemName ?? "").ReplaceInvalidChars(" ").Trim(), (subItemName ?? "").ReplaceInvalidChars(" ").Trim());
        }

        public string DefaultPlaylistName()
        {
            var name = itemName != null ? itemName : source.Type != TrackType.Normal ? source.ToString(true) : "playlist";
            return $"_{name.ReplaceInvalidChars(" ").Trim()}.m3u8";
        }

        public string ItemNameOrSource()
        {
            return itemName != null ? itemName : source.Type != TrackType.Normal ? source.ToString(true) : "";
        }

        private List<string>? printLines = null;

        public TrackListEntry(Track source)
        {
            this.source = source;
            SetDefaults();
        }

        public TrackListEntry(TrackType trackType)
        {
            list = new List<List<Track>>();
            this.source = new Track() { Type = trackType };
            SetDefaults();
        }

        public TrackListEntry(Track source, TrackListEntry? other = null)
        {
            list = new List<List<Track>>();
            this.source = source;
            SetDefaults();

            if (other != null)
            {
                itemName = other.itemName;
                subItemName = other.subItemName;
                enablesIndexByDefault = other.enablesIndexByDefault;
                extractorPrefCond = other.extractorPrefCond;
                extractorCond = other.extractorCond;
                config = other.config;
                outputDirSkipper = other.outputDirSkipper;
                musicDirSkipper = other.musicDirSkipper;
                playlistEditor = other.playlistEditor;
                indexEditor = other.indexEditor;
            }
        }

        public TrackListEntry(List<List<Track>> list, Track source)
        {
            this.list = list;
            this.source = source;
            SetDefaults();
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
                Logger.Info(line);
            printLines = null;
        }
    }
}
