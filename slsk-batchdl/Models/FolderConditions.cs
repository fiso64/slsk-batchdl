
namespace Models
{
    public class FolderConditions
    {
        public int MinTrackCount { get; set; } = -1;  // -1 = no constraint
        public int MaxTrackCount { get; set; } = -1;  // -1 = no constraint

        public FolderConditions() { }

        public FolderConditions(FolderConditions other)
        {
            MinTrackCount = other.MinTrackCount;
            MaxTrackCount = other.MaxTrackCount;
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

            return undo;
        }

        public bool TrackCountSatisfies(int count)
        {
            if (MinTrackCount == -1 && MaxTrackCount == -1) return true;
            if (MaxTrackCount != -1 && count > MaxTrackCount) return false;
            if (MinTrackCount  >  0 && count < MinTrackCount) return false;
            return true;
        }
    }
}
