using Enums;

namespace Models
{
    public class TrackLists
    {
        public List<TrackListEntry> lists = new();
        public int Count => lists.Count;

        public TrackLists() { }

        public static TrackLists FromFlattened(IEnumerable<Track> flatList)
        {
            var res = new TrackLists();
            using var enumerator = flatList.GetEnumerator();

            while (enumerator.MoveNext())
            {
                var track = enumerator.Current;

                if (track.Type != TrackType.Normal)
                {
                    res.AddEntry(new TrackListEntry(track));
                }
                else
                {
                    res.AddEntry(new TrackListEntry(TrackType.Normal));
                    res.AddTrackToLast(track);

                    bool hasNext;
                    while (true)
                    {
                        hasNext = enumerator.MoveNext();
                        if (!hasNext || enumerator.Current.Type != TrackType.Normal)
                            break;
                        res.AddTrackToLast(enumerator.Current);
                    }

                    if (hasNext)
                        res.AddEntry(new TrackListEntry(enumerator.Current));
                    else break;
                }
            }

            return res;
        }

        public TrackListEntry this[int index]
        {
            get { return lists[index]; }
            set { lists[index] = value; }
        }

        public void AddEntry(TrackListEntry tle)
        {
            lists.Add(tle);
        }

        public void AddTrackToLast(Track track)
        {
            if (lists.Count == 0)
            {
                AddEntry(new TrackListEntry(new List<List<Track>> { new List<Track>() { track } }, new Track()));
                return;
            }

            int i = lists.Count - 1;

            if (lists[i].list == null)
            {
                lists[i].list = new List<List<Track>>() { new List<Track>() { track } };
                return;
            }
            else if (lists[i].list.Count == 0)
            {
                lists[i].list.Add(new List<Track>() { track });
                return;
            }

            int j = lists[i].list.Count - 1;
            lists[i].list[j].Add(track);
        }

        public void Reverse()
        {
            lists.Reverse();
            foreach (var tle in lists)
            {
                if (tle.list == null) continue;

                foreach (var ls in tle.list)
                {
                    ls.Reverse();
                }
            }
        }

        public void UpgradeListTypes(bool aggregate, bool album)
        {
            if (!aggregate && !album)
                return;

            var newLists = new List<TrackListEntry>();

            for (int i = 0; i < lists.Count; i++)
            {
                var tle = lists[i];

                if (tle.source.Type == TrackType.Album && aggregate)
                {
                    tle.source.Type = TrackType.AlbumAggregate;
                    tle.SetDefaults();
                    newLists.Add(tle);
                }
                else if (tle.source.Type == TrackType.Aggregate && album)
                {
                    tle.source.Type = TrackType.AlbumAggregate;
                    tle.SetDefaults();
                    newLists.Add(tle);
                }
                else if (tle.source.Type == TrackType.Normal && (album || aggregate))
                {
                    if (tle.list != null)
                    {
                        foreach (var track in tle.list[0])
                        {
                            if (album && aggregate)
                                track.Type = TrackType.AlbumAggregate;
                            else if (album)
                                track.Type = TrackType.Album;
                            else if (aggregate)
                                track.Type = TrackType.Aggregate;

                            var newTle = new TrackListEntry(track, tle);
                            newLists.Add(newTle);
                        }
                    }
                }
                else
                {
                    newLists.Add(tle);
                }
            }

            lists = newLists;
        }

        public void SetListEntryOptions()
        {
            foreach (var tle in lists)
            {
                if (tle.source.Type == TrackType.Aggregate || tle.source.Type == TrackType.AlbumAggregate)
                    tle.itemName = tle.source.ToString(true);
            }
        }

        public IEnumerable<Track> Flattened(bool addSources, bool addSpecialSourceTracks, bool sourcesOnly = false)
        {
            foreach (var tle in lists)
            {
                if ((addSources || sourcesOnly) && tle.source != null && tle.source.Type != TrackType.Normal)
                    yield return tle.source;
                if (!sourcesOnly && tle.list?.Count > 0 && (tle.source.Type == TrackType.Normal || addSpecialSourceTracks))
                {
                    foreach (var t in tle.list[0])
                        yield return t;
                }
            }
        }
    }
}
