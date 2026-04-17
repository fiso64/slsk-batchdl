using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using System.Collections.Generic;
using System.Linq;

namespace Tests.Jobs
{
    [TestClass]
    public class JobUpgradeTests
    {
        [TestMethod]
        public void SongJob_UpgradeToAlbum_ReturnsAlbumJob()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Title" }) { ItemNumber = 5, LineNumber = 10 };
            var results = ((IUpgradeable)song).Upgrade(album: true, aggregate: false).ToList();

            Assert.AreEqual(1, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(AlbumJob));
            var album = (AlbumJob)results[0];
            Assert.AreEqual("Artist", album.Query.Artist);
            Assert.AreEqual("Album", album.Query.Album);
            Assert.AreEqual("", album.Query.SearchHint);
            Assert.IsNotNull(album.ExtractorFolderCond);
            Assert.AreEqual("Title", album.ExtractorFolderCond.RequiredTrackTitle);
            Assert.AreEqual(5, album.ItemNumber);
            Assert.AreEqual(10, album.LineNumber);
        }

        [TestMethod]
        public void SongJob_UpgradeToAggregate_ReturnsAggregateJob()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var results = ((IUpgradeable)song).Upgrade(album: false, aggregate: true).ToList();

            Assert.AreEqual(1, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(AggregateJob));
            var agg = (AggregateJob)results[0];
            Assert.AreEqual("Artist", agg.Query.Artist);
            Assert.AreEqual("Title", agg.Query.Title);
            Assert.IsNotNull(agg.ItemName);
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_CreatesNestedList()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "A1", Title = "T1" }));
            list.Add(new SongJob(new SongQuery { Artist = "A2", Title = "T2" }));

            var results = ((IUpgradeable)list).Upgrade(album: true, aggregate: false).ToList();

            Assert.AreEqual(1, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(JobList));
            var resultList = (JobList)results[0];
            Assert.AreEqual(2, resultList.Jobs.Count);
            Assert.IsInstanceOfType(resultList.Jobs[0], typeof(AlbumJob));
            Assert.IsInstanceOfType(resultList.Jobs[1], typeof(AlbumJob));
        }

        [TestMethod]
        public void JobList_UpgradeToAggregate_FlattensList()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "A1", Title = "T1" }));
            list.Add(new SongJob(new SongQuery { Artist = "A2", Title = "T2" }));

            var results = ((IUpgradeable)list).Upgrade(album: false, aggregate: true).ToList();

            Assert.AreEqual(2, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(AggregateJob));
            Assert.IsInstanceOfType(results[1], typeof(AggregateJob));
        }

        [TestMethod]
        public void JobList_MixedContent_PreservesNonUpgradeable()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "A1", Title = "T1" }));
            var ej = new ExtractJob("some-input");
            list.Add(ej);

            var results = ((IUpgradeable)list).Upgrade(album: true, aggregate: false).ToList();

            Assert.AreEqual(1, results.Count);
            var resultList = (JobList)results[0];
            Assert.AreEqual(2, resultList.Jobs.Count);
            Assert.IsInstanceOfType(resultList.Jobs[0], typeof(AlbumJob));
            Assert.AreSame(ej, resultList.Jobs[1]);
        }
    }
}
