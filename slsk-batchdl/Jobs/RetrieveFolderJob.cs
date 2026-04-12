using Enums;
using Models;

namespace Jobs
{
    public class RetrieveFolderJob : Job
    {
        public AlbumFolder TargetFolder { get; set; }

        public RetrieveFolderJob(AlbumFolder targetFolder)
        {
            TargetFolder = targetFolder;
        }

        public override SongQuery? QueryTrack => null;
        public override string ToString() => $"RetrieveFolderJob: {TargetFolder.FolderPath}";
        protected override bool DefaultCanBeSkipped => false;
    }
}
