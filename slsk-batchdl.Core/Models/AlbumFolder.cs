using Sldl.Core.Jobs;

namespace Sldl.Core.Models;
    public class AlbumFolder
    {
        public string        Username   { get; }
        public string        FolderPath { get; }
        public List<SongJob> Files      { get; }

        public AlbumFolder(string username, string folderPath, List<SongJob> files)
        {
            Username   = username;
            FolderPath = folderPath;
            Files      = files;
        }
    }
