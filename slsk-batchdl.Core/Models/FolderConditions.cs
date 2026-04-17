
using Sldl.Core.Jobs;

namespace Sldl.Core.Models;
    public class FolderConditions
    {
        public int MinTrackCount { get; set; } = -1;  // -1 = no constraint
        public int MaxTrackCount { get; set; } = -1;  // -1 = no constraint
        public string RequiredTrackTitle { get; set; } = "";

        public FolderConditions() { }

        public FolderConditions(FolderConditions other)
        {
            MinTrackCount = other.MinTrackCount;
            MaxTrackCount = other.MaxTrackCount;
            RequiredTrackTitle = other.RequiredTrackTitle;
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
            if (mod.RequiredTrackTitle.Length > 0)
            {
                undo.RequiredTrackTitle = RequiredTrackTitle;
                RequiredTrackTitle      = mod.RequiredTrackTitle;
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

        public bool RequiredTrackTitleSatisfies(IEnumerable<SongJob> files)
        {
            if (RequiredTrackTitle.Length == 0)
                return true;

            var cond = new FileConditions { StrictTitle = true };
            return files.Any(file => file.ResolvedTarget != null
                && cond.StrictTitleSatisfies(file.ResolvedTarget.Filename, RequiredTrackTitle));
        }
    }
