
using Sldl.Core.Jobs;

namespace Sldl.Core.Models;
    public class FolderConditions
    {
        public int MinTrackCount { get; set; } = -1;  // -1 = no constraint
        public int MaxTrackCount { get; set; } = -1;  // -1 = no constraint
        public List<string> RequiredTrackTitles { get; set; } = [];

        public FolderConditions() { }

        public FolderConditions(FolderConditions other)
        {
            MinTrackCount = other.MinTrackCount;
            MaxTrackCount = other.MaxTrackCount;
            RequiredTrackTitles = [.. other.RequiredTrackTitles];
        }

        public FolderConditions AddConditions(FolderConditions mod)
        {
            var undo = new FolderConditions();

            if (mod.MinTrackCount != -1)
            {
                undo.MinTrackCount = MinTrackCount;
                MinTrackCount      = mod.MinTrackCount;
            }
            if (mod.MaxTrackCount != -1)
            {
                undo.MaxTrackCount = MaxTrackCount;
                MaxTrackCount      = mod.MaxTrackCount;
            }
            if (mod.RequiredTrackTitles.Count > 0)
            {
                undo.RequiredTrackTitles = [.. RequiredTrackTitles];
                AddRequiredTrackTitles(mod.RequiredTrackTitles);
            }

            return undo;
        }

        public bool TrackCountSatisfies(int count)
        {
            if (MinTrackCount == -1 && MaxTrackCount == -1) return true;
            if (MaxTrackCount != -1 && count > MaxTrackCount) return false;
            if (MinTrackCount  >  0 && count < MinTrackCount) return false;
            return true;
        }

        public void AddRequiredTrackTitle(string title)
        {
            if (title.Length > 0 && !RequiredTrackTitles.Contains(title))
                RequiredTrackTitles.Add(title);
        }

        public void AddRequiredTrackTitles(IEnumerable<string> titles)
        {
            foreach (var title in titles)
                AddRequiredTrackTitle(title);
        }

        public bool RequiredTrackTitlesSatisfy(IEnumerable<SongJob> files)
        {
            if (RequiredTrackTitles.Count == 0)
                return true;

            var fileList = files.ToList();
            var cond = new FileConditions { StrictTitle = true };
            return RequiredTrackTitles.All(title => fileList.Any(file => file.ResolvedTarget != null
                && cond.StrictTitleSatisfies(file.ResolvedTarget.Filename, title)));
        }
    }
